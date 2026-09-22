using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace StashWatchSync.Services;

/// <summary>
/// Synchronizes direct Jellyfin web-client links for Person items to matching Stash performers.
/// </summary>
public sealed class JellyfinPerformerUrlSyncService
{
    private const string StashProviderIdKey = "Stash";

    private readonly ILibraryManager _libraryManager;
    private readonly IServerApplicationHost _applicationHost;
    private readonly StashClient _stashClient;
    private readonly ILogger<JellyfinPerformerUrlSyncService> _logger;

    public JellyfinPerformerUrlSyncService(
        ILibraryManager libraryManager,
        IServerApplicationHost applicationHost,
        StashClient stashClient,
        ILogger<JellyfinPerformerUrlSyncService> logger)
    {
        _libraryManager = libraryManager;
        _applicationHost = applicationHost;
        _stashClient = stashClient;
        _logger = logger;
    }

    public async Task<JellyfinPerformerUrlSyncResult> SyncByPersonIdAsync(string? personId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(personId) || !Guid.TryParse(personId.Trim(), out var id))
        {
            return JellyfinPerformerUrlSyncResult.Failure(
                "Invalid Jellyfin Person ID. Enter a 32-character Jellyfin GUID.");
        }

        var item = _libraryManager.GetItemById(id);
        if (item is null)
        {
            return JellyfinPerformerUrlSyncResult.Failure($"Jellyfin item {id:N} was not found.");
        }

        if (item is not Person person)
        {
            return JellyfinPerformerUrlSyncResult.Failure(
                $"Jellyfin item {id:N} is not a Person (actual type: {item.GetType().Name}).");
        }

        return await SyncPersonAsync(person, ct).ConfigureAwait(false);
    }

    public async Task<JellyfinPerformerUrlSyncResult> SyncPersonAsync(Person person, CancellationToken ct)
    {
        var cfg = Plugin.Instance?.Configuration;
        if (cfg is null || !cfg.Enabled)
        {
            return JellyfinPerformerUrlSyncResult.Failure("JF To Stash Sync is disabled.", person);
        }

        if (!_stashClient.IsConfigured())
        {
            return JellyfinPerformerUrlSyncResult.Failure("Stash endpoint is not configured.", person);
        }

        if (!TryGetStashPerformerId(person, out var performerId))
        {
            return JellyfinPerformerUrlSyncResult.Failure(
                $"Jellyfin Person {person.Id:N} ({person.Name}) does not have a Stash provider ID.",
                person);
        }

        if (!TryBuildJellyfinItemUrl(person.Id, cfg.JellyfinBaseUrl, out var jellyfinUrl, out var urlError))
        {
            return JellyfinPerformerUrlSyncResult.Failure(urlError, person, performerId);
        }

        var upsert = await _stashClient.UpsertJellyfinPerformerUrlAsync(
            performerId,
            jellyfinUrl,
            ct).ConfigureAwait(false);

        if (!upsert.Success)
        {
            return JellyfinPerformerUrlSyncResult.Failure(
                upsert.Message,
                person,
                performerId,
                jellyfinUrl);
        }

        return new JellyfinPerformerUrlSyncResult
        {
            Success = true,
            Changed = upsert.Changed,
            ReplacedCount = upsert.ReplacedCount,
            PersonId = person.Id.ToString("N"),
            PersonName = person.Name ?? string.Empty,
            PerformerId = performerId,
            Url = jellyfinUrl,
            Message = upsert.Message,
        };
    }

    public async Task<JellyfinPerformerUrlSyncBatchResult> SyncAllAsync(
        IProgress<double> progress,
        CancellationToken ct)
    {
        progress.Report(0);

        var cfg = Plugin.Instance?.Configuration;
        if (cfg is null || !cfg.Enabled)
        {
            return JellyfinPerformerUrlSyncBatchResult.SkippedResult("JF To Stash Sync is disabled.");
        }

        if (!cfg.SyncJellyfinPerformerUrls)
        {
            return JellyfinPerformerUrlSyncBatchResult.SkippedResult(
                "Jellyfin performer URL synchronization is disabled in plugin settings.");
        }

        if (!_stashClient.IsConfigured())
        {
            return JellyfinPerformerUrlSyncBatchResult.SkippedResult("Stash endpoint is not configured.");
        }

        if (!TryBuildJellyfinItemUrl(Guid.Empty, cfg.JellyfinBaseUrl, out _, out var urlError))
        {
            return JellyfinPerformerUrlSyncBatchResult.SkippedResult(urlError);
        }

        // Ask Jellyfin only for Person records instead of loading every library item.
        var people = _libraryManager.GetItemList(
                new InternalItemsQuery
                {
                    IncludeItemTypes = new[] { BaseItemKind.Person },
                    Recursive = true,
                })
            .OfType<Person>()
            .Where(HasStashProviderId)
            .ToArray();

        var result = new JellyfinPerformerUrlSyncBatchResult
        {
            Total = people.Length,
        };

        if (people.Length == 0)
        {
            progress.Report(100);
            result.Message = "No Jellyfin people with a Stash provider ID were found.";
            return result;
        }

        for (var i = 0; i < people.Length; i++)
        {
            ct.ThrowIfCancellationRequested();
            var person = people[i];

            try
            {
                var personResult = await SyncPersonAsync(person, ct).ConfigureAwait(false);
                if (personResult.Success)
                {
                    if (personResult.Changed)
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
                        "StashWatchSync: Jellyfin performer URL skipped. personId={PersonId} name={Name} reason={Reason}",
                        person.Id,
                        person.Name,
                        personResult.Message);
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
                    "StashWatchSync: Jellyfin performer URL sync failed. personId={PersonId} name={Name}",
                    person.Id,
                    person.Name);
            }

            progress.Report((i + 1) * 100d / people.Length);
        }

        result.Message =
            $"Processed {result.Total} performers: updated {result.Updated}, unchanged {result.Unchanged}, " +
            $"skipped {result.Skipped}, failed {result.Failed}.";

        _logger.LogInformation(
            "StashWatchSync: Jellyfin performer URL batch finished. {Summary}",
            result.Message);

        return result;
    }

    private static bool HasStashProviderId(Person person)
        => TryGetStashPerformerId(person, out _);

    private static bool TryGetStashPerformerId(Person person, out string performerId)
    {
        performerId = string.Empty;
        if (person.ProviderIds is null
            || !person.ProviderIds.TryGetValue(StashProviderIdKey, out var id)
            || string.IsNullOrWhiteSpace(id))
        {
            return false;
        }

        performerId = id.Trim();
        return true;
    }

    private bool TryBuildJellyfinItemUrl(
        Guid itemId,
        string? configuredBaseUrl,
        out string url,
        out string error)
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

        if (baseUrl.EndsWith("/web/index.html", StringComparison.OrdinalIgnoreCase))
        {
            baseUrl = baseUrl[..^"/web/index.html".Length];
        }
        else if (baseUrl.EndsWith("/web", StringComparison.OrdinalIgnoreCase))
        {
            baseUrl = baseUrl[..^"/web".Length];
        }

        url =
            $"{baseUrl}/web/index.html#/details?id={itemId:N}&serverId={Uri.EscapeDataString(_applicationHost.SystemId)}";
        return true;
    }
}

public sealed class JellyfinPerformerUrlSyncResult
{
    public bool Success { get; set; }

    public bool Changed { get; set; }

    public int ReplacedCount { get; set; }

    public string PersonId { get; set; } = string.Empty;

    public string PersonName { get; set; } = string.Empty;

    public string PerformerId { get; set; } = string.Empty;

    public string Url { get; set; } = string.Empty;

    public string Message { get; set; } = string.Empty;

    public static JellyfinPerformerUrlSyncResult Failure(
        string message,
        Person? person = null,
        string? performerId = null,
        string? url = null)
        => new()
        {
            Success = false,
            PersonId = person?.Id.ToString("N") ?? string.Empty,
            PersonName = person?.Name ?? string.Empty,
            PerformerId = performerId ?? string.Empty,
            Url = url ?? string.Empty,
            Message = message,
        };
}

public sealed class JellyfinPerformerUrlSyncBatchResult
{
    public int Total { get; set; }

    public int Updated { get; set; }

    public int Unchanged { get; set; }

    public int Skipped { get; set; }

    public int Failed { get; set; }

    public string Message { get; set; } = string.Empty;

    public static JellyfinPerformerUrlSyncBatchResult SkippedResult(string message)
        => new()
        {
            Message = message,
        };
}
