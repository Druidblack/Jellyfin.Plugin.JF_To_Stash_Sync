using System;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace JFToStashSync.Services;

/// <summary>
/// Handles the Jellyfin Web player O+ action and increments the matching Stash scene O-counter.
/// </summary>
public sealed class OCounterSyncService
{
    private readonly ILibraryManager _libraryManager;
    private readonly StashClient _stashClient;
    private readonly ILogger<OCounterSyncService> _logger;

    public OCounterSyncService(
        ILibraryManager libraryManager,
        StashClient stashClient,
        ILogger<OCounterSyncService> logger)
    {
        _libraryManager = libraryManager;
        _stashClient = stashClient;
        _logger = logger;
    }

    public async Task<OCounterSyncResult> IncrementByItemIdAsync(
        string? itemId,
        string? userId,
        CancellationToken ct)
    {
        var cfg = Plugin.Instance?.Configuration;
        if (cfg is null || !cfg.Enabled)
        {
            return OCounterSyncResult.Failure("JF To Stash Sync is disabled.");
        }

        if (!cfg.EnablePlayerOCounterButton)
        {
            return OCounterSyncResult.Failure("The Jellyfin Web O-counter button is disabled in plugin settings.");
        }

        if (!_stashClient.IsConfigured())
        {
            return OCounterSyncResult.Failure("Stash endpoint is not configured.");
        }

        if (string.IsNullOrWhiteSpace(userId) || !Guid.TryParse(userId.Trim(), out var parsedUserId))
        {
            return OCounterSyncResult.Failure("Could not determine the current Jellyfin user.");
        }

        if (!IsUserAllowed(parsedUserId, cfg.OnlyUserIdsCsv))
        {
            return OCounterSyncResult.Failure("This Jellyfin user is excluded by the plugin user filter.");
        }

        if (string.IsNullOrWhiteSpace(itemId) || !Guid.TryParse(itemId.Trim(), out var id))
        {
            return OCounterSyncResult.Failure("Could not determine the currently playing Jellyfin video ID.");
        }

        var item = _libraryManager.GetItemById(id);
        if (item is not Video video)
        {
            return OCounterSyncResult.Failure(
                item is null
                    ? $"Jellyfin item {id:N} was not found."
                    : $"Jellyfin item {id:N} is not a video (actual type: {item.GetType().Name}).");
        }

        var sceneId = await _stashClient.ResolveSceneIdAsync(video, ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(sceneId))
        {
            return OCounterSyncResult.Failure(
                $"No matching Stash scene was found for Jellyfin item {video.Id:N} ({video.Name}).",
                video);
        }

        var newCount = await _stashClient.IncrementOCounterAsync(sceneId, ct).ConfigureAwait(false);
        if (newCount is null)
        {
            _logger.LogWarning(
                "JFToStashSync: failed to increment Stash O-counter. itemId={ItemId} sceneId={SceneId} userId={UserId}",
                video.Id,
                sceneId,
                parsedUserId);

            return OCounterSyncResult.Failure(
                "Stash rejected the O-counter update. Check the Jellyfin log for the GraphQL error.",
                video,
                sceneId);
        }

        _logger.LogInformation(
            "JFToStashSync: Stash O-counter incremented from Jellyfin Web player. itemId={ItemId} sceneId={SceneId} userId={UserId} newCount={Count}",
            video.Id,
            sceneId,
            parsedUserId,
            newCount.Value);

        return new OCounterSyncResult
        {
            Success = true,
            ItemId = video.Id.ToString("N"),
            ItemName = video.Name ?? string.Empty,
            SceneId = sceneId,
            Count = newCount.Value,
            Message = $"Stash O-counter increased to {newCount.Value}.",
        };
    }

    private static bool IsUserAllowed(Guid userId, string csv)
    {
        if (userId == Guid.Empty)
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(csv))
        {
            return true;
        }

        var parts = csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var part in parts)
        {
            if (Guid.TryParse(part, out var allowed) && allowed == userId)
            {
                return true;
            }
        }

        return false;
    }
}

public sealed class OCounterSyncResult
{
    public bool Success { get; set; }

    public string ItemId { get; set; } = string.Empty;

    public string ItemName { get; set; } = string.Empty;

    public string SceneId { get; set; } = string.Empty;

    public int Count { get; set; }

    public string Message { get; set; } = string.Empty;

    public static OCounterSyncResult Failure(string message, Video? video = null, string? sceneId = null)
        => new()
        {
            Success = false,
            ItemId = video?.Id.ToString("N") ?? string.Empty,
            ItemName = video?.Name ?? string.Empty,
            SceneId = sceneId ?? string.Empty,
            Message = message,
        };
}
