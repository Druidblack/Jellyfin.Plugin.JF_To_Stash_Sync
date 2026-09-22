using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace StashWatchSync.Services;

/// <summary>
/// Synchronizes direct Jellyfin web-client links to matching Stash scenes.
/// </summary>
public sealed class JellyfinUrlSyncService
{
    private readonly ILibraryManager _libraryManager;
    private readonly IServerApplicationHost _applicationHost;
    private readonly StashClient _stashClient;
    private readonly ILogger<JellyfinUrlSyncService> _logger;

    public JellyfinUrlSyncService(
        ILibraryManager libraryManager,
        IServerApplicationHost applicationHost,
        StashClient stashClient,
        ILogger<JellyfinUrlSyncService> logger)
    {
        _libraryManager = libraryManager;
        _applicationHost = applicationHost;
        _stashClient = stashClient;
        _logger = logger;
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
                        "StashWatchSync: Jellyfin URL skipped. itemId={ItemId} name={Name} reason={Reason}",
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
                    "StashWatchSync: Jellyfin URL sync failed. itemId={ItemId} name={Name}",
                    video.Id,
                    video.Name);
            }

            progress.Report((i + 1) * 100d / videos.Length);
        }

        result.Message =
            $"Processed {result.Total} videos: updated {result.Updated}, unchanged {result.Unchanged}, " +
            $"skipped {result.Skipped}, failed {result.Failed}.";

        _logger.LogInformation("StashWatchSync: Jellyfin URL batch finished. {Summary}", result.Message);
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
