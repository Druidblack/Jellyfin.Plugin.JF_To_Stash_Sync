using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;

namespace JFToStashSync.Services;

/// <summary>Persisted statistics for one verified Jellyfin Person / Pornhub profile.</summary>
public sealed class PornhubActorStat
{
    public string PersonId { get; set; } = string.Empty;
    public string PersonName { get; set; } = string.Empty;
    public string ProfileUrl { get; set; } = string.Empty;
    public bool AwardWinner { get; set; }
    public bool Verified { get; set; }
    public int? ModelRank { get; set; }
    public int? WeeklyRank { get; set; }
    public int? MonthlyRank { get; set; }
    public int? LastMonthRank { get; set; }
    public int? YearlyRank { get; set; }
    public long? VideoViews { get; set; }
    public long? Subscribers { get; set; }
    public DateTime FetchedAtUtc { get; set; }
    // Increment when parsing rules change, so old cached results are refreshed.
    public int ParserVersion { get; set; }
    public bool FromCache { get; set; }
}

/// <summary>
/// Cached Pornhub profile statistics; external requests only from manual fetch or selected scheduled collection.
/// Uses the same verified profile resolution and rate gate as the existing artwork preview service.
/// </summary>
public sealed class PornhubActorStatsService
{
    private const int CurrentParserVersion = 3;
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(400);
    private readonly PornhubActorArtworkService _artwork;
    private readonly PornhubScheduledStateService _scheduledState;
    private readonly ILogger<PornhubActorStatsService> _logger;
    private readonly string _path;
    private readonly SemaphoreSlim _fetchGate = new(1, 1);
    private readonly object _lock = new();
    private readonly Dictionary<string, PornhubActorStat> _cache = new(StringComparer.OrdinalIgnoreCase);

    public PornhubActorStatsService(PornhubActorArtworkService artwork, PornhubScheduledStateService scheduledState, IApplicationPaths paths, ILogger<PornhubActorStatsService> logger)
    {
        _artwork = artwork;
        _scheduledState = scheduledState;
        _logger = logger;
        _path = Path.Combine(paths.DataPath, "JFToStashSync", "pornhub-actor-stats.json");
        try
        {
            if (!File.Exists(_path)) return;
            var rows = JsonConvert.DeserializeObject<List<PornhubActorStat>>(File.ReadAllText(_path));
            if (rows is null || rows.Count > 100000) return;
            foreach (var row in rows)
            {
                if (Guid.TryParse(row.PersonId, out var id) && PornhubActorArtworkService.NormalizeProfileUrl(row.ProfileUrl) is not null)
                    _cache[id.ToString("N")] = row;
            }
        }
        catch (Exception ex) { _logger.LogWarning(ex, "JFToStashSync: Pornhub statistics cache could not be read."); }
    }

    public IReadOnlyList<PornhubActorStat> GetCached()
    {
        // Do not expose a misleading rank parsed by an older implementation.
        lock (_lock) return _cache.Values.Where(row => row.ParserVersion == CurrentParserVersion).Select(Copy).ToArray();
    }

    /// <summary>
    /// Whether any successfully parsed rating has already been stored for this person.
    /// A result from an older parser still counts as an existing rating: parser upgrades
    /// must not outrank actors whose ratings have never been downloaded.
    /// </summary>
    public bool HasCachedRecord(string personId)
    {
        if (!Guid.TryParse(personId, out var id)) return false;
        lock (_lock) return _cache.ContainsKey(id.ToString("N"));
    }

    /// <summary>Cheap disk-cache freshness check for the scheduled task; no Pornhub calls.</summary>
    public bool IsFresh(string personId, int cacheDays)
    {
        if (!Guid.TryParse(personId, out var id)) return false;
        var days = cacheDays is 1 or 7 or 30 ? cacheDays : 7;
        lock (_lock)
            return _cache.TryGetValue(id.ToString("N"), out var row) &&
                   row.ParserVersion == CurrentParserVersion &&
                   row.FetchedAtUtc > DateTime.UtcNow.AddDays(-days);
    }

    public async Task<PornhubActorStat> FetchOneAsync(string personId, bool force, CancellationToken ct)
    {
        var cfg = Plugin.Instance?.Configuration;
        if (cfg is null || !cfg.Enabled) throw new InvalidOperationException("JF To Stash Sync is disabled.");
        if (!Guid.TryParse(personId, out var guid)) throw new ArgumentException("Invalid Jellyfin Person ID.");
        var key = guid.ToString("N");
        await _fetchGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var days = cfg.PornhubStatsCacheDays is 1 or 7 or 30 ? cfg.PornhubStatsCacheDays : 7;
            lock (_lock)
            {
                if (!force && _cache.TryGetValue(key, out var cached) && cached.ParserVersion == CurrentParserVersion && cached.FetchedAtUtc > DateTime.UtcNow.AddDays(-days))
                {
                    var result = Copy(cached);
                    result.FromCache = true;
                    return result;
                }
            }

            var (url, html, name) = await _artwork.FetchProfileHtmlForStatsAsync(key, ct).ConfigureAwait(false);
            var resultNew = ParseProfile(html, key, name, url);
            resultNew.FetchedAtUtc = DateTime.UtcNow;
            // Preserve the previous valid record if parsing/network fails.
            lock (_lock)
            {
                var hadPrevious = _cache.TryGetValue(key, out var previous);
                _cache[key] = resultNew;
                try { PersistLocked(); }
                catch
                {
                    if (hadPrevious && previous is not null) _cache[key] = previous;
                    else _cache.Remove(key);
                    throw;
                }
            }
            // Successful manual fetches must also release a previous negative-cache entry.
            _scheduledState.ClearDefer(key, statistics: true);
            _logger.LogInformation("JFToStashSync: Pornhub actor statistics saved for Person {PersonId}.", key);
            return Copy(resultNew);
        }
        finally { _fetchGate.Release(); }
    }

    private void PersistLocked()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var temp = _path + ".tmp";
        File.WriteAllText(temp, JsonConvert.SerializeObject(_cache.Values, Formatting.Indented));
        File.Move(temp, _path, true);
    }

    private static PornhubActorStat Copy(PornhubActorStat a) => new()
    {
        PersonId = a.PersonId, PersonName = a.PersonName, ProfileUrl = a.ProfileUrl,
        AwardWinner = a.AwardWinner, Verified = a.Verified, ModelRank = a.ModelRank,
        WeeklyRank = a.WeeklyRank, MonthlyRank = a.MonthlyRank, LastMonthRank = a.LastMonthRank,
        YearlyRank = a.YearlyRank, VideoViews = a.VideoViews, Subscribers = a.Subscribers,
        FetchedAtUtc = a.FetchedAtUtc, FromCache = a.FromCache, ParserVersion = a.ParserVersion
    };

    internal static PornhubActorStat ParseProfile(string html, string personId, string personName, string url)
    {
        if (string.IsNullOrWhiteSpace(html) || html.Length > 2_000_000)
            throw new InvalidOperationException("Pornhub returned an empty or oversized profile.");
        var stat = new PornhubActorStat { PersonId = personId, PersonName = personName, ProfileUrl = url, ParserVersion = CurrentParserVersion };
        // Pornhub may render trophies elsewhere on the page (recommendations,
        // site navigation, etc.).  Only the badges belonging to the profile's
        // own name/header identify this performer.
        var badges = FindProfileBadgeBlock(html);
        if (badges is not null)
        {
            stat.AwardWinner = HasSpanClass(badges, "trophyPornStar") ||
                HasSpanWithClasses(badges, "trophyChannel", "award-icon");
            // Both site variants represent verification: the old
            // 'verifiedPornstar' and the newer 'verified-icon'.
            stat.Verified = HasSpanClass(badges, "verifiedPornstar") ||
                HasSpanClass(badges, "verified-icon");
        }

        var ranking = FindDivByClass(html, "rankingInfo");
        if (ranking is not null)
        {
            var boxes = FindDirectDivsByClass(ranking, "infoBox");
            var detailPosition = 0;
            foreach (var box in boxes)
            {
                // The first infoBox is the overall/model rank. The four rankDetails
                // elements hold week/month/last month/year, in that order.
                // This also works when <span class="big"> contains a nested
                // <span class="icon rank-down"> before the actual number.
                var firstTag = Regex.Match(box, @"^<div\b[^>]*>", RegexOptions.IgnoreCase, RegexTimeout);
                var classList = firstTag.Success ? GetAttribute(firstTag.Value, "class") ?? "" : "";
                var isDetail = classList.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
                    .Contains("rankDetails", StringComparer.OrdinalIgnoreCase);
                var title = ExtractTagText(box, "div", "title").ToLowerInvariant();
                var rank = ParseInt(ExtractTagText(box, "span", "big"));
                if (!isDetail)
                {
                    // Never substitute the weekly rank for a missing model rank.
                    stat.ModelRank ??= rank;
                    continue;
                }

                // Use the label when present; retain the documented order as a
                // fallback for localized or empty labels.
                if (title.Contains("недель") || title.Contains("weekly") || title.Contains("week rank"))
                    stat.WeeklyRank = rank;
                else if (title.Contains("последний месяц") || title.Contains("last month"))
                    stat.LastMonthRank = rank;
                else if (title.Contains("месячн") || title.Contains("monthly"))
                    stat.MonthlyRank = rank;
                else if (title.Contains("годов") || title.Contains("yearly") || title.Contains("annual"))
                    stat.YearlyRank = rank;
                else
                {
                    switch (detailPosition)
                    {
                        case 0: stat.WeeklyRank = rank; break;
                        case 1: stat.MonthlyRank = rank; break;
                        case 2: stat.LastMonthRank = rank; break;
                        case 3: stat.YearlyRank = rank; break;
                    }
                }
                detailPosition++;
            }
        }

        var info = FindDivByClass(html, "infoBoxes");
        if (info is not null)
        {
            foreach (var box in FindDirectDivsByClass(info, "infoBox"))
            {
                var title = ExtractTagText(box, "div", "title").ToLowerInvariant();
                var firstTag = Regex.Match(box, @"<div\b[^>]*>", RegexOptions.IgnoreCase, RegexTimeout);
                var tooltip = firstTag.Success ? WebUtility.HtmlDecode(GetAttribute(firstTag.Value, "data-title") ?? "") : "";
                var lower = tooltip.ToLowerInvariant();
                var big = ExtractTagText(box, "span", "big");
                if (title.Contains("просмотр") || title.Contains("video views") || lower.Contains("video views") || lower.Contains("просмотры видео"))
                    stat.VideoViews = ParseCounter(tooltip) ?? ParseCounter(big);
                else if (title.Contains("абонент") || title.Contains("подписчик") || title.Contains("subscriber") || lower.Contains("subscriber") || lower.Contains("абонент"))
                    stat.Subscribers = ParseCounter(tooltip) ?? ParseCounter(big);
            }
        }

        if (!stat.AwardWinner && !stat.Verified && stat.ModelRank is null && stat.WeeklyRank is null &&
            stat.MonthlyRank is null && stat.LastMonthRank is null && stat.YearlyRank is null &&
            stat.VideoViews is null && stat.Subscribers is null)
            throw new InvalidOperationException("The Pornhub profile does not contain recognizable rankings or badges. The old cache was retained.");
        return stat;
    }

    private static string? FindProfileBadgeBlock(string html)
    {
        // The profile header has a name wrapper containing an <h1 itemprop="name">
        // followed by <div class="badge-username">.  Do not fall back to
        // searching the full HTML: that can mistake someone else's trophy for
        // the current performer's award.
        foreach (Match wrapper in Regex.Matches(html, @"<div\b[^>]*>", RegexOptions.IgnoreCase, RegexTimeout))
        {
            var classes = GetAttribute(wrapper.Value, "class") ?? "";
            if (!classes.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
                .Contains("name", StringComparer.OrdinalIgnoreCase)) continue;
            var block = ReadBalancedDiv(html, wrapper.Index);
            if (block is null || !Regex.IsMatch(block,
                    @"<h1\b[^>]*\bitemprop\s*=\s*(?:""name""|'name')[^>]*>",
                    RegexOptions.IgnoreCase, RegexTimeout)) continue;
            return FindDivByClass(block, "badge-username");
        }
        return null;
    }

    private static bool HasSpanWithClasses(string html, string first, string second)
    {
        foreach (Match m in Regex.Matches(html, @"<span\b[^>]{0,2000}>", RegexOptions.IgnoreCase, RegexTimeout))
        {
            var classes = (GetAttribute(m.Value, "class") ?? "")
                .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (classes.Contains(first, StringComparer.OrdinalIgnoreCase) &&
                classes.Contains(second, StringComparer.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    private static bool HasSpanClass(string html, string className)
    {
        foreach (Match m in Regex.Matches(html, @"<span\b[^>]{0,2000}>", RegexOptions.IgnoreCase, RegexTimeout))
        {
            var classes = GetAttribute(m.Value, "class") ?? "";
            if (classes.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Contains(className, StringComparer.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    private static string? GetAttribute(string tag, string attribute)
    {
        var found = Regex.Match(tag, @"\b" + Regex.Escape(attribute) + @"\s*=\s*(?:""([^""<>]*)""|'([^'<>]*)')",
            RegexOptions.IgnoreCase, RegexTimeout);
        return found.Success ? found.Groups[1].Success ? found.Groups[1].Value : found.Groups[2].Value : null;
    }

    private static string? FindDivByClass(string html, string className)
    {
        foreach (Match m in Regex.Matches(html, @"<div\b[^>]*>", RegexOptions.IgnoreCase, RegexTimeout))
        {
            var classes = GetAttribute(m.Value, "class") ?? "";
            if (classes.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Contains(className, StringComparer.OrdinalIgnoreCase))
                return ReadBalancedDiv(html, m.Index);
        }
        return null;
    }

    private static string? ReadBalancedDiv(string html, int start)
    {
        var depth = 0;
        foreach (Match tag in Regex.Matches(html.Substring(start), @"</?div\b[^>]*>", RegexOptions.IgnoreCase, RegexTimeout))
        {
            depth += tag.Value.StartsWith("</", StringComparison.Ordinal) ? -1 : 1;
            if (depth == 0) return html.Substring(start, tag.Index + tag.Length);
        }
        return null;
    }

    private static IReadOnlyList<string> FindDirectDivsByClass(string html, string className)
    {
        var rows = new List<string>();
        foreach (Match m in Regex.Matches(html, @"<div\b[^>]*>", RegexOptions.IgnoreCase, RegexTimeout))
        {
            var classes = GetAttribute(m.Value, "class") ?? "";
            if (!classes.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Contains(className, StringComparer.OrdinalIgnoreCase)) continue;
            var block = ReadBalancedDiv(html, m.Index);
            if (block is not null) rows.Add(block);
        }
        return rows;
    }

    private static string ExtractTagText(string html, string tag, string className)
    {
        // Selecting the class-qualified opening tag first avoids consuming an
        // outer <div class="infoBox"> while looking for its nested .title.
        // Counting matching opening/closing tags also preserves the text that
        // comes AFTER <span class="icon rank-down"></span> within .big.
        var tagPattern = @"</?" + Regex.Escape(tag) + @"\b[^>]*>";
        foreach (Match opening in Regex.Matches(html, "<" + Regex.Escape(tag) + @"\b[^>]*>",
            RegexOptions.IgnoreCase, RegexTimeout))
        {
            var classes = GetAttribute(opening.Value, "class") ?? "";
            if (!classes.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
                .Contains(className, StringComparer.OrdinalIgnoreCase)) continue;

            var depth = 0;
            foreach (Match token in Regex.Matches(html.Substring(opening.Index), tagPattern,
                RegexOptions.IgnoreCase, RegexTimeout))
            {
                if (!token.Value.StartsWith("</", StringComparison.Ordinal))
                {
                    depth++;
                }
                else if (--depth == 0)
                {
                    var contentStart = opening.Index + opening.Length;
                    var contentLength = token.Index - opening.Length;
                    return contentLength < 0 ? string.Empty : CleanText(html.Substring(contentStart, contentLength));
                }
            }
            return string.Empty;
        }
        return string.Empty;
    }

    private static string CleanText(string html) => Regex.Replace(
        WebUtility.HtmlDecode(Regex.Replace(html, @"<[^>]*>", " ", RegexOptions.Singleline, RegexTimeout)),
        @"\s+", " ", RegexOptions.None, RegexTimeout).Trim();

    private static int? ParseInt(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || !Regex.IsMatch(value, @"\d", RegexOptions.None, RegexTimeout)) return null;
        var digits = Regex.Replace(value, @"[^0-9]", "", RegexOptions.None, RegexTimeout);
        return int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n > 0 ? n : null;
    }

    private static long? ParseCounter(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        // Prefer exact totals from data-title, for example: "Video views: 1,835,898,112".
        var exact = Regex.Match(value, @":\s*([0-9][0-9, .\u00a0]*)", RegexOptions.None, RegexTimeout);
        if (exact.Success)
        {
            var digits = Regex.Replace(exact.Groups[1].Value, @"[^0-9]", "", RegexOptions.None, RegexTimeout);
            if (long.TryParse(digits, out var count)) return count;
        }
        // Fallback for visible abbreviated counters, e.g. 1.8B or 3.7M.
        var compact = Regex.Match(value.Trim(), @"^([0-9]+(?:[.,][0-9]+)?)\s*([kKmMbB])?$", RegexOptions.None, RegexTimeout);
        if (!compact.Success) return null;
        if (!double.TryParse(compact.Groups[1].Value.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var number)) return null;
        var unit = compact.Groups[2].Value.ToUpperInvariant();
        var factor = unit == "B" ? 1_000_000_000d : unit == "M" ? 1_000_000d : unit == "K" ? 1_000d : 1d;
        return number >= 0 && number * factor <= long.MaxValue ? (long)Math.Round(number * factor) : null;
    }
}
