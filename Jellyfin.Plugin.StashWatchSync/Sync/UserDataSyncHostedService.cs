using System;
using System.Collections.Concurrent;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using StashWatchSync.Services;

namespace StashWatchSync.Sync;

/// <summary>
/// Background service that listens to Jellyfin user data changes and syncs play state to Stash.
/// </summary>
public sealed class UserDataSyncHostedService : IHostedService, IDisposable
{
    private readonly IUserDataManager _userDataManager;
    private readonly ILogger<UserDataSyncHostedService> _logger;
    private readonly StashClient _stashClient;
    private readonly ISessionManager _sessionManager;

    private readonly ConcurrentDictionary<string, SyncState> _state = new();

    private CancellationTokenSource? _cts;
    private Task? _flushLoopTask;

    public UserDataSyncHostedService(
        IUserDataManager userDataManager,
        ISessionManager sessionManager,
        StashClient stashClient,
        ILogger<UserDataSyncHostedService> logger)
    {
        _userDataManager = userDataManager;
        _sessionManager = sessionManager;
        _stashClient = stashClient;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _userDataManager.UserDataSaved += OnUserDataSaved;
        _sessionManager.PlaybackStart += OnPlaybackStart;
        _sessionManager.PlaybackProgress += OnPlaybackProgress;
        _sessionManager.PlaybackStopped += OnPlaybackStopped;
        _logger.LogInformation("StashWatchSync: subscribed to UserDataSaved and playback events");

        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _flushLoopTask = Task.Run(() => FlushLoopAsync(_cts.Token), CancellationToken.None);

        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _userDataManager.UserDataSaved -= OnUserDataSaved;
        _sessionManager.PlaybackStart -= OnPlaybackStart;
        _sessionManager.PlaybackProgress -= OnPlaybackProgress;
        _sessionManager.PlaybackStopped -= OnPlaybackStopped;
        _logger.LogInformation("StashWatchSync: unsubscribed from UserDataSaved and playback events");

        try
        {
            _cts?.Cancel();
        }
        catch
        {
            // ignore
        }

        if (_flushLoopTask is not null)
        {
            try
            {
                await _flushLoopTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // expected
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "StashWatchSync: flush loop stopped with error");
            }
        }
    }

    public void Dispose()
    {
        _userDataManager.UserDataSaved -= OnUserDataSaved;
        _sessionManager.PlaybackStart -= OnPlaybackStart;
        _sessionManager.PlaybackProgress -= OnPlaybackProgress;
        _sessionManager.PlaybackStopped -= OnPlaybackStopped;
    }




private void OnPlaybackStart(object? sender, PlaybackProgressEventArgs e)
    => _ = HandlePlaybackProgressAsync(e, isStart: true);

private void OnPlaybackProgress(object? sender, PlaybackProgressEventArgs e)
    => _ = HandlePlaybackProgressAsync(e, isStart: false);

private void OnPlaybackStopped(object? sender, PlaybackStopEventArgs e)
    => _ = HandlePlaybackStoppedAsync(e);

private async Task HandlePlaybackProgressAsync(PlaybackProgressEventArgs e, bool isStart)
{
    try
    {
        var cfg = Plugin.Instance?.Configuration;
        if (cfg is null || !cfg.Enabled)
        {
            return;
        }

        if (!_stashClient.IsConfigured())
        {
            return;
        }

        // We only track duration if enabled; played sync uses the user-data hook.
        if (!cfg.SyncPlayDuration)
        {
            return;
        }

        if (e.Item is not Video item)
        {
            return;
        }

        var userId = TryGetUserId(e);
        if (userId is null)
        {
            return;
        }

        // Optional user filter.
        if (!IsUserAllowed(userId.Value, cfg.OnlyUserIdsCsv))
        {
            return;
        }

        string key = userId.Value.ToString("N", CultureInfo.InvariantCulture) + ":" + item.Id.ToString("N", CultureInfo.InvariantCulture);
        var state = _state.GetOrAdd(key, _ => new SyncState());

        // Cache identifying info for future resolution.
        if (item.ProviderIds is not null && item.ProviderIds.TryGetValue("Stash", out var stashProviderId) && !string.IsNullOrWhiteSpace(stashProviderId))
        {
            state.ProviderId = stashProviderId;
            state.SceneId ??= stashProviderId;
        }

        state.ItemPath = item.Path ?? state.ItemPath;
        state.ItemName = item.Name ?? state.ItemName;

        var nowUtc = DateTime.UtcNow;
        state.LastActivityUtc = nowUtc;

        var posTicks = e.PlaybackPositionTicks ?? 0L;

        if (isStart)
        {
            // Start a fresh baseline for this playback session.
            state.SessionStartUtc = nowUtc;
            state.SessionStartTicks = posTicks;
            state.LastPositionTicks = posTicks;
            state.LastPositionSeenUtc = nowUtc;
            return;
        }

        UpdateWatchedTimeAccumulator(state, posTicks, nowUtc);

	        // Keep this handler as an async Task (it is invoked via fire-and-forget) while still
	        // avoiding CS1998 when the logic is fully in-memory.
	        await Task.CompletedTask;
    }
    catch (Exception ex)
    {
        _logger.LogDebug(ex, "StashWatchSync: playback progress handler error");
    }
}

private async Task HandlePlaybackStoppedAsync(PlaybackStopEventArgs e)
{
    try
    {
        var cfg = Plugin.Instance?.Configuration;
        if (cfg is null || !cfg.Enabled)
        {
            return;
        }

        if (!_stashClient.IsConfigured())
        {
            return;
        }

        // We rely on watched-time tracking to decide whether a playback session is meaningful.
        // This tracking is part of the play_duration feature.
        if (!cfg.SyncPlayDuration)
        {
            return;
        }

        if (e.Item is not Video item)
        {
            return;
        }

        var userId = TryGetUserId(e);
        if (userId is null)
        {
            return;
        }

        // Optional user filter.
        if (!IsUserAllowed(userId.Value, cfg.OnlyUserIdsCsv))
        {
            return;
        }

        string key = userId.Value.ToString("N", CultureInfo.InvariantCulture) + ":" + item.Id.ToString("N", CultureInfo.InvariantCulture);
        var state = _state.GetOrAdd(key, _ => new SyncState());

        // Cache identifying info for future resolution.
        if (item.ProviderIds is not null && item.ProviderIds.TryGetValue("Stash", out var stashProviderId) && !string.IsNullOrWhiteSpace(stashProviderId))
        {
            state.ProviderId = stashProviderId;
            state.SceneId ??= stashProviderId;
        }

        state.ItemPath = item.Path ?? state.ItemPath;
        state.ItemName = item.Name ?? state.ItemName;

        var nowUtc = DateTime.UtcNow;
        state.LastActivityUtc = nowUtc;

        var posTicks = e.PlaybackPositionTicks ?? 0L;

        // Update accumulator one last time.
        UpdateWatchedTimeAccumulator(state, posTicks, nowUtc);

        // Eligibility threshold (avoid counting a "play" when a user immediately stops).
        var minMeaningfulSeconds = Math.Max(1, cfg.InProgressMinWatchedSecondsToSync);
        if (state.SessionWatchedSecondsTotal < minMeaningfulSeconds)
        {
            state.ResetWatchSession();
            return;
        }

        // Resolve scene id if needed.
        var sceneId = state.SceneId;
        if (string.IsNullOrWhiteSpace(sceneId))
        {
            sceneId = await _stashClient.ResolveSceneIdAsync(state.ProviderId, state.ItemPath, CancellationToken.None).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(sceneId))
            {
                state.SceneId = sceneId;
            }
        }

        if (string.IsNullOrWhiteSpace(sceneId))
        {
            state.ResetWatchSession();
            return;
        }

        // 1) Increment play count ONCE per playback session (even if the item is not marked Played in Jellyfin).
        // Play-count syncing is always enabled (UI option removed).
        {
            var incOk = await _stashClient.IncrementPlayCountOnlyAsync(sceneId, 1, CancellationToken.None).ConfigureAwait(false);
            if (!incOk)
            {
                _logger.LogWarning("StashWatchSync: failed to increment play count on playback stop. sceneId={SceneId}", sceneId);
            }
            else
            {
                _logger.LogInformation("StashWatchSync: incremented play count on playback stop. sceneId={SceneId}", sceneId);
            }
        }

        // 2) Flush any remaining unsent watched seconds to play_duration (if enabled).
        if (cfg.SyncInProgressPlayDuration)
        {
            var pendingSeconds = state.SessionWatchedSeconds;
            if (pendingSeconds >= 0.5 && !state.DurationSyncInFlight)
            {
                state.DurationSyncInFlight = true;
                try
                {
                    var ok = await _stashClient.AddPlayDurationAsync(sceneId, pendingSeconds, CancellationToken.None).ConfigureAwait(false);
                    if (ok)
                    {
                        _logger.LogInformation(
                            "StashWatchSync: flushed in-progress play_duration to Stash on playback stop. sceneId={SceneId} seconds={Seconds:F1}",
                            sceneId, pendingSeconds);

                        state.SessionWatchedSeconds = 0;
                        state.LastDurationSentUtc = nowUtc;
                    }
                }
                finally
                {
                    state.DurationSyncInFlight = false;
                }
            }
        }

        // Reset session accumulator after a playback stop (we counted this session already).
        state.ResetWatchSession();
    }
    catch (Exception ex)
    {
        _logger.LogDebug(ex, "StashWatchSync: playback stopped handler error");
    }
}


private static Guid? TryGetUserId(object args)
{
    // Prefer args.UserId if it exists.
    var userIdObj = GetPropertyValue(args, "UserId");
    if (userIdObj is Guid g)
    {
        return g;
    }

    if (userIdObj is string s && Guid.TryParse(s, out var gs))
    {
        return gs;
    }

    // Try args.Session.UserId.
    var sessionObj = GetPropertyValue(args, "Session");
    if (sessionObj is null)
    {
        return null;
    }

    var sessionUserIdObj = GetPropertyValue(sessionObj, "UserId");
    if (sessionUserIdObj is Guid sg)
    {
        return sg;
    }

    if (sessionUserIdObj is string ss && Guid.TryParse(ss, out var sgs))
    {
        return sgs;
    }

    return null;
}

private static object? GetPropertyValue(object obj, string name)
{
    try
    {
        var prop = obj.GetType().GetProperty(name);
        return prop?.GetValue(obj);
    }
    catch
    {
        return null;
    }
}

    private async Task FlushLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var cfg = Plugin.Instance?.Configuration;
                if (cfg is not null
                    && cfg.Enabled
                    && _stashClient.IsConfigured()
                    && cfg.SyncPlayDuration
                    && cfg.SyncInProgressPlayDuration)
                {
                    var nowUtc = DateTime.UtcNow;

                    foreach (var kv in _state)
                    {
                        if (ct.IsCancellationRequested)
                        {
                            break;
                        }

                        var state = kv.Value;

                        if (state.DurationSyncInFlight)
                        {
                            continue;
                        }

                        // Only flush meaningful deltas.
                        if (state.SessionWatchedSeconds < Math.Max(1, cfg.InProgressMinWatchedSecondsToSync))
                        {
                            continue;
                        }

                        var lastSeenUtc = state.LastPositionSeenUtc != DateTime.MinValue ? state.LastPositionSeenUtc : state.LastActivityUtc;
                        if (lastSeenUtc == DateTime.MinValue)
                        {
                            continue;
                        }

                        var idleSeconds = (nowUtc - lastSeenUtc).TotalSeconds;

                        var inactivityThreshold = Math.Max(1, cfg.InProgressInactivityFlushSeconds);
                        var minIntervalSeconds = Math.Max(1, cfg.InProgressMinIntervalSeconds);

                        bool idleFlush = idleSeconds >= inactivityThreshold;
                        bool intervalFlush = (nowUtc - state.LastDurationSentUtc).TotalSeconds >= minIntervalSeconds;

                        // Send either when we detect playback has stopped (idle), or periodically (interval).
                        if (!idleFlush && !intervalFlush)
                        {
                            continue;
                        }

                        state.DurationSyncInFlight = true;

                        try
                        {
                            // Resolve scene id (cached providerId/path are stored in state).
                            var sceneId = state.SceneId;
                            if (string.IsNullOrWhiteSpace(sceneId))
                            {
                                sceneId = await _stashClient.ResolveSceneIdAsync(state.ProviderId, state.ItemPath, ct).ConfigureAwait(false);
                                if (!string.IsNullOrWhiteSpace(sceneId))
                                {
                                    state.SceneId = sceneId;
                                }
                            }

                            if (string.IsNullOrWhiteSpace(sceneId))
                            {
                                continue;
                            }

                            var sendSeconds = state.SessionWatchedSeconds;
                            if (sendSeconds <= 0.5)
                            {
                                continue;
                            }

                            // Add the currently accumulated watched seconds to Stash play_duration.
                            var ok = await _stashClient.AddPlayDurationAsync(sceneId!, sendSeconds, ct).ConfigureAwait(false);
                            if (ok)
                            {
                                // Reset pending seconds so we don't double-count on the next sync.
                                state.SessionWatchedSeconds = Math.Max(0, state.SessionWatchedSeconds - sendSeconds);
                                state.LastDurationSentUtc = nowUtc;

                                // If playback is considered stopped, reset the position baseline so the next session starts cleanly.
                                if (idleFlush)
                                {
                                    state.LastPositionTicks = -1;
                                    state.LastPositionSeenUtc = DateTime.MinValue;
                                }
                            }
                        }
                        finally
                        {
                            state.DurationSyncInFlight = false;
                        }
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // expected on shutdown
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "StashWatchSync: flush loop error");
            }

            var cfg2 = Plugin.Instance?.Configuration;
            var interval = Math.Max(1, cfg2?.BackgroundFlushIntervalSeconds ?? 10);

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(interval), ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // shutdown
            }
        }
    }

    private void OnUserDataSaved(object? sender, UserDataSaveEventArgs e)
    {
        // Fire-and-forget, but keep it async-safe.
        _ = HandleAsync(e);
    }

    private async Task HandleAsync(UserDataSaveEventArgs e)
    {
        var cfg = Plugin.Instance?.Configuration;
        if (cfg is null || !cfg.Enabled || !_stashClient.IsConfigured())
        {
            return;
        }

        // Jellyfin's UserDataSaveEventArgs exposes UserId (Guid), not a full User object.
        // (Some older Emby-derived APIs had a User property, but Jellyfin does not.)
        var userId = e.UserId;
        if (userId == Guid.Empty)
        {
            return;
        }

        // Optional user filter.
        if (!IsUserAllowed(userId, cfg.OnlyUserIdsCsv))
        {
            return;
        }

        // We only care about videos (Movie/Episode/etc.).
        if (e.Item is not Video item)
        {
            return;
        }

        var itemName = item.Name ?? string.Empty;
        var itemPath = item.Path ?? string.Empty;

        var ud = e.UserData;
        if (ud is null)
        {
            return;
        }

        string key = userId.ToString("N", CultureInfo.InvariantCulture) + ":" + item.Id.ToString("N", CultureInfo.InvariantCulture);
        var state = _state.GetOrAdd(key, _ => new SyncState());

        // Cache identifying info for background flush.
        if (item.ProviderIds is not null && item.ProviderIds.TryGetValue("Stash", out var stashProviderId) && !string.IsNullOrWhiteSpace(stashProviderId))
        {
            state.ProviderId = stashProviderId;
            state.SceneId ??= stashProviderId;
        }

        state.ItemPath = itemPath;
        state.ItemName = itemName;

        var nowUtc = DateTime.UtcNow;
        state.LastActivityUtc = nowUtc;

        // Track real watched time (best-effort) by looking at how the playback position changes.
        // This runs for both "in-progress" playback and the final Played=true save.
        if (cfg.SyncPlayDuration)
        {
            UpdateWatchedTimeAccumulator(state, ud.PlaybackPositionTicks, nowUtc);

            // For in-progress sessions, try to resolve the scene id early so the background flush can send updates.
            if (cfg.SyncInProgressPlayDuration
                && string.IsNullOrWhiteSpace(state.SceneId)
                && state.SessionWatchedSeconds > 0.5
                && (state.LastSceneResolveAttemptUtc == DateTime.MinValue || (nowUtc - state.LastSceneResolveAttemptUtc).TotalSeconds >= 30))
            {
                state.LastSceneResolveAttemptUtc = nowUtc;
                var resolved = await _stashClient.ResolveSceneIdAsync(item, CancellationToken.None).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(resolved))
                {
                    state.SceneId = resolved;
                }
            }
        }

        // 1) Played -> mark watched in Stash. (Always enabled; UI option removed.)
        if (ud.Played)
        {
            _logger.LogDebug(
                "StashWatchSync: Played=true. userId={UserId} itemId={ItemId} name={Name} playCount={PlayCount} path={Path}",
                userId,
                item.Id,
                itemName,
                ud.PlayCount,
                itemPath);

            bool shouldSend = !state.LastPlayed || ud.PlayCount != state.LastPlayCount;
            if (shouldSend)
            {
                var sceneId = await _stashClient.ResolveSceneIdAsync(item, CancellationToken.None).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(sceneId))
                {
                    // We sync play_count per playback session using playback stop events.
// When Jellyfin flips Played=true, we only ensure the scene is marked watched in Stash (resume_time=0),
// and we optionally add any remaining unsent watched seconds to play_duration.
double? playedDurationSeconds = null;
if (cfg.SyncPlayDuration)
{
    playedDurationSeconds = GetAndFinalizeWatchedSeconds(state, item, nowUtc);
}

var ok = await _stashClient.SyncPlayedAsync(sceneId!, playCountDelta: 0, playedDurationSeconds, CancellationToken.None).ConfigureAwait(false);
                    if (ok)
                    {
                        _logger.LogInformation(
                            "StashWatchSync: synced watched to Stash. sceneId={SceneId} userId={UserId} itemId={ItemId} name={Name}",
                            sceneId,
                            userId,
                            item.Id,
                            itemName);

                        state.LastPlayed = true;
                        state.LastPlayCount = ud.PlayCount;
                        state.LastResumeSeconds = 0;
                        state.LastResumeSentUtc = nowUtc;

                        // Reset session accumulator after a successful sync.
                        state.ResetWatchSession();
                    }
                    else
                    {
                        _logger.LogWarning(
                            "StashWatchSync: failed to sync watched to Stash. sceneId={SceneId} userId={UserId} itemId={ItemId} name={Name}",
                            sceneId,
                            userId,
                            item.Id,
                            itemName);
                    }
                }
                else
                {
                    var hasProviderId = item.ProviderIds is not null && item.ProviderIds.ContainsKey("Stash");
                    _logger.LogWarning(
                        "StashWatchSync: could not resolve Stash scene for played item. userId={UserId} itemId={ItemId} name={Name} path={Path} hasProviderId={HasProviderId} pathFallback={PathFallback} jfPrefix={JfPrefix} stashPrefix={StashPrefix} fullPath={FullPath}",
                        userId,
                        item.Id,
                        itemName,
                        itemPath,
                        hasProviderId,
                        cfg.EnablePathFallback,
                        cfg.JellyfinPathPrefix,
                        cfg.StashPathPrefix,
                        cfg.SearchByFullPath);
                }
            }

            return;
        }

        // 2) Resume position.
        if (cfg.SyncResumePosition)
        {
            // Don't push resume updates for played items.
            if (ud.Played)
            {
                return;
            }

            double resumeSeconds = ud.PlaybackPositionTicks / (double)TimeSpan.TicksPerSecond;
            if (resumeSeconds <= 0)
            {
                return;
            }

            var now = DateTime.UtcNow;
            var minInterval = TimeSpan.FromSeconds(Math.Max(0, cfg.MinResumeIntervalSeconds));
            var minDelta = Math.Max(0, cfg.MinResumeDeltaSeconds);

            if (now - state.LastResumeSentUtc < minInterval)
            {
                return;
            }

            if (Math.Abs(resumeSeconds - state.LastResumeSeconds) < minDelta)
            {
                return;
            }

            var sceneId = await _stashClient.ResolveSceneIdAsync(item, CancellationToken.None).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(sceneId))
            {
                return;
            }

            var ok = await _stashClient.SyncResumeAsync(sceneId!, resumeSeconds, CancellationToken.None).ConfigureAwait(false);
            if (ok)
            {
                state.LastResumeSeconds = resumeSeconds;
                state.LastResumeSentUtc = now;
                state.LastPlayed = false;
                state.LastPlayCount = ud.PlayCount;
            }
        }
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
        foreach (var p in parts)
        {
            if (Guid.TryParse(p, out var g) && g == userId)
            {
                return true;
            }
        }

        return false;
    }

    private sealed class SyncState
    {
        public bool LastPlayed { get; set; }
        public int LastPlayCount { get; set; }
        public double LastResumeSeconds { get; set; }
        public DateTime LastResumeSentUtc { get; set; } = DateTime.MinValue;

        // Cached identity info for background flush.
        public string? SceneId { get; set; }
        public string? ProviderId { get; set; }
        public string? ItemPath { get; set; }
        public string? ItemName { get; set; }
        public DateTime LastActivityUtc { get; set; } = DateTime.MinValue;
        public DateTime LastSceneResolveAttemptUtc { get; set; } = DateTime.MinValue;

        // In-progress duration sync state.
        public DateTime LastDurationSentUtc { get; set; } = DateTime.MinValue;
        public bool DurationSyncInFlight { get; set; } = false;

        // Real watched-time tracking (best-effort).
        public long SessionStartTicks { get; set; } = -1;
        public DateTime SessionStartUtc { get; set; } = DateTime.MinValue;

        public long LastPositionTicks { get; set; } = -1;
        public DateTime LastPositionSeenUtc { get; set; } = DateTime.MinValue;
        public double SessionWatchedSeconds { get; set; } = 0;
        public double SessionWatchedSecondsTotal { get; set; } = 0;

        public void ResetWatchSession()
        {
            SessionStartTicks = -1;
            SessionStartUtc = DateTime.MinValue;
            LastPositionTicks = -1;
            LastPositionSeenUtc = DateTime.MinValue;
            SessionWatchedSeconds = 0;
            SessionWatchedSecondsTotal = 0;
            DurationSyncInFlight = false;
        }
    }

    /// <summary>
    /// Update the per-item accumulator with a best-effort estimate of watched time.
    /// We compare playback position deltas with wall-clock elapsed time to avoid counting seeks as watched time.
    /// </summary>
    
private static void UpdateWatchedTimeAccumulator(SyncState state, long playbackPositionTicks, DateTime nowUtc)
{
    if (playbackPositionTicks < 0)
    {
        playbackPositionTicks = 0;
    }

    // If we don't have a baseline yet, establish it.
    if (state.LastPositionTicks < 0 || state.LastPositionSeenUtc == DateTime.MinValue)
    {
        state.LastPositionTicks = playbackPositionTicks;
        state.LastPositionSeenUtc = nowUtc;

        if (state.SessionStartUtc == DateTime.MinValue)
        {
            state.SessionStartUtc = nowUtc;
            state.SessionStartTicks = playbackPositionTicks;
        }

        return;
    }

    // If the clock went backwards, just reset the baseline.
    if (nowUtc < state.LastPositionSeenUtc)
    {
        state.LastPositionTicks = playbackPositionTicks;
        state.LastPositionSeenUtc = nowUtc;
        state.SessionStartUtc = nowUtc;
        state.SessionStartTicks = playbackPositionTicks;
        return;
    }

    var elapsedWallSeconds = (nowUtc - state.LastPositionSeenUtc).TotalSeconds;
    if (elapsedWallSeconds <= 0)
    {
        state.LastPositionTicks = playbackPositionTicks;
        state.LastPositionSeenUtc = nowUtc;
        return;
    }

    // If there is a large gap, treat it as a new playback session and do not count the jump.
    const double maxContinuousGapSeconds = 120.0;
    if (elapsedWallSeconds > maxContinuousGapSeconds)
    {
        state.LastPositionTicks = playbackPositionTicks;
        state.LastPositionSeenUtc = nowUtc;
        state.SessionStartUtc = nowUtc;
        state.SessionStartTicks = playbackPositionTicks;
        return;
    }

    var deltaTicks = playbackPositionTicks - state.LastPositionTicks;
    state.LastPositionTicks = playbackPositionTicks;
    state.LastPositionSeenUtc = nowUtc;

    if (deltaTicks <= 0)
    {
        // Seek backwards or reset; don't count.
        return;
    }

    var deltaPosSeconds = deltaTicks / (double)TimeSpan.TicksPerSecond;

    // Heuristic limits:
    // - allow up to 2.5x playback speed
    // - small tolerance for coarse timer saves
    const double maxSpeedFactor = 2.5;
    const double toleranceSeconds = 2.0;

    var maxCredible = (elapsedWallSeconds * maxSpeedFactor) + toleranceSeconds;
    var add = Math.Min(deltaPosSeconds, maxCredible);

    // Ignore ultra-tiny increments.
    if (add >= 0.25)
    {
        state.SessionWatchedSeconds += add;
        state.SessionWatchedSecondsTotal += add;
    }
}

/// <summary>
/// Finalize watched time for a Played=true save.
/// Tries to also estimate the "tail" (from last saved position to end) when possible.
/// </summary> for a Played=true save.
    /// Tries to also estimate the "tail" (from last saved position to end) when possible.
    /// </summary>
    private static double? GetAndFinalizeWatchedSeconds(SyncState state, Video item, DateTime nowUtc)
    {
        var watched = Math.Max(0, state.SessionWatchedSeconds);

        // Best-effort tail estimation:
        // When Jellyfin flips Played=true, it may also reset playback position to 0.
        // If we have a recent last position, estimate the remaining watched seconds up to runtime.
        var rtTicks = Convert.ToInt64(item.RunTimeTicks);
        if (rtTicks > 0 && state.LastPositionTicks > 0 && state.LastPositionSeenUtc != DateTime.MinValue)
        {
            var runtimeSeconds = rtTicks / (double)TimeSpan.TicksPerSecond;
            var lastPosSeconds = state.LastPositionTicks / (double)TimeSpan.TicksPerSecond;

            if (runtimeSeconds > 0 && lastPosSeconds > 0)
            {
                // Only trust tail estimation if the last position sample is recent.
                var sinceLast = (nowUtc - state.LastPositionSeenUtc).TotalSeconds;
                if (sinceLast >= 0 && sinceLast <= 600)
                {
                    var remaining = Math.Max(0, runtimeSeconds - lastPosSeconds);
                    if (remaining > 0)
                    {
                        const double maxSpeedFactor = 2.5;
                        const double toleranceSeconds = 2.0;
                        var maxCredible = (sinceLast * maxSpeedFactor) + toleranceSeconds;
                        watched += Math.Min(remaining, maxCredible);
                    }
                }
            }
        }

        // If we have nothing meaningful, don't update play_duration.
        return watched > 0.5 ? watched : null;
    }
}
