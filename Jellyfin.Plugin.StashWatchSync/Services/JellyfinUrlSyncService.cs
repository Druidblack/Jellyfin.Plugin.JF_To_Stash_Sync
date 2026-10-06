using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.IO;
using Microsoft.Extensions.Logging;

namespace JFToStashSync.Services;

/// <summary>
/// Synchronizes direct Jellyfin web-client links to matching Stash scenes.
/// </summary>
public sealed class JellyfinUrlSyncService
{
    private readonly ILibraryManager _libraryManager;
    private readonly IServerApplicationHost _applicationHost;
    private readonly StashClient _stashClient;
    private readonly IItemPersistenceService _itemPersistenceService;
    private readonly IFileSystem _fileSystem;
    private readonly ILogger<JellyfinUrlSyncService> _logger;

    public JellyfinUrlSyncService(
        ILibraryManager libraryManager,
        IServerApplicationHost applicationHost,
        StashClient stashClient,
        IItemPersistenceService itemPersistenceService,
        IFileSystem fileSystem,
        ILogger<JellyfinUrlSyncService> logger)
    {
        _libraryManager = libraryManager;
        _applicationHost = applicationHost;
        _stashClient = stashClient;
        _itemPersistenceService = itemPersistenceService;
        _fileSystem = fileSystem;
        _logger = logger;
    }


    public SceneLinkStatusResult GetSceneLinkStatus(string? itemId)
    {
        if (string.IsNullOrWhiteSpace(itemId) || !Guid.TryParse(itemId.Trim(), out var id))
        {
            return SceneLinkStatusResult.Invalid("Invalid Jellyfin item ID.");
        }

        var item = _libraryManager.GetItemById(id);
        if (item is not Video video)
        {
            return SceneLinkStatusResult.NotVideo(id);
        }

        string? stashId = null;
        var hasStashId = video.ProviderIds is not null
            && video.ProviderIds.TryGetValue("Stash", out stashId)
            && !string.IsNullOrWhiteSpace(stashId);

        var cfg = Plugin.Instance?.Configuration;
        // The manual button can always try a native Jellyfin metadata refresh first.
        // Path fallback is only the second-stage fallback when the metadata providers
        // still did not assign a Stash provider ID.
        var canSearch = cfg is not null
            && cfg.Enabled
            && _stashClient.IsConfigured();

        return new SceneLinkStatusResult
        {
            Valid = true,
            IsVideo = true,
            HasStashId = hasStashId,
            StashId = hasStashId && stashId is not null ? stashId : string.Empty,
            CanSearch = canSearch,
            ItemId = video.Id.ToString("N"),
            Message = hasStashId
                ? "This Jellyfin video already has a Stash provider ID."
                : canSearch
                    ? "A Stash provider ID is not assigned yet."
                    : "Manual Stash matching is unavailable for this item.",
        };
    }

    /// <summary>
    /// Runs Jellyfin's native full metadata refresh with ReplaceAllMetadata enabled and
    /// returns the Stash provider ID if one of the installed metadata providers assigned it.
    /// Image replacement is optional and controlled by plugin configuration.
    /// </summary>
    public async Task<string?> RefreshMetadataAndGetStashIdAsync(Guid itemId, CancellationToken ct)
    {
        var item = _libraryManager.GetItemById(itemId);
        if (item is not Video video)
        {
            return null;
        }

        if (TryGetStashProviderId(video, out var existingStashId))
        {
            return existingStashId;
        }

        try
        {
            var replaceImages = Plugin.Instance?.Configuration.ReplaceImagesOnStashMetadataRefresh == true;

            _logger.LogInformation(
                "JFToStashSync: refreshing Jellyfin metadata before Stash identity resolution. itemId={ItemId} name={Name} replaceImages={ReplaceImages}",
                video.Id,
                video.Name ?? string.Empty,
                replaceImages);

            var options = new MetadataRefreshOptions(new DirectoryService(_fileSystem))
            {
                MetadataRefreshMode = MetadataRefreshMode.FullRefresh,
                ImageRefreshMode = replaceImages ? MetadataRefreshMode.FullRefresh : MetadataRefreshMode.ValidationOnly,
                ReplaceAllMetadata = true,
                ReplaceAllImages = replaceImages,
                ForceSave = true,
            };

            await video.RefreshMetadata(options, ct).ConfigureAwait(false);

            var refreshed = _libraryManager.GetItemById(itemId) as Video;
            if (refreshed is not null && TryGetStashProviderId(refreshed, out var refreshedStashId))
            {
                _logger.LogInformation(
                    "JFToStashSync: Jellyfin metadata refresh assigned Stash provider id. itemId={ItemId} sceneId={SceneId} name={Name}",
                    refreshed.Id,
                    refreshedStashId,
                    refreshed.Name ?? string.Empty);
                return refreshedStashId;
            }

            _logger.LogInformation(
                "JFToStashSync: metadata refresh completed without a Stash provider id. itemId={ItemId} name={Name}",
                video.Id,
                video.Name ?? string.Empty);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // A metadata-provider failure must not prevent the caller from applying its next resolution step.
            _logger.LogWarning(
                ex,
                "JFToStashSync: Jellyfin metadata refresh failed; continuing with the caller-specific Stash resolution step. itemId={ItemId} name={Name}",
                video.Id,
                video.Name ?? string.Empty);
        }

        return null;
    }

    private static bool TryGetStashProviderId(Video video, out string stashId)
    {
        stashId = string.Empty;
        if (video.ProviderIds is null
            || !video.ProviderIds.TryGetValue("Stash", out var value)
            || string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        stashId = value;
        return true;
    }

    public async Task<ManualSceneLinkResult> ResolveAndLinkSceneByItemIdAsync(string? itemId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(itemId) || !Guid.TryParse(itemId.Trim(), out var id))
        {
            return ManualSceneLinkResult.Failure("Invalid Jellyfin item ID.");
        }

        var cfg = Plugin.Instance?.Configuration;
        if (cfg is null || !cfg.Enabled)
        {
            return ManualSceneLinkResult.Failure("JF To Stash Sync is disabled.");
        }

        if (!_stashClient.IsConfigured())
        {
            return ManualSceneLinkResult.Failure("Stash endpoint is not configured.");
        }

        var item = _libraryManager.GetItemById(id);
        if (item is not Video video)
        {
            return ManualSceneLinkResult.Failure($"Jellyfin item {id:N} is not a video.");
        }

        if (TryGetStashProviderId(video, out var existingStashId))
        {
            return ManualSceneLinkResult.AlreadyLinkedResult(video, existingStashId);
        }

        if (!TryBuildJellyfinItemUrl(video.Id, cfg.JellyfinBaseUrl, out var jellyfinUrl, out var urlError))
        {
            return ManualSceneLinkResult.Failure(urlError, video);
        }

        // First ask Jellyfin itself to do a full metadata refresh with ReplaceAllMetadata.
        // If the installed Stash metadata provider can identify the file, it will assign the
        // provider ID and no plugin-side title/path search is needed.
        var sceneId = await RefreshMetadataAndGetStashIdAsync(video.Id, ct).ConfigureAwait(false);
        var matchedByMetadataRefresh = !string.IsNullOrWhiteSpace(sceneId);
        video = _libraryManager.GetItemById(id) as Video ?? video;

        // Manual search intentionally skips the plugin-side path/filename fallback.
        // If Jellyfin's metadata providers did not assign a Stash ID, go directly to the
        // optional targeted Stash scan. Resume/Played/play-duration, O-counter, and batch URL sync may still use path fallback.
        StashFolderScanResult? scanResult = null;

        if (string.IsNullOrWhiteSpace(sceneId) && cfg.ScanStashFolderOnManualSearchFailure)
        {
            scanResult = await _stashClient
                .ScanParentFolderAndWaitForSceneAsync(video.Path, ct)
                .ConfigureAwait(false);

            if (!scanResult.Started)
            {
                return ManualSceneLinkResult.Failure(
                    "Stash scene was not found and the targeted folder scan could not be started: " + scanResult.Message,
                    video);
            }

            if (!scanResult.FoundScene || string.IsNullOrWhiteSpace(scanResult.SceneId))
            {
                return ManualSceneLinkResult.Failure(
                    $"Stash scan started for '{scanResult.ScanPath}' (job {scanResult.JobId}), but the exact video did not appear within 60 seconds. The scan may still be queued or running; try the manual search button again after it finishes.",
                    video);
            }

            sceneId = scanResult.SceneId;
        }

        if (string.IsNullOrWhiteSpace(sceneId))
        {
            return ManualSceneLinkResult.Failure(
                cfg.ScanStashFolderOnManualSearchFailure
                    ? $"Metadata refresh did not assign a Stash ID and the targeted Stash folder scan did not find {video.Name}."
                    : $"Metadata refresh did not assign a Stash ID for {video.Name}. Targeted Stash folder scan is disabled.",
                video);
        }

        // Write the Stash URL first. If it fails, leave the Jellyfin provider ID absent so the
        // button remains available for a retry. UpsertJellyfinUrlAsync is idempotent.
        var upsert = await _stashClient.UpsertJellyfinUrlAsync(sceneId, jellyfinUrl, ct).ConfigureAwait(false);
        if (!upsert.Success)
        {
            return ManualSceneLinkResult.Failure(
                "The Stash scene was found, but the Jellyfin URL could not be saved: " + upsert.Message,
                video,
                sceneId);
        }

        if (!matchedByMetadataRefresh)
        {
            try
            {
                video.ProviderIds ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                video.ProviderIds["Stash"] = sceneId;
                _itemPersistenceService.SaveItems(new BaseItem[] { video }, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "JFToStashSync: manual scene match found scene {SceneId}, but Stash provider id could not be persisted. itemId={ItemId}",
                    sceneId,
                    video.Id);

                return ManualSceneLinkResult.Failure(
                    "The Stash scene and Jellyfin URL were found, but the Stash provider ID could not be saved in Jellyfin.",
                    video,
                    sceneId);
            }
        }

        _logger.LogInformation(
            "JFToStashSync: manual Stash scene link completed. itemId={ItemId} sceneId={SceneId} name={Name} urlChanged={UrlChanged} source={Source}",
            video.Id,
            sceneId,
            video.Name ?? string.Empty,
            upsert.Changed,
            matchedByMetadataRefresh ? "metadata-refresh" : "stash-folder-scan");

        return new ManualSceneLinkResult
        {
            Success = true,
            ItemId = video.Id.ToString("N"),
            ItemName = video.Name ?? string.Empty,
            SceneId = sceneId,
            ProviderIdSaved = true,
            UrlSynced = true,
            UrlChanged = upsert.Changed,
            Message = matchedByMetadataRefresh
                ? $"Metadata refresh linked Stash scene {sceneId}."
                : $"Stash folder scan found and linked scene {sceneId} from '{scanResult?.ScanPath}'.",
        };
    }

    public async Task<JellyfinUrlSyncResult> SyncByItemIdAsync(string? itemId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(itemId) || !Guid.TryParse(itemId.Trim(), out var id))
        {
            return JellyfinUrlSyncResult.Failure("Invalid Jellyfin item ID. Enter a 32-character Jellyfin GUID.");
        }

        var item = _libraryManager.GetItemById(id);
        if (item is null)
        {
            return JellyfinUrlSyncResult.Failure($"Jellyfin item {id:N} was not found.");
        }

        if (item is not Video video)
        {
            return JellyfinUrlSyncResult.Failure(
                $"Jellyfin item {id:N} is not a video (actual type: {item.GetType().Name}).");
        }

        return await SyncVideoAsync(video, ct).ConfigureAwait(false);
    }

    public async Task<JellyfinUrlSyncResult> SyncVideoAsync(Video video, CancellationToken ct)
    {
        var cfg = Plugin.Instance?.Configuration;
        if (cfg is null || !cfg.Enabled)
        {
            return JellyfinUrlSyncResult.Failure("JF To Stash Sync is disabled.");
        }

        if (!_stashClient.IsConfigured())
        {
            return JellyfinUrlSyncResult.Failure("Stash endpoint is not configured.");
        }

        if (!TryBuildJellyfinItemUrl(video.Id, cfg.JellyfinBaseUrl, out var jellyfinUrl, out var urlError))
        {
            return JellyfinUrlSyncResult.Failure(urlError);
        }

        var sceneId = await _stashClient.ResolveSceneIdAsync(video, ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(sceneId))
        {
            return JellyfinUrlSyncResult.Failure(
                $"No matching Stash scene was found for Jellyfin item {video.Id:N} ({video.Name}).");
        }

        var upsert = await _stashClient.UpsertJellyfinUrlAsync(sceneId, jellyfinUrl, ct).ConfigureAwait(false);
        if (!upsert.Success)
        {
            return JellyfinUrlSyncResult.Failure(upsert.Message, video, sceneId, jellyfinUrl);
        }

        return new JellyfinUrlSyncResult
        {
            Success = true,
            Changed = upsert.Changed,
            ReplacedCount = upsert.ReplacedCount,
            ItemId = video.Id.ToString("N"),
            ItemName = video.Name ?? string.Empty,
            SceneId = sceneId,
            Url = jellyfinUrl,
            Message = upsert.Message,
        };
    }

    public async Task<JellyfinUrlSyncBatchResult> SyncAllAsync(IProgress<double> progress, CancellationToken ct)
    {
        progress.Report(0);

        var cfg = Plugin.Instance?.Configuration;
        if (cfg is null || !cfg.Enabled)
        {
            return JellyfinUrlSyncBatchResult.SkippedResult("JF To Stash Sync is disabled.");
        }

        if (!cfg.SyncJellyfinUrls)
        {
            return JellyfinUrlSyncBatchResult.SkippedResult(
                "Jellyfin URL synchronization is disabled in plugin settings.");
        }

        if (!_stashClient.IsConfigured())
        {
            return JellyfinUrlSyncBatchResult.SkippedResult("Stash endpoint is not configured.");
        }

        if (!TryBuildJellyfinItemUrl(Guid.Empty, cfg.JellyfinBaseUrl, out _, out var urlError))
        {
            return JellyfinUrlSyncBatchResult.SkippedResult(urlError);
        }

        // Query only non-folder, non-virtual library items and then keep Video instances.
        // OfType<Video>() avoids hard-coding every individual Jellyfin video item kind.
        var videos = _libraryManager.GetItemList(
                new InternalItemsQuery
                {
                    Recursive = true,
                    IsFolder = false,
                    IsVirtualItem = false,
                })
            .OfType<Video>()
            .Where(CanAttemptSceneResolution)
            .ToArray();

        var result = new JellyfinUrlSyncBatchResult
        {
            Total = videos.Length,
        };

        if (videos.Length == 0)
        {
            progress.Report(100);
            result.Message = "No eligible Jellyfin videos were found.";
            return result;
        }

        for (var i = 0; i < videos.Length; i++)
        {
            ct.ThrowIfCancellationRequested();
            var video = videos[i];

            try
            {
                var itemResult = await SyncVideoAsync(video, ct).ConfigureAwait(false);
                if (itemResult.Success)
                {
                    if (itemResult.Changed)
                    {
                        result.Updated++;
                    }
                    else
                    {
                        result.Unchanged++;
                    }
                }
                else
                {
                    result.Skipped++;
                    _logger.LogDebug(
                        "JFToStashSync: Jellyfin URL skipped. itemId={ItemId} name={Name} reason={Reason}",
                        video.Id,
                        video.Name,
                        itemResult.Message);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                result.Failed++;
                _logger.LogError(
                    ex,
                    "JFToStashSync: Jellyfin URL sync failed. itemId={ItemId} name={Name}",
                    video.Id,
                    video.Name);
            }

            progress.Report((i + 1) * 100d / videos.Length);
        }

        result.Message =
            $"Processed {result.Total} videos: updated {result.Updated}, unchanged {result.Unchanged}, " +
            $"skipped {result.Skipped}, failed {result.Failed}.";

        _logger.LogInformation("JFToStashSync: Jellyfin URL batch finished. {Summary}", result.Message);
        return result;
    }

    private static bool CanAttemptSceneResolution(Video video)
    {
        var cfg = Plugin.Instance?.Configuration;
        if (video.ProviderIds is not null
            && video.ProviderIds.TryGetValue("Stash", out var providerId)
            && !string.IsNullOrWhiteSpace(providerId))
        {
            return true;
        }

        return cfg?.EnablePathFallback == true && !string.IsNullOrWhiteSpace(video.Path);
    }

    private bool TryBuildJellyfinItemUrl(Guid itemId, string? configuredBaseUrl, out string url, out string error)
    {
        url = string.Empty;
        error = string.Empty;

        var baseUrl = (configuredBaseUrl ?? string.Empty).Trim();
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var parsed)
            || (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps))
        {
            error = "Configure a valid Jellyfin base URL, for example http://192.168.1.201:3096.";
            return false;
        }

        if (!string.IsNullOrEmpty(parsed.Query) || !string.IsNullOrEmpty(parsed.Fragment))
        {
            error = "Jellyfin base URL must not contain a query string or fragment.";
            return false;
        }

        baseUrl = baseUrl.TrimEnd('/');

        // Be forgiving when a user pastes the web-client path instead of just the server base URL.
        if (baseUrl.EndsWith("/web/index.html", StringComparison.OrdinalIgnoreCase))
        {
            baseUrl = baseUrl[..^"/web/index.html".Length];
        }
        else if (baseUrl.EndsWith("/web", StringComparison.OrdinalIgnoreCase))
        {
            baseUrl = baseUrl[..^"/web".Length];
        }

        var id = itemId.ToString("N");
        url = $"{baseUrl}/web/index.html#/details?id={id}&serverId={Uri.EscapeDataString(_applicationHost.SystemId)}";
        return true;
    }
}

public sealed class JellyfinUrlSyncResult
{
    public bool Success { get; set; }

    public bool Changed { get; set; }

    public int ReplacedCount { get; set; }

    public string ItemId { get; set; } = string.Empty;

    public string ItemName { get; set; } = string.Empty;

    public string SceneId { get; set; } = string.Empty;

    public string Url { get; set; } = string.Empty;

    public string Message { get; set; } = string.Empty;

    public static JellyfinUrlSyncResult Failure(
        string message,
        Video? video = null,
        string? sceneId = null,
        string? url = null)
        => new()
        {
            Success = false,
            ItemId = video?.Id.ToString("N") ?? string.Empty,
            ItemName = video?.Name ?? string.Empty,
            SceneId = sceneId ?? string.Empty,
            Url = url ?? string.Empty,
            Message = message,
        };
}

public sealed class JellyfinUrlSyncBatchResult
{
    public int Total { get; set; }

    public int Updated { get; set; }

    public int Unchanged { get; set; }

    public int Skipped { get; set; }

    public int Failed { get; set; }

    public string Message { get; set; } = string.Empty;

    public static JellyfinUrlSyncBatchResult SkippedResult(string message)
        => new()
        {
            Message = message,
        };
}


public sealed class SceneLinkStatusResult
{
    public bool Valid { get; set; }

    public bool IsVideo { get; set; }

    public bool HasStashId { get; set; }

    public bool CanSearch { get; set; }

    public string ItemId { get; set; } = string.Empty;

    public string StashId { get; set; } = string.Empty;

    public string Message { get; set; } = string.Empty;

    public bool IsProcessing { get; set; }

    public string JobId { get; set; } = string.Empty;

    public static SceneLinkStatusResult Invalid(string message)
        => new() { Message = message };

    public static SceneLinkStatusResult NotVideo(Guid id)
        => new()
        {
            Valid = true,
            IsVideo = false,
            ItemId = id.ToString("N"),
            Message = "The Jellyfin item is not a video.",
        };
}

public sealed class ManualSceneLinkResult
{
    public bool Success { get; set; }

    public bool AlreadyLinked { get; set; }

    public bool ProviderIdSaved { get; set; }

    public bool UrlSynced { get; set; }

    public bool UrlChanged { get; set; }

    public string ItemId { get; set; } = string.Empty;

    public string ItemName { get; set; } = string.Empty;

    public string SceneId { get; set; } = string.Empty;

    public string Message { get; set; } = string.Empty;

    public static ManualSceneLinkResult Failure(string message, Video? video = null, string? sceneId = null)
        => new()
        {
            Success = false,
            ItemId = video?.Id.ToString("N") ?? string.Empty,
            ItemName = video?.Name ?? string.Empty,
            SceneId = sceneId ?? string.Empty,
            Message = message,
        };

    public static ManualSceneLinkResult AlreadyLinkedResult(Video video, string sceneId)
        => new()
        {
            Success = true,
            AlreadyLinked = true,
            ProviderIdSaved = true,
            ItemId = video.Id.ToString("N"),
            ItemName = video.Name ?? string.Empty,
            SceneId = sceneId,
            Message = $"This video is already linked to Stash scene {sceneId}.",
        };
}
