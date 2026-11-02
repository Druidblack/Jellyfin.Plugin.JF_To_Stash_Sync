using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using JFToStashSync.Services;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace JFToStashSync.Tasks;

/// <summary>
/// Gradually refresh Babepedia statistics for local Stash-linked Jellyfin people.
/// A bounded batch and persisted retry deferrals prevent bursts and repeated 404/name-mismatch lookups.
/// </summary>
public sealed class RefreshBabepediaActorStatsTask : IScheduledTask, IConfigurableScheduledTask
{
    private const int MaxProfileAttemptsPerRun = 100;
    private static readonly SemaphoreSlim RunGate = new(1, 1);
    private readonly ILibraryManager _library;
    private readonly BabepediaActorStatsService _stats;
    private readonly StashClient _stash;
    private readonly ILogger<RefreshBabepediaActorStatsTask> _logger;

    public RefreshBabepediaActorStatsTask(
        ILibraryManager library,
        BabepediaActorStatsService stats,
        StashClient stash,
        ILogger<RefreshBabepediaActorStatsTask> logger)
    {
        _library = library;
        _stats = stats;
        _stash = stash;
        _logger = logger;
    }

    public string Name => "Refresh Babepedia actor statistics";
    public string Description => "Fetch never-checked ratings first, then expired cache, then missing-profile (30 days) and temporary-error (1 day) retries. Up to 100 actors per run.";
    public string Category => "JF To Stash Sync";
    public bool IsHidden => false;
    public bool IsEnabled => true;
    public bool IsLogged => true;
    public string Key => "JFToStashSyncRefreshBabepediaActorStats";

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() => new[]
    {
        new TaskTriggerInfo
        {
            Type = TaskTriggerInfoType.IntervalTrigger,
            IntervalTicks = TimeSpan.FromHours(3).Ticks,
        },
    };

    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        progress.Report(0);
        var configuration = Plugin.Instance?.Configuration;
        if (configuration is null || !configuration.Enabled || !configuration.EnableBabepediaActorStats)
        {
            _logger.LogDebug("JFToStashSync: Babepedia statistics task skipped (feature is disabled).");
            progress.Report(100);
            return;
        }

        if (!_stash.IsConfigured())
        {
            _logger.LogWarning("JFToStashSync: Babepedia statistics task skipped: Stash is not configured.");
            progress.Report(100);
            return;
        }

        if (_stats.IsSiteBlocked(out var retryAt))
        {
            _logger.LogWarning("JFToStashSync: Babepedia updates paused until {RetryAt:u} after site rate limiting.", retryAt);
            progress.Report(100);
            return;
        }

        // Query only Person IDs with a Stash provider ID. Never materialize the entire
        // Person library, and do not keep Person objects alive across web requests.
        var personIds = _library.GetItemIds(new InternalItemsQuery
        {
            IncludeItemTypes = new[] { BaseItemKind.Person },
            HasAnyProviderId = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Stash"] = string.Empty,
            },
            Recursive = true,
            EnableTotalRecordCount = false,
        });

        // ID-only passes. Historical negative-cache entries remain in the last
        // queues even after their retry dates expire; they must never be mistaken
        // for actors who were never checked at all.
        await RunGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var examined = 0;
            var freshOrDeferred = 0;
            var firstFetched = 0;
            var refreshed = 0;
            var failed = 0;
            var missingProfiles = 0;
            var attempted = 0;
            var limited = false;
            var pendingChanges = false;

            try
            {
                // 0: never checked, 1: expired cache, 2: missing-profile retry,
                // 3: temporary-error retry (including legacy failed attempts).
                for (var priority = 0; priority < 4; priority++)
                {
                    foreach (var personId in personIds)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        examined++;
                        if (_stats.GetScheduledQueuePriority(personId) != priority)
                            continue;
                        if (_stats.SkipScheduledFetch(personId, configuration.BabepediaCacheDays))
                        {
                            freshOrDeferred++;
                            continue;
                        }

                        if (_library.GetItemById(personId) is not Person person ||
                            person.ProviderIds is null ||
                            !person.ProviderIds.TryGetValue("Stash", out var stashId) ||
                            string.IsNullOrWhiteSpace(stashId))
                            continue;

                        attempted++;
                        try
                        {
                            await _stats.FetchOneAsync(person.Id.ToString("N"), cancellationToken, deferPersistence: true).ConfigureAwait(false);
                            if (priority == 1) refreshed++;
                            else firstFetched++;
                            pendingChanges = true;
                        }
                        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                        {
                            throw;
                        }
                        catch (BabepediaFetchDeferredException ex)
                        {
                            limited = true;
                            _logger.LogWarning("JFToStashSync: Babepedia automatic collection paused: {Message}", ex.Message);
                            break;
                        }
                        catch (ActorProfileNotFoundException ex)
                        {
                            missingProfiles++;
                            _stats.DeferScheduledFetch(person.Id, deferPersistence: true, missingProfile: true);
                            pendingChanges = true;
                            _logger.LogDebug(ex, "JFToStashSync: Babepedia profile missing for {PersonId}; retry in 30 days.", person.Id);
                        }
                        catch (Exception ex)
                        {
                            failed++;
                            // Failed lookups are deferred for one day so later actors
                            // can make progress without a three-hour retry loop.
                            _stats.DeferScheduledFetch(person.Id, deferPersistence: true);
                            pendingChanges = true;
                            _logger.LogDebug(ex, "JFToStashSync: Babepedia lookup deferred for actor {PersonId}.", person.Id);
                        }

                        // Flush every ten attempts and again on cancellation / site block.
                        if (attempted % 10 == 0 && pendingChanges)
                        {
                            _stats.FlushScheduledCache();
                            pendingChanges = false;
                        }
                        progress.Report(attempted * 100d / MaxProfileAttemptsPerRun);
                        if (attempted >= MaxProfileAttemptsPerRun) break;
                    }
                    if (attempted >= MaxProfileAttemptsPerRun || limited) break;
                }
            }
            finally
            {
                if (pendingChanges) _stats.FlushScheduledCache();
            }

            progress.Report(100);
            _logger.LogInformation(
                "JFToStashSync: Babepedia task completed. First ratings={FirstFetched}, expired refreshed={Refreshed}, missing profiles (30d)={Missing}, temporary errors (1d)={Failed}, cached/deferred skipped={Skipped}, IDs examined={Examined}, rate-limited={RateLimited}.",
                firstFetched, refreshed, missingProfiles, failed, freshOrDeferred, examined, limited);
        }
        finally { RunGate.Release(); }
    }
}
