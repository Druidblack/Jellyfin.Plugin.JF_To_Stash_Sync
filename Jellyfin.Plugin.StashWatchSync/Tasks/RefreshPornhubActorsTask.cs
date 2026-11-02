using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using JFToStashSync.Services;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace JFToStashSync.Tasks;

/// <summary>
/// Bounded scheduled collection for Stash-linked actors. Neither browsing Jellyfin
/// nor rendering portraits makes a Pornhub request. Statistics and artwork each
/// have independent cache policies and persistent retry deferrals.
/// </summary>
public sealed class RefreshPornhubActorsTask : IScheduledTask, IConfigurableScheduledTask
{
    private const int MaxActorsPerRun = 100;
    private static readonly SemaphoreSlim RunGate = new(1, 1);
    private readonly ILibraryManager _library;
    private readonly StashClient _stash;
    private readonly PornhubActorArtworkService _artwork;
    private readonly PornhubActorStatsService _stats;
    private readonly PornhubScheduledStateService _state;
    private readonly ILogger<RefreshPornhubActorsTask> _logger;

    public RefreshPornhubActorsTask(ILibraryManager library, StashClient stash,
        PornhubActorArtworkService artwork, PornhubActorStatsService stats,
        PornhubScheduledStateService state, ILogger<RefreshPornhubActorsTask> logger)
    {
        _library = library;
        _stash = stash;
        _artwork = artwork;
        _stats = stats;
        _state = state;
        _logger = logger;
    }

    public string Name => "Refresh Pornhub actor data";
    public string Description => "Fetch never-checked ratings first, expired cache second, missing-profile (30 days) and temporary-error (1 day) retries later; artwork independently. At most 100 profiles per run.";
    public string Category => "JF To Stash Sync";
    public string Key => "JFToStashSyncRefreshPornhubActorData";
    public bool IsHidden => false;
    public bool IsEnabled => true;
    public bool IsLogged => true;

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() => new[]
    {
        new TaskTriggerInfo { Type = TaskTriggerInfoType.IntervalTrigger,
            IntervalTicks = TimeSpan.FromHours(3).Ticks }
    };

    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken ct)
    {
        progress.Report(0);
        var cfg = Plugin.Instance?.Configuration;
        if (cfg is null || !cfg.Enabled ||
            (!cfg.PornhubScheduledFetchStats && !cfg.PornhubScheduledFetchArtwork))
        {
            progress.Report(100);
            return;
        }
        if (!_stash.IsConfigured())
        {
            _logger.LogWarning("JFToStashSync: Pornhub scheduled task skipped: Stash is not configured.");
            progress.Report(100);
            return;
        }

        await RunGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_artwork.IsSiteBlocked(out var blockedUntil))
            {
                _logger.LogWarning("JFToStashSync: Pornhub requests temporarily paused until {RetryAt:u}.", blockedUntil);
                progress.Report(100);
                return;
            }

            // ID-only filtered query: do not load every Person into memory and do
            // not hold Person instances while later profiles are fetched.
            var ids = _library.GetItemIds(new InternalItemsQuery
            {
                IncludeItemTypes = new[] { BaseItemKind.Person },
                HasAnyProviderId = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["Stash"] = string.Empty,
                },
                Recursive = true,
                EnableTotalRecordCount = false,
            });
            var attempted = 0;
            var firstFetched = 0;
            var refreshedStats = 0;
            var savedImages = 0;
            var failed = 0;
            var missingProfiles = 0;
            var paused = false;
            // A profile may change classification when it is fetched or deferred.
            // Do not count/process it twice in the same scheduled run.
            var processed = new HashSet<Guid>();

            // Priority 0: never attempted; 1: expired successful cache;
            // 2: missing profile due after 30 days; 3: transient error due after 1 day;
            // 4: artwork-only (unchanged independent policy). No Person list in RAM.
            var lastPriority = cfg.PornhubScheduledFetchArtwork ? 4 : 3;
            for (var priority = 0; priority <= lastPriority; priority++)
            {
                foreach (var id in ids)
                {
                    ct.ThrowIfCancellationRequested();
                    if (processed.Contains(id)) continue;
                    var key = id.ToString("N");
                    var needsStats = cfg.PornhubScheduledFetchStats &&
                        !_stats.IsFresh(key, cfg.PornhubStatsCacheDays) &&
                        !_state.IsDeferred(key, statistics: true);
                    var hasStats = _stats.HasCachedRecord(key);
                    var statsPriority = _state.GetStatsQueuePriority(key, hasStats);
                    if (priority < 4 && (!needsStats || statsPriority != priority)) continue;
                    if (priority == 4 && needsStats) continue;
                    // A known absent actor must not trigger artwork-only lookups
                    // repeatedly while its 30-day negative cache is active.
                    if (priority == 4 && _state.IsMissingProfileDeferred(key)) continue;

                    Person? person = null;
                    var logo = false;
                    var banner = false;
                    if (cfg.PornhubScheduledFetchArtwork)
                    {
                        person = _library.GetItemById(id) as Person;
                        if (person is not null)
                            (logo, banner) = _state.GetDueArtwork(person,
                                cfg.PornhubArtworkUpdateMode, cfg.PornhubArtworkCacheDays);
                    }
                    if (!needsStats && !logo && !banner) continue;
                    processed.Add(id);
                    attempted++;

                    if (needsStats)
                    {
                        try
                        {
                            await _stats.FetchOneAsync(key, force: false, ct).ConfigureAwait(false);
                            if (hasStats) refreshedStats++;
                            else firstFetched++;
                            _state.ClearDefer(key, statistics: true);
                        }
                        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                        catch (ActorProfileNotFoundException ex)
                        {
                            missingProfiles++;
                            _state.Defer(key, statistics: true, missingProfile: true);
                            _logger.LogDebug(ex, "JFToStashSync: Pornhub profile missing for {PersonId}; retry in 30 days.", key);
                        }
                        catch (Exception ex)
                        {
                            failed++;
                            _state.Defer(key, statistics: true);
                            _logger.LogDebug(ex, "JFToStashSync: Pornhub temporary statistics failure for {PersonId}; retry in 1 day.", key);
                        }
                    }
                    if (_artwork.IsSiteBlocked(out _)) { paused = true; break; }
                    // Do not re-request the same known-missing profile for artwork.
                    if (_state.IsMissingProfileDeferred(key))
                    {
                        progress.Report(attempted * 100d / MaxActorsPerRun);
                        if (attempted >= MaxActorsPerRun) break;
                        continue;
                    }

                    if (logo || banner)
                    {
                        try
                        {
                            var saved = await _artwork.RefreshScheduledArtworkAsync(key, logo, banner,
                                type => { _state.RecordImageSaved(key, type); return Task.CompletedTask; }, ct).ConfigureAwait(false);
                            savedImages += (saved.LogoSaved ? 1 : 0) + (saved.BannerSaved ? 1 : 0);
                            _state.ClearDefer(key, statistics: false);
                            if ((logo && !saved.LogoSaved) || (banner && !saved.BannerSaved))
                                _state.Defer(key, statistics: false);
                        }
                        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                        catch (Exception ex)
                        {
                            failed++;
                            _state.Defer(key, statistics: false);
                            _logger.LogDebug(ex, "JFToStashSync: Pornhub artwork deferred for {PersonId}.", key);
                        }
                    }
                    if (_artwork.IsSiteBlocked(out _)) { paused = true; break; }

                    progress.Report(attempted * 100d / MaxActorsPerRun);
                    if (attempted >= MaxActorsPerRun) break;
                }
                if (attempted >= MaxActorsPerRun || paused) break;
            }
            progress.Report(100);
            _logger.LogInformation("JFToStashSync: Pornhub task completed. Actors checked={Attempted}, first ratings={FirstFetched}, expired refreshed={Refreshed}, images saved={Images}, missing profiles (30d)={Missing}, temporary errors (1d)={Failed}, rate-limited={Paused}.",
                attempted, firstFetched, refreshedStats, savedImages, missingProfiles, failed, paused);
        }
        finally { RunGate.Release(); }
    }
}
