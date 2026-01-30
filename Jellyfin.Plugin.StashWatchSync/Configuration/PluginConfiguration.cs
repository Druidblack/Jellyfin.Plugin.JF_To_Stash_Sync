using MediaBrowser.Model.Plugins;

namespace StashWatchSync.Configuration;

public sealed class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>Master on/off switch.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Base URL of your Stash instance (e.g. http://192.168.1.201:9999).</summary>
    public string StashEndpoint { get; set; } = string.Empty;

    /// <summary>Optional API key for Stash (Settings → Security → API Keys).</summary>
    public string StashApiKey { get; set; } = string.Empty;

    /// <summary>When an item becomes "Played" in Jellyfin, mark it watched in Stash.</summary>
    public bool SyncWatched { get; set; } = true;

    /// <summary>
    /// Sync Jellyfin play count to Stash using the GraphQL mutations sceneIncrementPlayCount/sceneDecrementPlayCount.
    /// By default, the plugin only increments (never decreases).
    /// </summary>
    public bool SyncPlayCount { get; set; } = true;

    /// <summary>
    /// Sync Jellyfin playback duration to Stash play_duration.
    /// The plugin estimates real watched time by tracking how the playback position changes over time.
    /// When an item becomes Played in Jellyfin, the plugin will add the accumulated watched time to Stash's play_duration.
    /// </summary>
    public bool SyncPlayDuration { get; set; } = true;



    /// <summary>
    /// Flush in-progress watched-time to Stash even when the item is not marked Played in Jellyfin.
    /// This helps keep Stash play_duration up to date for unfinished playback sessions.
    /// </summary>
    public bool SyncInProgressPlayDuration { get; set; } = true;

    /// <summary>
    /// Minimum seconds of newly accumulated watched time before we try to send an in-progress update.
    /// </summary>
    public int InProgressMinWatchedSecondsToSync { get; set; } = 1;

    /// <summary>
    /// Minimum interval (seconds) between in-progress play_duration updates per item+user.
    /// </summary>
    public int InProgressMinIntervalSeconds { get; set; } = 30;

    /// <summary>
    /// If no position updates were seen for this many seconds, treat the playback session as stopped and flush any pending watched time.
    /// </summary>
    public int InProgressInactivityFlushSeconds { get; set; } = 25;

    /// <summary>
    /// Background flush loop interval (seconds).
    /// </summary>
    public int BackgroundFlushIntervalSeconds { get; set; } = 10;

    /// <summary>Sync Jellyfin resume position (PlaybackPositionTicks) to Stash resume_time.</summary>
    public bool SyncResumePosition { get; set; } = false;

    /// <summary>Minimum delta (seconds) between resume updates.</summary>
    public int MinResumeDeltaSeconds { get; set; } = 20;

    /// <summary>Minimum interval (seconds) between resume updates per item+user.</summary>
    public int MinResumeIntervalSeconds { get; set; } = 60;

    /// <summary>If the item has no Stash provider id, attempt to find it by file path.</summary>
    public bool EnablePathFallback { get; set; } = true;

    /// <summary>When searching by path, use the full path; otherwise only use the filename.</summary>
    public bool SearchByFullPath { get; set; } = true;

    /// <summary>
    /// Optional: replace this prefix in Jellyfin paths before querying Stash.
    /// Example: JellyfinPathPrefix=/mnt/media, StashPathPrefix=/data
    /// </summary>
    public string JellyfinPathPrefix { get; set; } = string.Empty;

    /// <summary>See <see cref="JellyfinPathPrefix"/>.</summary>
    public string StashPathPrefix { get; set; } = string.Empty;

    /// <summary>
    /// Optional comma-separated list of Jellyfin user IDs to sync. Empty = all users.
    /// </summary>
    public string OnlyUserIdsCsv { get; set; } = string.Empty;
}
