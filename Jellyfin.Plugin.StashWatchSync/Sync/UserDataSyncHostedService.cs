using System;
using System.Collections;
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
        _cts?.Dispose();
    }




private void OnPlaybackStart(object? sender, PlaybackProgressEventArgs e)
    => _ = HandlePlaybackProgressAsync(e, isStart: true);

private void OnPlaybackProgress(object? sender, PlaybackProgressEventArgs e)
    => _ = HandlePlaybackProgressAsync(e, isStart: false);

private void OnPlaybackStopped(object? sender, PlaybackStopEventArgs e)
    => _ = HandlePlaybackStoppedAsync(e);

private CancellationToken ServiceToken => _cts?.Token ?? CancellationToken.None;

private async Task HandlePlaybackProgressAsync(PlaybackProgressEventArgs e, bool isStart)
{
    try
    {
        var nowUtc = DateTime.UtcNow;

        var cfg = Plugin.Instance?.Configuration;
        if (cfg is null || !cfg.Enabled)
        {
            return;
        }

        if (!_stashClient.IsConfigured())
        {
            return;
        }

        // Resume sync uses UserDataSaved; the start event also reopens the resume window.
        // Do not require play-duration sync for this step.
        if (e.Item is Video startedVideo && isStart && cfg.SyncResumePosition)
        {
            var startUserId = TryGetUserId(e, cfg.ResumeUserId);
            if (startUserId is not null && IsUserAllowed(startUserId.Value, cfg.OnlyUserIdsCsv))
            {
                var startKey = startUserId.Value.ToString("N", CultureInfo.InvariantCulture) + ":" + startedVideo.Id.ToString("N", CultureInfo.InvariantCulture);
                var startState = _state.GetOrAdd(startKey, _ => new SyncState());
                startState.LastResumeStopUtc = DateTime.MinValue;
                startState.LastActivityUtc = nowUtc;
            }
        }

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
        var nowUtc = DateTime.UtcNow;

        var cfg = Plugin.Instance?.Configuration;
        if (cfg is null || !cfg.Enabled)
        {
            return;
        }

        if (!_stashClient.IsConfigured())
        {
            return;
        }

        // Resume position must work independently of play-duration tracking and its
        // minimum-watched-time threshold. In Jellyfin 12 the stop event contains the
        // authoritative final playback position; don't depend on a separate user-data save.
        if (!cfg.SyncResumePosition && !cfg.SyncPlayDuration)
        {
            return;
        }

        if (e.Item is not Video item)
        {
            return;
        }

        var userId = TryGetUserId(e, cfg.SyncResumePosition ? cfg.ResumeUserId : null);
        if (userId is null)
        {
            _logger.LogWarning("StashWatchSync: playback stopped but no user ID was found. itemId={ItemId}", item.Id);
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

        state.LastActivityUtc = nowUtc;

        // UserDataSaved handlers can finish after the stop event. Mark it immediately,
        // so an older progress event cannot overwrite the final Stash resume position.
        if (cfg.SyncResumePosition)
        {
            state.LastResumeStopUtc = nowUtc;
            await SyncResumeOnPlaybackStopAsync(item, userId.Value, e, state, cfg).ConfigureAwait(false);
        }

        // The remaining logic is ONLY for play_duration and play_count. Neither its
        // threshold nor its scene lookup must prevent writing the resume position.
        if (!cfg.SyncPlayDuration)
        {
            return;
        }

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
            sceneId = await _stashClient.ResolveSceneIdAsync(state.ProviderId, state.ItemPath, ServiceToken).ConfigureAwait(false);
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
            var incOk = await _stashClient.IncrementPlayCountOnlyAsync(sceneId, 1, ServiceToken).ConfigureAwait(false);
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
            if (pendingSeconds >= 0.5 && Interlocked.CompareExchange(ref state.DurationSyncInFlight, 1, 0) == 0)
            {
                try
                {
                    var ok = await _stashClient.AddPlayDurationAsync(sceneId, pendingSeconds, ServiceToken).ConfigureAwait(false);
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
                    Interlocked.Exchange(ref state.DurationSyncInFlight, 0);
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


private static Guid? TryGetUserId(object args, string? preferredUserId = null)
{
    Guid? preferred = Guid.TryParse(preferredUserId, out var selected) && selected != Guid.Empty
        ? selected : null;

    // Jellyfin 12 playback events expose Users, not necessarily a top-level UserId.
    // Avoid selecting an unrelated user from a multi-user playback session.
    var users = GetPropertyValue(args, "Users");
    if (users is IEnumerable entries)
    {
        Guid? first = null;
        foreach (var entry in entries)
        {
            if (entry is null)
            {
                continue;
            }

            var id = GetGuid(entry) ?? GetGuid(GetPropertyValue(entry, "Id"));
            if (id is null || id == Guid.Empty)
            {
                continue;
            }

            first ??= id;
            if (preferred is not null && preferred == id)
            {
                return id;
            }
        }

        if (first is not null)
        {
            return first;
        }
    }

    return GetGuid(GetPropertyValue(args, "UserId"))
        ?? GetGuid(GetPropertyValue(GetPropertyValue(args, "Session") ?? args, "UserId"));
}

private static Guid? GetGuid(object? value)
{
    if (value is Guid g && g != Guid.Empty)
    {
        return g;
    }

    if (value is string s && Guid.TryParse(s, out var parsed) && parsed != Guid.Empty)
    {
        return parsed;
    }

    return null;
}

private async Task SyncResumeOnPlaybackStopAsync(
    Video item,
    Guid userId,
    PlaybackStopEventArgs e,
    SyncState state,
    StashWatchSync.Configuration.PluginConfiguration cfg)
{
    if (!string.IsNullOrWhiteSpace(cfg.ResumeUserId))
    {
        if (!Guid.TryParse(cfg.ResumeUserId.Trim(), out var allowedId))
        {
            _logger.LogWarning("StashWatchSync: resume sync skipped: invalid ResumeUserId in settings");
            return;
        }

        if (allowedId != userId)
        {
            _logger.LogDebug("StashWatchSync: resume sync skipped for user {UserId}: different user selected in settings", userId);
            return;
        }
    }

    var completed = TryGetBoolProperty(e, "PlayedToCompletion") == true;
    var ticks = e.PlaybackPositionTicks;

    // A real completion clears the resume point. Some playback clients report
    // zero or null on an ordinary stop; those must NOT clear a valid Stash position.
    if (!completed && (ticks is null || ticks <= 0))
    {
        _logger.LogWarning(
            "StashWatchSync: resume sync skipped on stop: missing/zero playback position. itemId={ItemId} userId={UserId}",
            item.Id, userId);
        return;
    }

    double seconds = completed ? 0d : ticks!.Value / (double)TimeSpan.TicksPerSecond;
    if (!double.IsFinite(seconds) || seconds < 0
        || (item.RunTimeTicks is long runtime && runtime > 0 && seconds > runtime / (double)TimeSpan.TicksPerSecond + 1))
    {
        _logger.LogWarning("StashWatchSync: resume sync skipped: invalid stop position for item {ItemId}: {Seconds}", item.Id, seconds);
        return;
    }

    await state.ResumeSyncGate.WaitAsync(ServiceToken).ConfigureAwait(false);
    try
    {
        var sceneId = state.SceneId;
        if (string.IsNullOrWhiteSpace(sceneId))
        {
            sceneId = await _stashClient.ResolveSceneIdAsync(item, ServiceToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(sceneId))
            {
                _logger.LogWarning("StashWatchSync: resume sync skipped: Stash scene not found for Jellyfin item {ItemId} path={Path}", item.Id, item.Path);
                return;
            }

            state.SceneId = sceneId;
        }

        var ok = await _stashClient.SyncResumeAsync(sceneId!, seconds, ServiceToken, verify: true).ConfigureAwait(false);
        if (ok)
        {
            state.LastResumeSeconds = seconds;
            state.LastResumeSentUtc = DateTime.UtcNow;
            state.LastPlayed = completed;
            _logger.LogInformation(
                "StashWatchSync: resume synced after playback stop. itemId={ItemId} sceneId={SceneId} userId={UserId} seconds={Seconds:F2} completed={Completed}",
                item.Id, sceneId, userId, seconds, completed);
        }
        else
        {
            _logger.LogWarning(
                "StashWatchSync: resume sync FAILED on playback stop. itemId={ItemId} sceneId={SceneId} userId={UserId} seconds={Seconds:F2}",
                item.Id, sceneId, userId, seconds);
        }
    }
    finally
    {
        state.ResumeSyncGate.Release();
    }
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

private static bool? TryGetBoolProperty(object obj, params string[] names)
{
    foreach (var name in names)
    {
        var v = GetPropertyValue(obj, name);
        // Nullable<bool> values are boxed either as 'bool' (when HasValue)
        // or as 'null' (when !HasValue). So we only need to handle 'bool'.
        if (v is bool b)
        {
            return b;
        }
    }
    return null;
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

                        // Bound memory usage: old item/user states are not useful forever.
                        if (state.LastActivityUtc != DateTime.MinValue
                            && nowUtc - state.LastActivityUtc > TimeSpan.FromHours(24)
                            && state.SessionWatchedSeconds < 0.5
                            && Volatile.Read(ref state.DurationSyncInFlight) == 0)
                        {
                            _state.TryRemove(kv.Key, out _);
                            continue;
                        }

                        if (Volatile.Read(ref state.DurationSyncInFlight) != 0)
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

                        if (Interlocked.CompareExchange(ref state.DurationSyncInFlight, 1, 0) != 0)
                        {
                            continue;
                        }

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
                            Interlocked.Exchange(ref state.DurationSyncInFlight, 0);
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
        _ = HandleUserDataSavedSafeAsync(e);
    }

    private async Task HandleUserDataSavedSafeAsync(UserDataSaveEventArgs e)
    {
        try
        {
            await HandleAsync(e).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ServiceToken.IsCancellationRequested)
        {
            // Normal during server/plugin shutdown.
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "StashWatchSync: user-data handler error");
        }
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

        var ud = e.UserData;
        if (ud is null)
        {
            return;
        }

        var nowUtc = DateTime.UtcNow;

        // 0) Person favorites -> Stash performer favorites.
        if (cfg.SyncPerformerFavorites && e.Item is Person person)
        {
            if (person.ProviderIds is null
                || !person.ProviderIds.TryGetValue("Stash", out var stashPerformerId)
                || string.IsNullOrWhiteSpace(stashPerformerId))
            {
                // To avoid accidental matches, we only sync when a Stash provider id exists on the Jellyfin person.
                return;
            }

            string favKey = userId.ToString("N", CultureInfo.InvariantCulture) + ":" + person.Id.ToString("N", CultureInfo.InvariantCulture);
            var favState = _state.GetOrAdd(favKey, _ => new SyncState());

            var isFavorite = ud.IsFavorite;

            // Only act on changes.
            if (favState.LastIsFavorite.HasValue && favState.LastIsFavorite.Value == isFavorite)
            {
                return;
            }

            // Guard against multiple quick saves producing duplicate Stash calls.
            if (favState.LastFavoriteSentUtc != DateTime.MinValue && (nowUtc - favState.LastFavoriteSentUtc).TotalSeconds < 2)
            {
                favState.LastIsFavorite = isFavorite;
                return;
            }

            var ok = await _stashClient.SetPerformerFavoriteAsync(stashPerformerId, isFavorite, ServiceToken).ConfigureAwait(false);
            if (ok)
            {
                _logger.LogInformation(
                    "StashWatchSync: synced performer favorite to Stash. performerId={PerformerId} favorite={Favorite} userId={UserId} itemId={ItemId} name={Name}",
                    stashPerformerId,
                    isFavorite,
                    userId,
                    person.Id,
                    person.Name ?? string.Empty);
            }
            else
            {
                _logger.LogWarning(
                    "StashWatchSync: failed to sync performer favorite to Stash. performerId={PerformerId} favorite={Favorite} userId={UserId} itemId={ItemId} name={Name}",
                    stashPerformerId,
                    isFavorite,
                    userId,
                    person.Id,
                    person.Name ?? string.Empty);
            }

            favState.LastIsFavorite = isFavorite;
            favState.LastFavoriteSentUtc = nowUtc;
            return;
        }

        // We only care about videos (Movie/Episode/etc.).
        if (e.Item is not Video item)
        {
            return;
        }

        var itemName = item.Name ?? string.Empty;
        var itemPath = item.Path ?? string.Empty;

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
                var resolved = await _stashClient.ResolveSceneIdAsync(item, ServiceToken).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(resolved))
                {
                    state.SceneId = resolved;
                }
            }
        }

        // 0) Favorite videos -> set Stash rating (optional).
        if (cfg.SyncFavoriteToRating)
        {
            var isFavorite = TryGetBoolProperty(ud, "IsFavorite", "IsFavourite");
            if (isFavorite is not null)
            {
                var saveReason = GetPropertyValue(e, "SaveReason")?.ToString() ?? string.Empty;
                var reasonLooksLikeFavorite = saveReason.IndexOf("favorite", StringComparison.OrdinalIgnoreCase) >= 0
                    || saveReason.IndexOf("favour", StringComparison.OrdinalIgnoreCase) >= 0;

                // Avoid accidental overwrites: if we have no baseline yet, only act on Favorite=true (safe)
                // or when the save reason explicitly looks like a favorite toggle.
                var shouldConsider = reasonLooksLikeFavorite || isFavorite.Value || state.LastIsFavorite is not null;

                if (shouldConsider && (state.LastIsFavorite is null || state.LastIsFavorite.Value != isFavorite.Value || reasonLooksLikeFavorite))
                {
                    var sceneIdForFav = await _stashClient.ResolveSceneIdAsync(item, ServiceToken).ConfigureAwait(false);
                    var rating = isFavorite.Value ? 5 : 0;

                    if (!string.IsNullOrWhiteSpace(sceneIdForFav))
                    {
                        var ok = await _stashClient.SetSceneRatingAsync(sceneIdForFav!, rating, ServiceToken).ConfigureAwait(false);
                        if (ok)
                        {
                            _logger.LogInformation(
                                "StashWatchSync: updated scene rating from favorite. sceneId={SceneId} rating={Rating} userId={UserId} itemId={ItemId} name={Name}",
                                sceneIdForFav, rating, userId, item.Id, itemName);
                        }
                        else
                        {
                            _logger.LogWarning(
                                "StashWatchSync: failed to update scene rating from favorite. sceneId={SceneId} rating={Rating} userId={UserId} itemId={ItemId} name={Name}",
                                sceneIdForFav, rating, userId, item.Id, itemName);
                        }
                    }

                    state.LastIsFavorite = isFavorite.Value;
                }
                else if (state.LastIsFavorite is null)
                {
                    // Establish baseline without writing to Stash.
                    state.LastIsFavorite = isFavorite.Value;
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
                var sceneId = await _stashClient.ResolveSceneIdAsync(item, ServiceToken).ConfigureAwait(false);
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

var ok = await _stashClient.SyncPlayedAsync(sceneId!, playCountDelta: 0, playedDurationSeconds, ServiceToken).ConfigureAwait(false);
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

        // 2) Resume position: only the saved Jellyfin position is used, not elapsed watch time.
        // Stash has a single scene-wide resume point, so administrators may select one source user.
        if (cfg.SyncResumePosition)
        {
            // The final stop is synchronized directly from PlaybackStopEventArgs.
            // A delayed UserDataSaved callback must not overwrite the new final point.
            if (state.LastResumeStopUtc != DateTime.MinValue
                && DateTime.UtcNow - state.LastResumeStopUtc < TimeSpan.FromSeconds(10))
            {
                return;
            }

            if (!string.IsNullOrWhiteSpace(cfg.ResumeUserId))
            {
                if (!Guid.TryParse(cfg.ResumeUserId.Trim(), out var resumeUserId))
                {
                    _logger.LogWarning("StashWatchSync: invalid ResumeUserId in plugin settings; resume sync skipped");
                    return;
                }

                if (resumeUserId != userId)
                {
                    return;
                }
            }

            // A finished/marked-watched item must not create a new continue-watching point.
            // The existing Played handler above clears Stash's resume point to zero.
            if (ud.Played)
            {
                return;
            }

            var resumeSeconds = ud.PlaybackPositionTicks / (double)TimeSpan.TicksPerSecond;
            if (resumeSeconds < 0 || !double.IsFinite(resumeSeconds))
            {
                return;
            }

            // Reject impossible positions rather than writing incorrect state to Stash.
            if (item.RunTimeTicks is long runTimeTicks && runTimeTicks > 0
                && ud.PlaybackPositionTicks > runTimeTicks + TimeSpan.TicksPerSecond)
            {
                return;
            }

            // Jellyfin writes UserData with reason PlaybackFinished when a session stops.
            // Always send that final saved position, even if the delta/time throttle has
            // not yet expired. A zero position is sent only on stop, never on playback start.
            var saveReason = GetPropertyValue(e, "SaveReason")?.ToString() ?? string.Empty;
            var isFinalSave = string.Equals(saveReason, "PlaybackFinished", StringComparison.OrdinalIgnoreCase)
                || string.Equals(saveReason, "PlaybackStopped", StringComparison.OrdinalIgnoreCase);

            if (resumeSeconds == 0 && !isFinalSave)
            {
                return;
            }

            var now = DateTime.UtcNow;
            if (!isFinalSave)
            {
                var minInterval = TimeSpan.FromSeconds(Math.Clamp(cfg.MinResumeIntervalSeconds, 0, 86400));
                var minDelta = Math.Clamp(cfg.MinResumeDeltaSeconds, 0, 86400);

                if (now - state.LastResumeSentUtc < minInterval)
                {
                    return;
                }

                if (state.LastResumeSentUtc != DateTime.MinValue
                    && Math.Abs(resumeSeconds - state.LastResumeSeconds) < minDelta)
                {
                    return;
                }
            }

            var sceneId = state.SceneId;
            if (string.IsNullOrWhiteSpace(sceneId))
            {
                sceneId = await _stashClient.ResolveSceneIdAsync(item, ServiceToken).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(sceneId))
                {
                    return;
                }

                state.SceneId = sceneId;
            }

            await state.ResumeSyncGate.WaitAsync(ServiceToken).ConfigureAwait(false);
            try
            {
                // This save may have waited for the final stop request to finish.
                if (state.LastResumeStopUtc != DateTime.MinValue
                    && DateTime.UtcNow - state.LastResumeStopUtc < TimeSpan.FromSeconds(10))
                {
                    return;
                }

                var ok = await _stashClient.SyncResumeAsync(sceneId!, resumeSeconds, ServiceToken).ConfigureAwait(false);
                if (ok)
                {
                    state.LastResumeSeconds = resumeSeconds;
                    state.LastResumeSentUtc = DateTime.UtcNow;
                    state.LastPlayed = false;
                    state.LastPlayCount = ud.PlayCount;
                    _logger.LogInformation(
                        "StashWatchSync: synced playback resume point from user data. sceneId={SceneId} userId={UserId} seconds={Seconds:F1} final={IsFinal}",
                        sceneId, userId, resumeSeconds, isFinalSave);
                }
                else
                {
                    _logger.LogWarning("StashWatchSync: failed resume sync from user data. sceneId={SceneId} userId={UserId} seconds={Seconds:F1}", sceneId, userId, resumeSeconds);
                }
            }
            finally
            {
                state.ResumeSyncGate.Release();
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
        public DateTime LastResumeStopUtc { get; set; } = DateTime.MinValue;
        public SemaphoreSlim ResumeSyncGate { get; } = new(1, 1);

        public bool? LastIsFavorite { get; set; } = null;
        public DateTime LastFavoriteSentUtc { get; set; } = DateTime.MinValue;


        // Cached identity info for background flush.
        public string? SceneId { get; set; }
        public string? ProviderId { get; set; }
        public string? ItemPath { get; set; }
        public string? ItemName { get; set; }
        public DateTime LastActivityUtc { get; set; } = DateTime.MinValue;
        public DateTime LastSceneResolveAttemptUtc { get; set; } = DateTime.MinValue;

        // In-progress duration sync state.
        public DateTime LastDurationSentUtc { get; set; } = DateTime.MinValue;
        public int DurationSyncInFlight;

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
