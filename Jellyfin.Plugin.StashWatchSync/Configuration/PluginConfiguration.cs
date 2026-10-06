using MediaBrowser.Model.Plugins;

namespace JFToStashSync.Configuration;

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
/// Sync Jellyfin actor favorites to Stash performer favorites.
/// When enabled, toggling the 'heart' on a Person/Actor item in Jellyfin will set the same favorite state
/// on the corresponding Stash performer (only when the Jellyfin person has a Stash provider id).
/// </summary>
public bool SyncPerformerFavorites { get; set; } = false;

    /// <summary>
    /// When you favorite/unfavorite a video in Jellyfin, set the matching scene rating in Stash (favorite=5, unfavorite=0).
    /// Scene matching uses the Stash provider id when available; otherwise the file-path fallback is used when enabled.
    /// </summary>
    public bool SyncFavoriteToRating { get; set; } = false;



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

    /// <summary>
    /// Optional Jellyfin user GUID whose resume position is sent to Stash.
    /// Stash stores a single shared resume position for each scene. Empty = all allowed users.
    /// </summary>
    public string ResumeUserId { get; set; } = string.Empty;

    /// <summary>Minimum delta (seconds) between resume updates.</summary>
    public int MinResumeDeltaSeconds { get; set; } = 20;

    /// <summary>Minimum interval (seconds) between resume updates per item+user.</summary>
    public int MinResumeIntervalSeconds { get; set; } = 60;


    /// <summary>
    /// When enabled, the scheduled task writes a direct Jellyfin details URL
    /// to the matching Stash scene.
    /// </summary>
    public bool SyncJellyfinUrls { get; set; } = false;

    /// <summary>
    /// When enabled, the scheduled task writes a direct Jellyfin Person details URL
    /// to the matching Stash performer. Matching requires the Jellyfin Person to have
    /// a Stash provider id.
    /// </summary>
    public bool SyncJellyfinPerformerUrls { get; set; } = false;

    /// <summary>
    /// Show an O+ button in the Jellyfin Web video player. Pressing it increments
    /// the matching Stash scene o-counter using Stash sceneAddO.
    /// Requires the File Transformation plugin to inject the client script.
    /// </summary>
    public bool EnablePlayerOCounterButton { get; set; } = true;

    /// <summary>
    /// Show an actors button in the Jellyfin Web video player. The popup lists Actor persons
    /// for the currently playing item and lets the current Jellyfin user toggle favorites.
    /// </summary>
    public bool EnablePlayerActorListButton { get; set; } = true;

    /// <summary>
    /// In Jellyfin Web movie/video detail pages, replace recognized actor-role text
    /// (Female, Male, Transgender Female, Transgender Male, Non Binary) with gender icons.
    /// The role is read from the current video's Jellyfin People metadata, not parsed from localized DOM text.
    /// Requires the File Transformation web integration.
    /// </summary>
    public bool ShowActorGenderIcons { get; set; } = false;

    /// <summary>
    /// In Jellyfin Web person detail pages, convert plain http/https URLs in the person's
    /// overview text into clickable external links. Requires the File Transformation web integration.
    /// </summary>
    public bool LinkifyPersonOverviewUrls { get; set; } = false;

    /// <summary>
    /// In Jellyfin Web person detail pages, show clickable social/service icons next to
    /// the person's name when matching http/https URLs are present in the Jellyfin Overview.
    /// Uses the bundled user-supplied SVG logos and does not require Stash.
    /// </summary>
    public bool ShowPersonSocialIcons { get; set; } = false;

    /// <summary>
    /// Base Jellyfin URL that Stash users can open, for example
    /// http://192.168.1.201:3096 or https://example.org/jellyfin.
    /// </summary>
    public string JellyfinBaseUrl { get; set; } = string.Empty;

    /// <summary>
    /// When resolving a missing Stash provider id via Jellyfin metadata refresh, also perform
    /// a full image refresh and replace existing images. Applies to the manual Stash-search button only.
    /// </summary>
    public bool ReplaceImagesOnStashMetadataRefresh { get; set; } = false;

    /// <summary>
    /// When the manual Stash-search button still has no Stash provider id after Jellyfin metadata refresh,
    /// ask Stash to scan the parent folder of the mapped video path. Manual search skips path/title fallback.
    /// This option applies only to the manual search button; Favorite-triggered lookup never starts a Stash scan.
    /// </summary>
    public bool ScanStashFolderOnManualSearchFailure { get; set; } = false;

    /// <summary>If the item has no Stash provider id, Resume/Played/play-duration, O-counter, and batch URL sync may attempt to find it by file path. Favorite and manual search do not use this fallback.</summary>
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
