using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Microsoft.Extensions.Logging;

namespace JFToStashSync.Services;

/// <summary>
/// Reads the existing StashDB connection from Stash, obtains the TRENDING ranking
/// directly from the official StashDB GraphQL API, and resolves it against Stash
/// scenes. No Stash-side plugin, Python, or separate StashDB credential is required.
/// No API key or remote media metadata is returned to Jellyfin Web.
/// </summary>
public sealed class StashDbTrendingService : IDisposable
{

    private const string StashDbGraphQl = "https://stashdb.org/graphql";
    private const int PageSize = 40;
    private const int LocalPageSize = 100;
    private const int MaxLocalPages = 100;

    private const string StashConnectionQuery = @"query JFToStashTrendingStashBoxes {
  configuration { general { stashBoxes { endpoint api_key } } }
}";

    private const string RemoteTrendingQuery = @"query JFToStashTrending($input: SceneQueryInput!) {
  queryScenes(input: $input) { count scenes { id } }
}";

    private const string RemotePerformersQuery = @"query JFToStashPopularPerformers($input: PerformerQueryInput!) {
  queryPerformers(input: $input) { count performers { id gender } }
}";

    private const string LocalPerformersQuery = @"query JFToStashLocalPerformers($filter: FindFilterType!, $performer_filter: PerformerFilterType!) {
  findPerformers(filter: $filter, performer_filter: $performer_filter) {
    count performers { id stash_ids { endpoint stash_id } }
  }
}";

    private const string LocalScenesQuery = @"query JFToStashTrendingLocal($filter: FindFilterType!, $scene_filter: SceneFilterType!) {
  findScenes(filter: $filter, scene_filter: $scene_filter) {
    count scenes { id stash_ids { endpoint stash_id } }
  }
}";

    // Owned by this singleton service. Redirects are disabled so a StashDB
    // ApiKey header cannot be forwarded to a different destination.
    private readonly HttpClient _httpClient = new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        ConnectTimeout = TimeSpan.FromSeconds(10),
        PooledConnectionLifetime = TimeSpan.FromMinutes(5)
    });
    private readonly ILogger<StashDbTrendingService> _logger;
    // Ranking responses are intentionally not cached in memory. Every scheduled
    // index refresh reads the current ranking from StashDB, then atomically
    // replaces the persisted prepared index after *all* matching succeeds.
    private sealed record RankingPage(int TotalCount, IReadOnlyList<string> Ids);

    public StashDbTrendingService(ILogger<StashDbTrendingService> logger)
    {
        _logger = logger;
    }

    public void Dispose()
    {
        _httpClient.Dispose();
    }

    public async Task<StashTrendingPage> GetPageAsync(int page, CancellationToken ct)
    {
        if (page < 1 || page > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(page));
        }

        var cfg = Plugin.Instance?.Configuration;
        if (cfg is null || !cfg.Enabled || !cfg.EnableStashDbTrendingRow || string.IsNullOrWhiteSpace(cfg.StashEndpoint))
        {
            throw new InvalidOperationException("Enable Trending and configure the Stash connection first.");
        }

        // Stash's saved Metadata Providers -> Stash-box Endpoints is the source
        // of truth for the StashDB API key. Never expose or persist this key.
        _logger.LogDebug("JFToStashSync: Trending page {Page} started; connecting to Stash.", page);
        var stashUri = StashGraphQlUri(cfg.StashEndpoint);
        var connection = await PostAsync(stashUri, StashConnectionQuery, new { }, cfg.StashApiKey,
            "Stash", ct).ConfigureAwait(false);
        var boxes = connection["configuration"]?["general"]?["stashBoxes"] as JArray
            ?? throw new InvalidOperationException("Stash did not return its Stash-box connections.");
        var stashDbBox = boxes.OfType<JObject>()
            .FirstOrDefault(box => IsOfficialStashDbEndpoint(box.Value<string>("endpoint")));
        var stashDbApiKey = stashDbBox?.Value<string>("api_key");
        if (string.IsNullOrWhiteSpace(stashDbApiKey))
        {
            throw new InvalidOperationException("Add https://stashdb.org/graphql and an API key in Stash Metadata Providers → Stash-box Endpoints.");
        }

        _logger.LogDebug("JFToStashSync: Trending page {Page}: StashDB connection found; requesting ranking.", page);
        var ranking = await GetRankingAsync(stashDbApiKey, page, "TRENDING", ct).ConfigureAwait(false);
        _logger.LogDebug("JFToStashSync: Trending page {Page}: received {RemoteCount} StashDB scene IDs; resolving locally.",
            page, ranking.Ids.Count);
        if (ranking.Ids.Count == 0)
        {
            return new StashTrendingPage(page, Array.Empty<StashTrendingEntry>(), false);
        }

        var ranks = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < ranking.Ids.Count; index++)
        {
            // Keep the first (best) rank if a changing remote list includes duplicates.
            ranks.TryAdd(ranking.Ids[index], ((page - 1) * PageSize) + index + 1);
        }

        var entries = await FindLocalMatchesAsync(stashUri, cfg.StashApiKey, ranks, ct).ConfigureAwait(false);
        var hasMore = ranking.Ids.Count == PageSize && (long)page * PageSize < ranking.TotalCount;
        _logger.LogDebug("JFToStashSync: Trending page {Page}: resolved {LocalCount} local Stash scenes; hasMore={HasMore}.",
            page, entries.Count, hasMore);
        return new StashTrendingPage(page, entries, hasMore);
    }

    /// <summary>
    /// Builds the configured Trending ranking for the existing scheduled task.
    /// </summary>
    public Task<IReadOnlyList<StashTrendingEntry>> GetTopEntriesAsync(int requestedPages, CancellationToken ct)
        => GetTopEntriesAsync(requestedPages, "TRENDING", ct);

    /// <summary>
    /// Fetches a StashDB scene ranking and resolves it against local Stash IDs.
    /// Both modes share HTTP transport and local matching code. Each invocation
    /// fetches fresh remote ranking pages. Only the two known GraphQL enum values
    /// may reach the API; never interpolate an arbitrary request parameter.
    /// </summary>
    public async Task<IReadOnlyList<StashTrendingEntry>> GetTopEntriesAsync(
        int requestedPages, string rankingSort, CancellationToken ct)
    {
        if (rankingSort is not ("TRENDING" or "POPULARITY"))
            throw new ArgumentOutOfRangeException(nameof(rankingSort));

        var cfg = Plugin.Instance?.Configuration;
        var enabled = rankingSort == "POPULARITY"
            ? cfg?.EnableStashDbPopularityRow == true
            : cfg?.EnableStashDbTrendingRow == true;
        if (cfg is null || !cfg.Enabled || !enabled || string.IsNullOrWhiteSpace(cfg.StashEndpoint))
            throw new InvalidOperationException("Enable " + rankingSort + " and configure Stash first.");

        var stashUri = StashGraphQlUri(cfg.StashEndpoint);
        var connection = await PostAsync(stashUri, StashConnectionQuery, new { }, cfg.StashApiKey,
            "Stash", ct).ConfigureAwait(false);
        var boxes = connection["configuration"]?["general"]?["stashBoxes"] as JArray
            ?? throw new InvalidOperationException("Stash did not return its Stash-box connections.");
        var key = boxes.OfType<JObject>()
            .FirstOrDefault(b => IsOfficialStashDbEndpoint(b.Value<string>("endpoint")))?.Value<string>("api_key");
        if (string.IsNullOrWhiteSpace(key))
            throw new InvalidOperationException("Configure StashDB and its API key in Stash Metadata Providers.");

        var ranks = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        int pages = Math.Clamp(requestedPages, 1, 10);
        for (int page = 1; page <= pages; page++)
        {
            ct.ThrowIfCancellationRequested();
            var ranking = await GetRankingAsync(key, page, rankingSort, ct)
                .ConfigureAwait(false);
            // A ranking with no entries cannot be a useful prepared index. Fail
            // rather than accidentally overwriting an existing good snapshot.
            if (page == 1 && ranking.Ids.Count == 0)
                throw new InvalidOperationException("StashDB returned an empty " + rankingSort + " ranking; the last prepared index was kept.");
            for (int index = 0; index < ranking.Ids.Count; index++)
                ranks.TryAdd(ranking.Ids[index], ((page - 1) * PageSize) + index + 1);
            _logger.LogDebug("JFToStashSync: {Sort} index: ranking page {Page}/{Pages}, {Total} unique scenes.",
                rankingSort, page, pages, ranks.Count);
            if (ranking.Ids.Count < PageSize || (long)page * PageSize >= ranking.TotalCount)
                break;
        }

        if (ranks.Count == 0)
            return Array.Empty<StashTrendingEntry>();

        var matches = await FindLocalMatchesAsync(stashUri, cfg.StashApiKey, ranks, ct).ConfigureAwait(false);
        _logger.LogDebug("JFToStashSync: {Sort} index: matched {Matches} local Stash scenes out of {Remote} ranked scenes.",
            rankingSort, matches.Count, ranks.Count);
        return matches;
    }

    /// <summary>
    /// Fetches a separate StashDB POPULARITY ranking for every configured gender.
    /// Resolves StashDB identifiers to local Stash performer identifiers using
    /// verified StashDB stash_ids. This method never returns API credentials.
    /// </summary>
    public async Task<IReadOnlyList<StashPerformerPopularityEntry>> GetPopularPerformersAsync(
        IReadOnlyCollection<string> genders, int requestedPages, CancellationToken ct)
    {
        var cfg = Plugin.Instance?.Configuration;
        if (cfg is null || !cfg.Enabled || !cfg.EnableStashDbPerformerPopularityRows ||
            string.IsNullOrWhiteSpace(cfg.StashEndpoint))
            throw new InvalidOperationException("Enable performer Popularity and configure Stash first.");

        var allowed = new HashSet<string>(new[] {
            "FEMALE", "INTERSEX", "MALE", "NON_BINARY", "TRANSGENDER_FEMALE", "TRANSGENDER_MALE"
        }, StringComparer.Ordinal);
        if (genders.Any(g => !allowed.Contains(g)))
            throw new ArgumentOutOfRangeException(nameof(genders));

        var stashUri = StashGraphQlUri(cfg.StashEndpoint);
        var connection = await PostAsync(stashUri, StashConnectionQuery, new { }, cfg.StashApiKey,
            "Stash", ct).ConfigureAwait(false);
        var boxes = connection["configuration"]?["general"]?["stashBoxes"] as JArray
            ?? throw new InvalidOperationException("Stash did not return its Stash-box connections.");
        var key = boxes.OfType<JObject>()
            .FirstOrDefault(b => IsOfficialStashDbEndpoint(b.Value<string>("endpoint")))?.Value<string>("api_key");
        if (string.IsNullOrWhiteSpace(key))
            throw new InvalidOperationException("Configure StashDB and its API key in Stash Metadata Providers.");

        var ranked = new Dictionary<string, (string Gender, int Rank)>(StringComparer.OrdinalIgnoreCase);
        var pages = Math.Clamp(requestedPages, 1, 10);
        foreach (var gender in genders.Distinct(StringComparer.Ordinal))
        {
            for (var page = 1; page <= pages; page++)
            {
                ct.ThrowIfCancellationRequested();
                var data = await PostAsync(new Uri(StashDbGraphQl), RemotePerformersQuery,
                    new { input = new { sort = "POPULARITY", direction = "DESC", page, per_page = PageSize, gender } },
                    key, "StashDB", ct).ConfigureAwait(false);
                var result = data["queryPerformers"] as JObject
                    ?? throw new InvalidOperationException("StashDB returned an unexpected performer ranking response.");
                var performers = result["performers"] as JArray
                    ?? throw new InvalidOperationException("StashDB returned an invalid performer list.");
                var total = result.Value<int?>("count")
                    ?? throw new InvalidOperationException("StashDB returned no performer count.");
                if (page == 1 && performers.Count == 0 && total > 0)
                    throw new InvalidOperationException("StashDB returned an empty performer ranking page.");
                if (total < 0 || performers.Count > PageSize)
                    throw new InvalidOperationException("StashDB returned invalid performer pagination.");
                for (var i = 0; i < performers.Count; i++)
                {
                    var entry = performers[i] as JObject;
                    var id = entry?.Value<string>("id");
                    var returnedGender = entry?.Value<string>("gender");
                    if (string.IsNullOrWhiteSpace(id) ||
                        !string.Equals(returnedGender, gender, StringComparison.OrdinalIgnoreCase))
                        continue; // Strict category verification, independent of the remote filter.
                    ranked.TryAdd(id, (gender, (page - 1) * PageSize + i + 1));
                }
                _logger.LogDebug("JFToStashSync: performer Popularity {Gender}: page {Page}/{Pages} read ({Count} entries).",
                    gender, page, pages, performers.Count);
                if (performers.Count < PageSize || (long)page * PageSize >= total) break;
            }
        }
        if (ranked.Count == 0) return Array.Empty<StashPerformerPopularityEntry>();

        // Query Stash in bounded chunks to avoid an oversized GraphQL ID filter.
        var matched = new Dictionary<string, StashPerformerPopularityEntry>(StringComparer.OrdinalIgnoreCase);
        var identifiers = ranked.Keys.ToArray();
        for (var start = 0; start < identifiers.Length; start += 100)
        {
            ct.ThrowIfCancellationRequested();
            var chunk = identifiers.Skip(start).Take(100).ToArray();
            for (var page = 1; page <= MaxLocalPages; page++)
            {
                var data = await PostAsync(stashUri, LocalPerformersQuery,
                    new {
                        filter = new { page, per_page = LocalPageSize, sort = "id", direction = "ASC" },
                        performer_filter = new {
                            stash_ids_endpoint = new { stash_ids = chunk, modifier = "EQUALS" }
                        }
                    }, cfg.StashApiKey, "Stash", ct).ConfigureAwait(false);
                var result = data["findPerformers"] as JObject
                    ?? throw new InvalidOperationException("Stash did not return matching performers.");
                var localPerformers = result["performers"] as JArray
                    ?? throw new InvalidOperationException("Stash returned an invalid performer list.");
                var total = result.Value<int?>("count")
                    ?? throw new InvalidOperationException("Stash returned no performer count.");
                if (total < 0) throw new InvalidOperationException("Stash returned an invalid performer count.");
                foreach (var performer in localPerformers.OfType<JObject>())
                {
                    var localId = performer.Value<string>("id");
                    if (string.IsNullOrWhiteSpace(localId) || performer["stash_ids"] is not JArray links)
                        continue;
                    foreach (var link in links.OfType<JObject>())
                    {
                        var externalId = link.Value<string>("stash_id");
                        if (!IsOfficialStashDbEndpoint(link.Value<string>("endpoint")) ||
                            string.IsNullOrWhiteSpace(externalId) || !ranked.TryGetValue(externalId, out var rank))
                            continue;
                        var row = new StashPerformerPopularityEntry(localId, rank.Gender, rank.Rank);
                        // A local Stash performer should occupy only its highest ranking slot.
                        if (!matched.TryGetValue(localId, out var old) || row.Rank < old.Rank)
                            matched[localId] = row;
                    }
                }
                if ((long)page * LocalPageSize >= total || localPerformers.Count == 0) break;
                if (page == MaxLocalPages)
                    throw new InvalidOperationException("Stash performer match safety page limit exceeded.");
            }
        }
        _logger.LogDebug("JFToStashSync: performer Popularity matched {Matches} Stash performers of {Ranked} ranked entries.",
            matched.Count, ranked.Count);
        return matched.Values.OrderBy(x => x.Gender, StringComparer.Ordinal).ThenBy(x => x.Rank).ToArray();
    }

    private async Task<RankingPage> GetRankingAsync(string apiKey, int page, string rankingSort, CancellationToken ct)
    {
        // Deliberately request the current ranking for every refresh, including
        // a manual rerun. A failed request aborts the index build; the previous
        // prepared-index file remains untouched by the scheduled task.
        var remote = await PostAsync(new Uri(StashDbGraphQl), RemoteTrendingQuery,
            new { input = new { sort = rankingSort, direction = "DESC", page, per_page = PageSize } },
            apiKey, "StashDB", ct).ConfigureAwait(false);
        var queryScenes = remote["queryScenes"] as JObject
            ?? throw new InvalidOperationException("StashDB returned an unexpected ranking response.");
        var remoteScenes = queryScenes["scenes"] as JArray
            ?? throw new InvalidOperationException("StashDB returned an unexpected scene list.");
        var totalCount = queryScenes.Value<int?>("count")
            ?? throw new InvalidOperationException("StashDB returned no scene count.");
        var ids = new List<string>(remoteScenes.Count);
        foreach (var token in remoteScenes)
        {
            var sceneId = token?["id"]?.Value<string>();
            if (string.IsNullOrWhiteSpace(sceneId))
                throw new InvalidOperationException("StashDB returned an invalid scene identifier.");
            ids.Add(sceneId);
        }
        if (ids.Count > PageSize || totalCount < 0 || (totalCount > 0 && ids.Count == 0))
            throw new InvalidOperationException("StashDB returned invalid ranking pagination data.");

        _logger.LogDebug("JFToStashSync: {Sort} page {Page}: fetched fresh ranking from StashDB.", rankingSort, page);
        return new RankingPage(totalCount, ids);
    }

    private async Task<IReadOnlyList<StashTrendingEntry>> FindLocalMatchesAsync(
        Uri stashUri, string stashApiKey, IReadOnlyDictionary<string, int> ranks, CancellationToken ct)
    {
        var results = new List<StashTrendingEntry>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // Do not use INCLUDES for stash_ids_endpoint: Stash accepts EQUALS for a
        // list of IDs, then we verify the actual endpoint for every returned ID.
        var sceneFilter = new
        {
            stash_ids_endpoint = new { stash_ids = ranks.Keys.ToArray(), modifier = "EQUALS" }
        };
        for (var page = 1; page <= MaxLocalPages; page++)
        {
            var local = await PostAsync(stashUri, LocalScenesQuery,
                new
                {
                    filter = new { page, per_page = LocalPageSize, sort = "id", direction = "ASC" },
                    scene_filter = sceneFilter
                },
                stashApiKey, "Stash", ct).ConfigureAwait(false);
            var findScenes = local["findScenes"] as JObject
                ?? throw new InvalidOperationException("Stash did not return matching scenes.");
            var scenes = findScenes["scenes"] as JArray
                ?? throw new InvalidOperationException("Stash returned an invalid scene list.");
            var count = findScenes.Value<int?>("count")
                ?? throw new InvalidOperationException("Stash returned no scene count.");
            if (count < 0)
            {
                throw new InvalidOperationException("Stash returned an invalid scene count.");
            }

            foreach (var scene in scenes.OfType<JObject>())
            {
                var localId = scene.Value<string>("id");
                if (string.IsNullOrWhiteSpace(localId) || scene["stash_ids"] is not JArray sceneIds)
                {
                    continue;
                }
                foreach (var link in sceneIds.OfType<JObject>())
                {
                    var externalId = link.Value<string>("stash_id");
                    if (IsOfficialStashDbEndpoint(link.Value<string>("endpoint")) &&
                        !string.IsNullOrWhiteSpace(externalId) && ranks.TryGetValue(externalId, out var rank) &&
                        seen.Add(localId))
                    {
                        results.Add(new StashTrendingEntry(localId, rank));
                    }
                }
            }

            if (page * LocalPageSize >= count || scenes.Count == 0)
            {
                return results.OrderBy(entry => entry.Rank).ThenBy(entry => entry.StashSceneId, StringComparer.Ordinal).ToArray();
            }
        }
        throw new InvalidOperationException("Too many Stash matches for one Trending page.");
    }

    private async Task<JObject> PostAsync(Uri uri, string query, object variables,
        string? apiKey, string service, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, uri);
        request.Headers.TryAddWithoutValidation("Accept", "application/json");
        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            request.Headers.TryAddWithoutValidation("ApiKey", apiKey);
        }
        request.Content = new StringContent(JsonConvert.SerializeObject(new { query, variables }),
            Encoding.UTF8, "application/json");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));

        try
        {
            using var response = await _httpClient.SendAsync(request, timeout.Token).ConfigureAwait(false);
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                throw new InvalidOperationException(service + " rejected the API key or permissions.");
            }
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                throw new InvalidOperationException(service + " rate limit reached. Try again later.");
            }
            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException(service + " returned HTTP " + (int)response.StatusCode + ".");
            }
            if (response.Content.Headers.ContentLength is > 2_000_000)
            {
                throw new InvalidOperationException(service + " returned a response that is too large.");
            }
            var jsonText = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
            if (jsonText.Length > 2_000_000)
            {
                throw new InvalidOperationException(service + " returned a response that is too large.");
            }
            var document = JObject.Parse(jsonText);
            if (document["errors"] is JArray { Count: > 0 })
            {
                throw new InvalidOperationException(service + " returned a GraphQL error. Verify its API and settings.");
            }
            return document["data"] as JObject
                ?? throw new InvalidOperationException(service + " returned an invalid GraphQL response.");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new InvalidOperationException(service + " did not respond within 30 seconds.");
        }
        catch (HttpRequestException)
        {
            throw new InvalidOperationException(service + " is unreachable from the Jellyfin server.");
        }
        catch (JsonException)
        {
            throw new InvalidOperationException(service + " returned invalid JSON.");
        }
    }

    private static Uri StashGraphQlUri(string stashEndpoint)
    {
        var baseUrl = stashEndpoint.Trim().TrimEnd('/');
        var address = baseUrl.EndsWith("/graphql", StringComparison.OrdinalIgnoreCase)
            ? baseUrl : baseUrl + "/graphql";
        if (!Uri.TryCreate(address, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            throw new InvalidOperationException("The configured Stash URL is not a valid HTTP(S) address.");
        }
        return uri;
    }

    private static bool IsOfficialStashDbEndpoint(string? value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri))
        {
            return false;
        }
        return uri.Scheme == Uri.UriSchemeHttps &&
            uri.Host.Equals("stashdb.org", StringComparison.OrdinalIgnoreCase) &&
            (uri.IsDefaultPort || uri.Port == 443) &&
            uri.AbsolutePath.TrimEnd('/').Equals("/graphql", StringComparison.Ordinal) &&
            uri.Query.Length == 0 && uri.Fragment.Length == 0 && uri.UserInfo.Length == 0;
    }
}

public sealed record StashPerformerPopularityEntry(string StashPerformerId, string Gender, int Rank);
