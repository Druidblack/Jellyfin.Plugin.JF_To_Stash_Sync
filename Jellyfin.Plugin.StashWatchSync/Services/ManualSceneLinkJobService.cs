using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace JFToStashSync.Services;

/// <summary>
/// Runs the long manual scene-link workflow independently from the lifetime of the
/// Jellyfin Web HTTP request. A metadata refresh may rebuild the details page and abort
/// the browser request; the server-side job must continue in that case.
/// </summary>
public sealed class ManualSceneLinkJobService
{
    private static readonly TimeSpan JobTimeout = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan CompletedJobRetention = TimeSpan.FromMinutes(30);

    private readonly JellyfinUrlSyncService _syncService;
    private readonly ILogger<ManualSceneLinkJobService> _logger;
    private readonly object _gate = new();
    private readonly Dictionary<string, JobEntry> _jobs = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _activeJobByItem = new(StringComparer.OrdinalIgnoreCase);

    public ManualSceneLinkJobService(
        JellyfinUrlSyncService syncService,
        ILogger<ManualSceneLinkJobService> logger)
    {
        _syncService = syncService;
        _logger = logger;
    }

    public ManualSceneLinkJobStatus StartOrGet(string? itemId)
    {
        var normalizedItemId = NormalizeItemId(itemId);
        if (string.IsNullOrWhiteSpace(normalizedItemId))
        {
            return ManualSceneLinkJobStatus.Invalid("Invalid Jellyfin item ID.");
        }

        JobEntry entry;
        lock (_gate)
        {
            CleanupExpiredJobsLocked();

            if (_activeJobByItem.TryGetValue(normalizedItemId, out var existingJobId)
                && _jobs.TryGetValue(existingJobId, out var existing)
                && existing.State == ManualSceneLinkJobState.Running)
            {
                return SnapshotLocked(existing);
            }

            entry = new JobEntry
            {
                JobId = Guid.NewGuid().ToString("N"),
                ItemId = normalizedItemId,
                State = ManualSceneLinkJobState.Running,
                StartedUtc = DateTimeOffset.UtcNow,
            };

            _jobs[entry.JobId] = entry;
            _activeJobByItem[normalizedItemId] = entry.JobId;
        }

        _ = Task.Run(() => RunJobAsync(entry));
        return Get(entry.JobId) ?? ManualSceneLinkJobStatus.Invalid("Could not start the manual Stash scene-link job.");
    }

    public ManualSceneLinkJobStatus? Get(string? jobId)
    {
        if (string.IsNullOrWhiteSpace(jobId))
        {
            return null;
        }

        lock (_gate)
        {
            CleanupExpiredJobsLocked();
            return _jobs.TryGetValue(jobId.Trim(), out var entry)
                ? SnapshotLocked(entry)
                : null;
        }
    }

    public ManualSceneLinkJobStatus? GetActiveForItem(string? itemId)
    {
        var normalizedItemId = NormalizeItemId(itemId);
        if (string.IsNullOrWhiteSpace(normalizedItemId))
        {
            return null;
        }

        lock (_gate)
        {
            CleanupExpiredJobsLocked();
            if (!_activeJobByItem.TryGetValue(normalizedItemId, out var jobId)
                || !_jobs.TryGetValue(jobId, out var entry)
                || entry.State != ManualSceneLinkJobState.Running)
            {
                return null;
            }

            return SnapshotLocked(entry);
        }
    }

    private async Task RunJobAsync(JobEntry entry)
    {
        ManualSceneLinkResult result;
        var state = ManualSceneLinkJobState.Failed;

        try
        {
            using var timeout = new CancellationTokenSource(JobTimeout);
            result = await _syncService
                .ResolveAndLinkSceneByItemIdAsync(entry.ItemId, timeout.Token)
                .ConfigureAwait(false);
            state = result.Success ? ManualSceneLinkJobState.Succeeded : ManualSceneLinkJobState.Failed;
        }
        catch (OperationCanceledException)
        {
            result = ManualSceneLinkResult.Failure(
                $"Manual Stash scene linking timed out after {(int)JobTimeout.TotalMinutes} minutes.");
            _logger.LogWarning(
                "JFToStashSync: manual Stash scene-link job timed out. jobId={JobId} itemId={ItemId}",
                entry.JobId,
                entry.ItemId);
        }
        catch (Exception ex)
        {
            result = ManualSceneLinkResult.Failure("Manual Stash scene linking failed: " + ex.Message);
            _logger.LogError(
                ex,
                "JFToStashSync: unhandled error in manual Stash scene-link job. jobId={JobId} itemId={ItemId}",
                entry.JobId,
                entry.ItemId);
        }

        lock (_gate)
        {
            entry.Result = result;
            entry.State = state;
            entry.CompletedUtc = DateTimeOffset.UtcNow;

            if (_activeJobByItem.TryGetValue(entry.ItemId, out var activeJobId)
                && string.Equals(activeJobId, entry.JobId, StringComparison.OrdinalIgnoreCase))
            {
                _activeJobByItem.Remove(entry.ItemId);
            }
        }
    }

    private void CleanupExpiredJobsLocked()
    {
        var cutoff = DateTimeOffset.UtcNow - CompletedJobRetention;
        var expiredIds = new List<string>();

        foreach (var pair in _jobs)
        {
            var entry = pair.Value;
            if (entry.State != ManualSceneLinkJobState.Running
                && entry.CompletedUtc.HasValue
                && entry.CompletedUtc.Value < cutoff)
            {
                expiredIds.Add(pair.Key);
            }
        }

        foreach (var jobId in expiredIds)
        {
            _jobs.Remove(jobId);
        }
    }

    private static ManualSceneLinkJobStatus SnapshotLocked(JobEntry entry)
    {
        var result = entry.Result;
        return new ManualSceneLinkJobStatus
        {
            Valid = true,
            JobId = entry.JobId,
            ItemId = entry.ItemId,
            State = entry.State,
            IsRunning = entry.State == ManualSceneLinkJobState.Running,
            IsCompleted = entry.State != ManualSceneLinkJobState.Running,
            Success = entry.State == ManualSceneLinkJobState.Succeeded,
            SceneId = result?.SceneId ?? string.Empty,
            Message = result?.Message ?? (entry.State == ManualSceneLinkJobState.Running
                ? "Manual Stash scene linking is running."
                : string.Empty),
            Result = result,
        };
    }

    private static string NormalizeItemId(string? itemId)
    {
        if (string.IsNullOrWhiteSpace(itemId) || !Guid.TryParse(itemId.Trim(), out var id))
        {
            return string.Empty;
        }

        return id.ToString("N");
    }

    private sealed class JobEntry
    {
        public string JobId { get; init; } = string.Empty;
        public string ItemId { get; init; } = string.Empty;
        public string State { get; set; } = ManualSceneLinkJobState.Running;
        public DateTimeOffset StartedUtc { get; init; }
        public DateTimeOffset? CompletedUtc { get; set; }
        public ManualSceneLinkResult? Result { get; set; }
    }
}

public static class ManualSceneLinkJobState
{
    public const string Running = "running";
    public const string Succeeded = "succeeded";
    public const string Failed = "failed";
}

public sealed class ManualSceneLinkJobStatus
{
    public bool Valid { get; set; }
    public string JobId { get; set; } = string.Empty;
    public string ItemId { get; set; } = string.Empty;
    public string State { get; set; } = string.Empty;
    public bool IsRunning { get; set; }
    public bool IsCompleted { get; set; }
    public bool Success { get; set; }
    public string SceneId { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public ManualSceneLinkResult? Result { get; set; }

    public static ManualSceneLinkJobStatus Invalid(string message)
        => new()
        {
            Message = message,
        };
}
