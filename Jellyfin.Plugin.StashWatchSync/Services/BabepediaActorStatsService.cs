using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace JFToStashSync.Services;

public sealed class BabepediaActorStat
{
    public string PersonId { get; set; } = string.Empty;
    public string PersonName { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
    public double? Rating { get; set; }
    public int? Rank { get; set; }
    public int? Favorites { get; set; }
    public int? Votes { get; set; }
    public DateTime FetchedAtUtc { get; set; }
    public bool FromCache { get; set; }
}

/// <summary>Signals Babepedia rate limiting or anti-bot challenge so a batch will stop.</summary>
internal sealed class BabepediaFetchDeferredException : InvalidOperationException
{
    public BabepediaFetchDeferredException(string message) : base(message) { }
}

internal sealed class BabepediaBatchAttempt
{
    public string PersonId { get; set; } = string.Empty;
    public DateTime RetryAfterUtc { get; set; }
    public string? FailureKind { get; set; } // MissingProfile or TemporaryError (legacy: null)
}

/// <summary>
/// Babepedia statistics shared by manual and scheduled fetches. No external requests on page views.
/// Successful and deferred attempts are stored on disk by Jellyfin Person ID.
/// </summary>
public sealed class BabepediaActorStatsService
{
    private static readonly SemaphoreSlim FetchGate = new(1, 1);
    private static DateTime _nextRequestUtc = DateTime.MinValue;
    private static DateTime _blockedUntilUtc = DateTime.MinValue;
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(200);
    private readonly ILibraryManager _library;
    private readonly StashClient _stash;
    private readonly ILogger<BabepediaActorStatsService> _logger;
    private readonly HttpClient _client;
    private readonly string _cachePath;
    private readonly string _attemptsPath;
    private readonly Dictionary<string, BabepediaBatchAttempt> _deferred = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, BabepediaActorStat> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _cacheLock = new();
    private readonly object _persistDeferredLock = new();
    private readonly object _persistStatsLock = new();

    public BabepediaActorStatsService(ILibraryManager library, StashClient stash,
        IApplicationPaths paths, ILogger<BabepediaActorStatsService> logger)
    {
        _library = library;
        _stash = stash;
        _logger = logger;
        _cachePath = Path.Combine(paths.DataPath, "JFToStashSync", "babepedia-actor-stats.json");
        _attemptsPath = Path.Combine(paths.DataPath, "JFToStashSync", "babepedia-attempts.json");
        _client = new HttpClient(new HttpClientHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
        }) { Timeout = TimeSpan.FromSeconds(22) };
        _client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (compatible; JFToStashSync/1.2.0.21; Babepedia ratings)");
        _client.DefaultRequestHeaders.Accept.ParseAdd("text/html, application/json;q=0.9, */*;q=0.5");
        Restore();
        RestoreDeferred();
    }

    private void Restore()
    {
        try
        {
            if (!File.Exists(_cachePath)) return;
            var json = File.ReadAllText(_cachePath);
            var items = JsonConvert.DeserializeObject<List<BabepediaActorStat>>(json);
            if (items is null || items.Count > 100000) return;
            lock (_cacheLock)
            {
                foreach (var stat in items)
                    if (Guid.TryParse(stat.PersonId, out var id) && IsBabepediaProfile(stat.Url))
                        _cache[id.ToString("N")] = stat;
            }
        }
        catch (Exception ex) { _logger.LogWarning(ex, "JFToStashSync: could not restore Babepedia cache."); }
    }

    private void RestoreDeferred()
    {
        try
        {
            if (!File.Exists(_attemptsPath)) return;
            var entries = JsonConvert.DeserializeObject<List<BabepediaBatchAttempt>>(File.ReadAllText(_attemptsPath));
            if (entries is null || entries.Count > 100000) return;
            lock (_cacheLock)
            {
                foreach (var attempt in entries)
                    if (Guid.TryParse(attempt.PersonId, out var id) &&
                        // Keep expired entries as a history of attempted profiles. Otherwise
                        // a 30-day retry becomes indistinguishable from first-time discovery.
                        attempt.RetryAfterUtc > DateTime.UtcNow.AddYears(-2) &&
                        attempt.RetryAfterUtc < DateTime.UtcNow.AddDays(32))
                        _deferred[id.ToString("N")] = attempt;
            }
        }
        catch (Exception ex) { _logger.LogWarning(ex, "JFToStashSync: could not restore Babepedia retry queue."); }
    }

    private void PersistDeferred()
    {
        // A manual test and a scheduled run may finish concurrently. Serialize the
        // temporary-file replacement separately from the small in-memory cache lock.
        lock (_persistDeferredLock)
        {
            var temp = _attemptsPath + ".tmp";
            List<BabepediaBatchAttempt> entries;
            lock (_cacheLock)
            {
                // Do not delete expired failures: this is the persistent "already checked"
                // marker required to keep negative-cache retries behind unseen actors.
                entries = _deferred.Values.ToList();
            }
            Directory.CreateDirectory(Path.GetDirectoryName(_attemptsPath)!);
            File.WriteAllText(temp, JsonConvert.SerializeObject(entries));
            File.Move(temp, _attemptsPath, true);
        }
    }

    /// <summary>
    /// True if a valid Babepedia profile has ever been cached for this actor.
    /// Used only for first-fetch priority in scheduled batches, independent of TTL.
    /// </summary>
    public bool HasCachedRecord(Guid personId)
    {
        lock (_cacheLock) return _cache.ContainsKey(personId.ToString("N"));
    }

    /// <summary>
    /// Priority 0: never checked; 1: expired successful cache;
    /// 2: confirmed missing; 3: temporary failure (including legacy attempts).
    /// State is read by GUID without materializing Jellyfin people.
    /// </summary>
    public int GetScheduledQueuePriority(Guid personId)
    {
        var key = personId.ToString("N");
        lock (_cacheLock)
        {
            if (_cache.ContainsKey(key)) return 1;
            if (!_deferred.TryGetValue(key, out var attempt)) return 0;
            return attempt.FailureKind == "MissingProfile" ? 2 : 3;
        }
    }

    /// <summary>Return true without hydrating Person when cached data is still valid or an earlier attempt is deferred.</summary>
    public bool SkipScheduledFetch(Guid personId, int cacheDays)
    {
        var key = personId.ToString("N");
        var days = cacheDays is 1 or 7 or 30 ? cacheDays : 7;
        lock (_cacheLock)
        {
            return (_cache.TryGetValue(key, out var stat) && stat.FetchedAtUtc > DateTime.UtcNow.AddDays(-days)) ||
                   (_deferred.TryGetValue(key, out var attempt) && attempt.RetryAfterUtc > DateTime.UtcNow);
        }
    }

    public bool IsSiteBlocked(out DateTime retryAfterUtc)
    {
        retryAfterUtc = _blockedUntilUtc;
        return DateTime.UtcNow < retryAfterUtc;
    }

    /// <summary>Defer unsuccessful automatic lookups; manual fetches are never blocked by these entries.</summary>
    public void DeferScheduledFetch(Guid personId, bool deferPersistence = false, bool missingProfile = false)
    {
        lock (_cacheLock)
        {
            _deferred[personId.ToString("N")] = new BabepediaBatchAttempt
            {
                PersonId = personId.ToString("N"),
                FailureKind = missingProfile ? "MissingProfile" : "TemporaryError",
                RetryAfterUtc = DateTime.UtcNow.AddDays(missingProfile ? 30 : 1)
            };
        }
        if (!deferPersistence) PersistDeferred();
    }

    private void Persist()
    {
        lock (_persistStatsLock)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_cachePath)!);
            var temp = _cachePath + ".tmp";
            List<BabepediaActorStat> entries;
            lock (_cacheLock) entries = _cache.Values.ToList();
            File.WriteAllText(temp, JsonConvert.SerializeObject(entries, Formatting.Indented));
            File.Move(temp, _cachePath, true);
        }
    }

    /// <summary>Flush both persistent stores after a scheduled batch (or on cancellation).</summary>
    public void FlushScheduledCache()
    {
        Persist();
        PersistDeferred();
    }

    public IReadOnlyList<BabepediaActorStat> GetCached()
    {
        lock (_cacheLock) return _cache.Values.Select(x => new BabepediaActorStat
        {
            PersonId = x.PersonId, PersonName = x.PersonName, Url = x.Url,
            Rating = x.Rating, Rank = x.Rank, Favorites = x.Favorites,
            Votes = x.Votes, FetchedAtUtc = x.FetchedAtUtc
        }).ToArray();
    }

    public async Task<BabepediaActorStat> FetchOneAsync(string personId, CancellationToken ct, bool deferPersistence = false)
    {
        var cfg = Plugin.Instance?.Configuration;
        if (cfg is null || !cfg.Enabled) throw new InvalidOperationException("JF To Stash Sync is disabled.");
        if (!Guid.TryParse(personId, out var guid)) throw new ArgumentException("Invalid Jellyfin Person ID.");
        if (_library.GetItemById(guid) is not Person person) throw new InvalidOperationException("Jellyfin Person not found.");
        var key = guid.ToString("N");

        await FetchGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            BabepediaActorStat? cached;
            lock (_cacheLock) _cache.TryGetValue(key, out cached);
            var days = cfg.BabepediaCacheDays is 1 or 7 or 30 ? cfg.BabepediaCacheDays : 7;
            if (cached is not null && cached.FetchedAtUtc > DateTime.UtcNow.AddDays(-days))
            {
                cached.FromCache = true;
                return cached;
            }
            if (DateTime.UtcNow < _blockedUntilUtc)
                throw new BabepediaFetchDeferredException("Babepedia has requested fewer requests. Try again after " + _blockedUntilUtc.ToString("u", CultureInfo.InvariantCulture));

            // Prefer a manually assigned Babepedia URL in Stash, never a guessed URL when one exists.
            var url = string.Empty;
            if (person.ProviderIds is not null && person.ProviderIds.TryGetValue("Stash", out var stashId)
                && !string.IsNullOrWhiteSpace(stashId))
            {
                var urls = await _stash.GetPerformerExternalUrlsAsync(stashId, ct).ConfigureAwait(false);
                if (urls is null) throw new InvalidOperationException("Cannot read performer links from Stash. Search was not attempted to avoid mismatches.");
                url = urls.Select(NormalizeBabepediaProfileUrl).FirstOrDefault(x => x.Length != 0) ?? string.Empty;
            }
            if (string.IsNullOrEmpty(url))
                url = await SearchByExactNameAsync(person.Name ?? string.Empty, ct).ConfigureAwait(false);

            var html = await RequestAsync(url, ct).ConfigureAwait(false);
            var stat = ParseHtml(html, key, person.Name ?? string.Empty, url);
            // Do not cache an HTTP 200 error page or unknown markup as a successful result.
            if (stat.Rating is null && stat.Rank is null && stat.Favorites is null)
                throw new InvalidOperationException("Babepedia profile returned no recognisable rating, rank or favorites. Cache unchanged.");
            bool hadDeferred;
            lock (_cacheLock)
            {
                _cache[key] = stat;
                hadDeferred = _deferred.Remove(key);
            }
            if (!deferPersistence)
            {
                Persist();
                if (hadDeferred) PersistDeferred();
            }
            _logger.LogDebug("JFToStashSync: Babepedia statistics cached for Jellyfin Person {PersonId}.", key);
            return stat;
        }
        finally { FetchGate.Release(); }
    }

    /// <summary>
    /// Fetch one known Top 100 page with the same shared 4-second throttle and
    /// anti-bot handling used for profiles. Never accepts an arbitrary URL.
    /// </summary>
    public async Task<string> FetchTop100PageAsync(string slug, CancellationToken ct)
    {
        if (!BabepediaTopPreparedIndexService.Categories.ContainsKey(slug))
            throw new ArgumentException("Unknown Babepedia Top 100 category.", nameof(slug));
        await FetchGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (DateTime.UtcNow < _blockedUntilUtc)
                throw new BabepediaFetchDeferredException("Babepedia is temporarily rate-limiting this server.");
            return await RequestAsync("https://www.babepedia.com/" + slug, ct).ConfigureAwait(false);
        }
        finally { FetchGate.Release(); }
    }

    private async Task<string> SearchByExactNameAsync(string name, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new InvalidOperationException("Person name is empty; cannot search Babepedia.");
        var json = await RequestAsync("https://www.babepedia.com/ajax-search.php?term=" + Uri.EscapeDataString(name.Replace('-', ' ')), ct).ConfigureAwait(false);
        JArray items;
        try { items = JArray.Parse(json); }
        catch (JsonException) { throw new InvalidOperationException("Babepedia search returned invalid JSON, possibly a challenge page."); }
        var matches = items.OfType<JObject>()
            .Where(x => !string.Equals((string?)x["type"], "search", StringComparison.OrdinalIgnoreCase))
            .Where(x => string.Equals(NormalizeName((string?)x["label"]), NormalizeName(name), StringComparison.OrdinalIgnoreCase))
            .Select(x => (string?)x["value"])
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (matches.Length == 0)
            throw new ActorProfileNotFoundException("No exact Babepedia name match found; add a Babepedia URL in Stash.");
        if (matches.Length > 1)
            throw new InvalidOperationException("Several Babepedia names match; add the correct profile URL in Stash.");
        var url = "https://www.babepedia.com/babe/" + Uri.EscapeDataString(matches[0]!.Replace(' ', '_'));
        if (!IsBabepediaProfile(url)) throw new InvalidOperationException("Invalid Babepedia search result.");
        return url;
    }

    private static string NormalizeName(string? value) => Regex.Replace((value ?? "").Trim().Replace('-', ' '), @"\s+", " ");

    private static string NormalizeBabepediaProfileUrl(string? url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return string.Empty;
        if (uri.UserInfo.Length != 0 || !(uri.Host.Equals("www.babepedia.com", StringComparison.OrdinalIgnoreCase) ||
            uri.Host.Equals("babepedia.com", StringComparison.OrdinalIgnoreCase)) ||
            !(uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp) ||
            !(uri.IsDefaultPort || uri.Port == 80 || uri.Port == 443) ||
            !uri.AbsolutePath.StartsWith("/babe/", StringComparison.OrdinalIgnoreCase) || uri.AbsolutePath.Length < 7)
            return string.Empty;
        return "https://www.babepedia.com" + uri.AbsolutePath;
    }

    private static bool IsBabepediaProfile(string? url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;
        return uri.Scheme == Uri.UriSchemeHttps &&
               (uri.Host.Equals("www.babepedia.com", StringComparison.OrdinalIgnoreCase) ||
                uri.Host.Equals("babepedia.com", StringComparison.OrdinalIgnoreCase)) &&
               uri.AbsolutePath.StartsWith("/babe/", StringComparison.OrdinalIgnoreCase) &&
               uri.AbsolutePath.Length > 6 && uri.UserInfo.Length == 0 && uri.Port == 443;
    }

    private async Task<string> RequestAsync(string url, CancellationToken ct)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps ||
            !(uri.Host.Equals("babepedia.com", StringComparison.OrdinalIgnoreCase) || uri.Host.Equals("www.babepedia.com", StringComparison.OrdinalIgnoreCase)) ||
            uri.Port != 443 || uri.UserInfo.Length != 0)
            throw new InvalidOperationException("Untrusted Babepedia URL blocked.");
        var delay = _nextRequestUtc - DateTime.UtcNow;
        if (delay > TimeSpan.Zero) await Task.Delay(delay, ct).ConfigureAwait(false);
        _nextRequestUtc = DateTime.UtcNow.AddSeconds(4); // global throttle, no parallel requests
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (response.StatusCode == (HttpStatusCode)429 || response.StatusCode == HttpStatusCode.ServiceUnavailable)
        {
            // Retry-After can be either a duration or an absolute HTTP-date.
            var header = response.Headers.RetryAfter;
            var retry = header?.Delta ??
                (header?.Date is DateTimeOffset retryDate ? retryDate - DateTimeOffset.UtcNow : TimeSpan.FromMinutes(5));
            if (retry < TimeSpan.FromMinutes(1)) retry = TimeSpan.FromMinutes(1);
            if (retry > TimeSpan.FromHours(2)) retry = TimeSpan.FromHours(2);
            _blockedUntilUtc = DateTime.UtcNow.Add(retry);
            throw new BabepediaFetchDeferredException("Babepedia is rate limiting requests (HTTP " + (int)response.StatusCode + "). Retry after " + _blockedUntilUtc.ToString("u", CultureInfo.InvariantCulture));
        }
        if (response.StatusCode == HttpStatusCode.Forbidden)
        {
            _blockedUntilUtc = DateTime.UtcNow.AddHours(3);
            throw new BabepediaFetchDeferredException("Babepedia returned HTTP 403 (access denied / Cloudflare challenge). No automatic bypass attempted.");
        }
        if (response.StatusCode == HttpStatusCode.NotFound &&
            uri.AbsolutePath.StartsWith("/babe/", StringComparison.OrdinalIgnoreCase))
            throw new ActorProfileNotFoundException("Babepedia performer profile returned HTTP 404.");
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException("Babepedia HTTP " + (int)response.StatusCode + "; cache not changed.");
        if (response.Content.Headers.ContentLength is > 2_000_000) throw new InvalidOperationException("Babepedia response is too large.");
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (body.Length > 2_000_000) throw new InvalidOperationException("Babepedia response is too large.");
        if (body.Contains("cf-chl-opt", StringComparison.OrdinalIgnoreCase) ||
            body.Contains("challenge-platform", StringComparison.OrdinalIgnoreCase) ||
            body.Contains("Just a moment...", StringComparison.OrdinalIgnoreCase))
        {
            _blockedUntilUtc = DateTime.UtcNow.AddHours(3);
            throw new BabepediaFetchDeferredException("Cloudflare challenge detected. No cache update or bypass attempted.");
        }
        return body;
    }

    internal static BabepediaActorStat ParseHtml(string html, string id, string name, string url)
    {
        var result = new BabepediaActorStat { PersonId = id, PersonName = name, Url = url, FetchedAtUtc = DateTime.UtcNow };
        // Proven selectors from Babepedia Advanced Filter & Badges.user.js:
        // .rating-global strong (score); .rating-global small (votes); .rating-fav div (favorites).
        var ratingBox = ElementWithClass(html, "rating-global");
        if (ratingBox is not null)
        {
            var strong = Capture(ratingBox, @"<strong\b[^>]*>(.*?)</strong>");
            var number = FirstNumber(StripTags(strong));
            if (double.TryParse(number, NumberStyles.Float, CultureInfo.InvariantCulture, out var rating) && rating >= 0 && rating <= 10)
                result.Rating = rating;
            var smalls = Regex.Matches(ratingBox, @"<small\b[^>]*>(.*?)</small>", RegexOptions.IgnoreCase | RegexOptions.Singleline, RegexTimeout);
            var votes = smalls.Cast<Match>().Select(m => StripTags(m.Groups[1].Value)).FirstOrDefault(x => x.Contains("vote", StringComparison.OrdinalIgnoreCase));
            votes ??= smalls.Count > 0 ? StripTags(smalls[smalls.Count - 1].Groups[1].Value) : null;
            result.Votes = ParseInt(votes);
        }
        var favBox = ElementWithClass(html, "rating-fav");
        if (favBox is not null)
        {
            var favorites = Capture(favBox, @"<div\b[^>]*>(.*?)</div>");
            result.Favorites = ParseInt(StripTags(favorites));
        }
        // Rank markup is not present in the supplied scraper. Treat only explicitly labelled
        // rank fields as reliable; do not mistake votes or the age for a rank.
        var rankData = Capture(html, @"<[^>]+(?:id|class)\s*=\s*['""][^'""]*(?:rank-position|babe-rank|ranking-position)[^'""]*['""][^>]*>(.*?)</[^>]+>");
        result.Rank = ParseInt(StripTags(rankData));
        if (result.Rank is null)
        {
            var rank = Regex.Match(StripTags(html), @"\b(?:ranked|ranking|rank)\s*(?:at|:|is)?\s*#?\s*(\d{1,7})\b", RegexOptions.IgnoreCase, RegexTimeout);
            if (rank.Success && int.TryParse(rank.Groups[1].Value, out var n) && n > 0 && n < 10_000_000) result.Rank = n;
        }
        return result;
    }

    private static string? ElementWithClass(string html, string cls)
    {
        var match = Regex.Match(html, @"<(?<tag>div|section|span)\b[^>]*\bclass\s*=\s*['""][^'""]*\b" + Regex.Escape(cls) + @"\b[^'""]*['""][^>]*>", RegexOptions.IgnoreCase | RegexOptions.Singleline, RegexTimeout);
        if (!match.Success) return null;
        // Enough of the surrounding markup for strong, small, and nested div extraction.
        return html.Substring(match.Index, Math.Min(3500, html.Length - match.Index));
    }
    private static string? Capture(string? html, string pattern)
    {
        if (string.IsNullOrEmpty(html)) return null;
        var match = Regex.Match(html, pattern, RegexOptions.IgnoreCase | RegexOptions.Singleline, RegexTimeout);
        return match.Success ? match.Groups[1].Value : null;
    }
    private static string StripTags(string? s) => WebUtility.HtmlDecode(Regex.Replace(s ?? string.Empty, "<[^>]+>", " ")).Trim();
    private static string? FirstNumber(string s)
    {
        var m = Regex.Match(s, @"\d+(?:[.,]\d+)?", RegexOptions.None, RegexTimeout);
        return m.Success ? m.Value.Replace(',', '.') : null;
    }
    private static int? ParseInt(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var m = Regex.Match(value, @"\d[\d,\.\s]*", RegexOptions.None, RegexTimeout);
        var digits = m.Success ? Regex.Replace(m.Value, @"[^\d]", "") : string.Empty;
        return int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : null;
    }
}
