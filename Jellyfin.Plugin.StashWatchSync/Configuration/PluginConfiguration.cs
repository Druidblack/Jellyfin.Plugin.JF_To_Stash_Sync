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
    /// Show a StashDB Trending carousel on the Jellyfin Web home page.
    /// Fetches Trending directly from StashDB using the credentials already stored in Stash.
    /// </summary>
    public bool EnableStashDbTrendingRow { get; set; } = false;

    /// <summary>Optional Jellyfin library ID for the Trending row; empty means accessible libraries.</summary>
    public string StashDbTrendingLibraryId { get; set; } = string.Empty;

    /// <summary>Number of StashDB Trending pages (40 entries each) initially examined.</summary>
    public int StashDbTrendingPages { get; set; } = 5;


    /// <summary>Show cached Babepedia ratings below actor portraits in Jellyfin Web.</summary>
    public bool EnableBabepediaActorStats { get; set; } = false;

    /// <summary>How long successfully retrieved Babepedia statistics remain fresh: 1, 7 or 30 days.</summary>
    public int BabepediaCacheDays { get; set; } = 7;

    /// <summary>Show cached Pornhub statistics in separate lines below Person portraits.</summary>
    public bool EnablePornhubActorStats { get; set; } = false;

    /// <summary>Show a featured Pornhub-style actor banner above custom home-page rows in Jellyfin Web.</summary>
    public bool EnablePornhubFeaturedBanner { get; set; } = false;

    // Self-contained media slideshow inspired by Media Bar Enhanced.
    // Independent of the featured actor banner; disabled until selected.
    public bool EnableFeaturedMediaBanner { get; set; } = false;
    public bool FeaturedMediaIncludeWatched { get; set; } = false;
    public bool FeaturedMediaRequireLogo { get; set; } = false;
    public bool FeaturedMediaShowOverview { get; set; } = true;
    public bool FeaturedMediaShowRatings { get; set; } = true;
    public bool FeaturedMediaShowProgress { get; set; } = true;
    public bool FeaturedMediaPauseOnHover { get; set; } = false;
    public string FeaturedMediaSortBy { get; set; } = "Random";
    public int FeaturedMediaSlideSeconds { get; set; } = 15;
    public int FeaturedMediaMaxItems { get; set; } = 30;
    public string FeaturedMediaLibraryIds { get; set; } = string.Empty;

    /// <summary>Featured banner rotation: Random or Ranked (best-ranked people first).</summary>
    // The actor slideshow has independent controls from the media slideshow.
    public int FeaturedBannerSlideSeconds { get; set; } = 15;
    public int FeaturedBannerMaxItems { get; set; } = 30;
    public bool FeaturedBannerShowProgress { get; set; } = true;
    public bool FeaturedBannerPauseOnHover { get; set; } = false;

    public string FeaturedBannerOrder { get; set; } = "Random";

    /// <summary>Ranking provider for banner sorting: Pornhub or Babepedia.</summary>
    public string FeaturedBannerRankingSource { get; set; } = "Pornhub";

    public bool FeaturedBannerShowPornhubModelRank { get; set; } = true;
    public bool FeaturedBannerShowPornhubWeeklyRank { get; set; } = false;
    public bool FeaturedBannerShowPornhubMonthlyRank { get; set; } = false;
    public bool FeaturedBannerShowPornhubLastMonthRank { get; set; } = false;
    public bool FeaturedBannerShowPornhubYearlyRank { get; set; } = false;
    public bool FeaturedBannerShowPornhubVideoViews { get; set; } = true;
    public bool FeaturedBannerShowPornhubSubscribers { get; set; } = true;
    public bool FeaturedBannerShowBabepediaRating { get; set; } = false;
    public bool FeaturedBannerShowBabepediaRank { get; set; } = false;
    public bool FeaturedBannerShowBabepediaFavorites { get; set; } = false;
    public bool FeaturedBannerShowBabepediaVotes { get; set; } = false;

    /// <summary>How many days manual Pornhub statistics remain fresh: 1, 7, 30.</summary>
    public int PornhubStatsCacheDays { get; set; } = 7;

    /// <summary>Enable scheduled collection of Pornhub ratings; independent of the Web display option.</summary>
    public bool PornhubScheduledFetchStats { get; set; } = false;

    /// <summary>Enable scheduled downloads of actor Logo/Banner from Pornhub profiles.</summary>
    public bool PornhubScheduledFetchArtwork { get; set; } = false;

    /// <summary>Artwork policy: MissingOnly (do not replace) or AfterCacheExpiry (refresh stale images).</summary>
    public string PornhubArtworkUpdateMode { get; set; } = "MissingOnly";

    /// <summary>Number of days before downloaded images may be replaced in AfterCacheExpiry mode.</summary>
    public int PornhubArtworkCacheDays { get; set; } = 7;

    /// <summary>Show precomputed Babepedia Top 100 lists as performer rows.</summary>
    public bool EnableBabepediaTopRows { get; set; } = false;

    /// <summary>Freshness of each Babepedia Top 100 list: 1, 7, or 30 days.</summary>
    public int BabepediaTopCacheDays { get; set; } = 1;

    /// <summary>
    /// When a Babepedia Top 100 profile has no matching external URL in Stash,
    /// try an exact, unambiguous name match against Jellyfin Person records.
    /// Never edits Stash links or Jellyfin provider IDs.
    /// </summary>
    public bool BabepediaTopFallbackToJellyfinName { get; set; } = false;

    public bool BabepediaTopPornstars { get; set; } = true;
    public bool BabepediaTopMilf { get; set; } = false;
    public bool BabepediaTopAsian { get; set; } = false;
    public bool BabepediaTopLatina { get; set; } = false;
    public bool BabepediaTopSlavic { get; set; } = false;
    public bool BabepediaTopEbony { get; set; } = false;
    public bool BabepediaTopCaucasian { get; set; } = false;
    public bool BabepediaTopOnlyfans { get; set; } = false;

    /// <summary>Show a separate StashDB Popularity row on the Jellyfin Web home page.</summary>
    public bool EnableStashDbPopularityRow { get; set; } = false;

    /// <summary>Optional Jellyfin library ID for Popularity; empty means all accessible libraries.</summary>
    public string StashDbPopularityLibraryId { get; set; } = string.Empty;

    /// <summary>Number of StashDB Popularity pages (40 entries per page).</summary>
    public int StashDbPopularityPages { get; set; } = 5;


    /// <summary>Color the poster border in StashDB Trending/Popularity by current user's favorite / played state.</summary>
    public bool HighlightRankedVideoStates { get; set; } = false;

    /// <summary>Show the performer's birth-country flag on StashDB/Babepedia ranking portraits.</summary>
    public bool ShowRankedPerformerBirthFlags { get; set; } = false;

    /// <summary>Show StashDB performer Popularity rows on the Jellyfin home page.</summary>
    public bool EnableStashDbPerformerPopularityRows { get; set; } = false;

    /// <summary>Number of ranking pages (40 performers/page) for each selected gender.</summary>
    public int StashDbPerformerPopularityPages { get; set; } = 5;

    /// <summary>Each enabled category is displayed as a separate horizontal row.</summary>
    public bool StashDbPerformerGenderFemale { get; set; } = true;
    public bool StashDbPerformerGenderIntersex { get; set; } = false;
    public bool StashDbPerformerGenderMale { get; set; } = false;
    public bool StashDbPerformerGenderNonBinary { get; set; } = false;
    public bool StashDbPerformerGenderTransgenderFemale { get; set; } = false;
    public bool StashDbPerformerGenderTransgenderMale { get; set; } = false;

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
    /// Highlight only current-user favorite actor portraits with a subtle pale-red inset outline in Jellyfin Web,
    /// both on native Person cards and on StashDB Performer Popularity rows.
    /// Does not affect non-Person posters or require the Popularity rows to be enabled.
    /// </summary>
    public bool HighlightActorPortraits { get; set; } = false;

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

    /// <summary>Replace the Person information section with optional Info / Co-stars tabs.</summary>
    public bool EnablePersonCoStarsTabs { get; set; } = false;
    public bool CoStarsGenderFemale { get; set; } = true;
    public bool CoStarsGenderMale { get; set; } = true;
    public bool CoStarsGenderIntersex { get; set; } = true;
    public bool CoStarsGenderNonBinary { get; set; } = true;
    public bool CoStarsGenderTransgenderFemale { get; set; } = true;
    public bool CoStarsGenderTransgenderMale { get; set; } = true;

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
