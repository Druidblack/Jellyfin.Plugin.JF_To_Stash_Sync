using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using JFToStashSync.Configuration;
using Jellyfin.Data.Enums;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;

namespace JFToStashSync.Services;

/// <summary>
/// Persists Babepedia Top 100 list positions and matched Jellyfin Person IDs.
/// Browser requests never contact Stash or Babepedia and do not scan Persons.
/// </summary>
public sealed class BabepediaTopPreparedIndexService
{
    public static readonly IReadOnlyDictionary<string, string> Categories =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["pornstartop100"] = "Pornstars",
            ["milfpornstartop100"] = "MILF",
            ["asianpornstartop100"] = "Asian",
            ["latinapornstartop100"] = "Latina",
            ["slavicpornstartop100"] = "Slavic",
            ["ebonypornstartop100"] = "Ebony",
            ["caucasianpornstartop100"] = "Caucasian",
            ["onlyfanstop100"] = "OnlyFans"
        };

    private const int FormatVersion = 1;
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(500);
    private readonly ILibraryManager _library;
    private readonly StashClient _stash;
    private readonly BabepediaActorStatsService _babepedia;
    private readonly ILogger<BabepediaTopPreparedIndexService> _logger;
    private readonly string _path;
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private readonly object _readGate = new();
    private BabepediaTopSnapshot? _snapshot;

    public BabepediaTopPreparedIndexService(ILibraryManager library, StashClient stash,
        BabepediaActorStatsService babepedia, IApplicationPaths paths,
        ILogger<BabepediaTopPreparedIndexService> logger)
    {
        _library = library;
        _stash = stash;
        _babepedia = babepedia;
        _logger = logger;
        _path = Path.Combine(paths.DataPath, "JFToStashSync", "babepedia-top-index.json");
        try
        {
            if (File.Exists(_path))
            {
                var loaded = JsonConvert.DeserializeObject<BabepediaTopSnapshot>(File.ReadAllText(_path));
                if (loaded?.Version == FormatVersion && loaded.Pages.Count <= 8 && loaded.Items.Count <= 800)
                    _snapshot = loaded;
            }
        }
        catch (Exception ex) { _logger.LogWarning(ex, "JFToStashSync: cannot restore Babepedia Top 100 index."); }
    }

    public static IReadOnlyList<string> SelectedCategories(PluginConfiguration cfg)
    {
        var result = new List<string>(8);
        if (cfg.BabepediaTopPornstars) result.Add("pornstartop100");
        if (cfg.BabepediaTopMilf) result.Add("milfpornstartop100");
        if (cfg.BabepediaTopAsian) result.Add("asianpornstartop100");
        if (cfg.BabepediaTopLatina) result.Add("latinapornstartop100");
        if (cfg.BabepediaTopSlavic) result.Add("slavicpornstartop100");
        if (cfg.BabepediaTopEbony) result.Add("ebonypornstartop100");
        if (cfg.BabepediaTopCaucasian) result.Add("caucasianpornstartop100");
        if (cfg.BabepediaTopOnlyfans) result.Add("onlyfanstop100");
        return result;
    }

    public BabepediaTopSnapshot? GetSnapshot()
    {
        lock (_readGate) return _snapshot;
    }

    public bool IsCurrent(BabepediaTopSnapshot snapshot, PluginConfiguration cfg)
        => snapshot.Version == FormatVersion && snapshot.StashSignature == Signature(cfg)
           && snapshot.NameFallbackEnabled == cfg.BabepediaTopFallbackToJellyfinName;

    private static string Signature(PluginConfiguration cfg)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            (cfg.StashEndpoint ?? "").Trim().TrimEnd('/').ToLowerInvariant())));

    public async Task<int> RefreshAsync(IProgress<double> progress, CancellationToken ct)
    {
        var cfg = Plugin.Instance?.Configuration;
        if (cfg is null || !cfg.Enabled || !cfg.EnableBabepediaTopRows)
        {
            progress.Report(100);
            return 0;
        }
        var selected = SelectedCategories(cfg);
        if (selected.Count == 0) { progress.Report(100); return 0; }
        await _refreshGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Avoid contending with the other prepared-index scans in Jellyfin.
            await StashDbIndexRefreshGate.Shared.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var now = DateTimeOffset.UtcNow;
                var days = cfg.BabepediaTopCacheDays is 1 or 7 or 30 ? cfg.BabepediaTopCacheDays : 1;
                var existing = GetSnapshot();
                // Changing name-match policy must rebuild Person mappings, but should
                // NOT invalidate still-fresh downloaded Babepedia ranking pages.
                var validSignature = existing is not null && existing.Version == FormatVersion &&
                    existing.StashSignature == Signature(cfg);
                var pages = validSignature
                    ? existing!.Pages.ToDictionary(p => p.Category, StringComparer.OrdinalIgnoreCase)
                    : new Dictionary<string, BabepediaTopPage>(StringComparer.OrdinalIgnoreCase);
                var fetched = 0;
                for (var i = 0; i < selected.Count; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    var category = selected[i];
                    if (pages.TryGetValue(category, out var old) && old.Profiles.Count >= 10 &&
                        old.FetchedAtUtc > now.AddDays(-days))
                    {
                        progress.Report(5 + 40d * (i + 1) / selected.Count);
                        continue;
                    }
                    // Same global throttle, HTTP 429 / Retry-After and Cloudflare detection
                    // as the existing Babepedia profile service. Exceptions keep the old index.
                    var html = await _babepedia.FetchTop100PageAsync(category, ct).ConfigureAwait(false);
                    var profiles = ParseTop100(html);
                    if (profiles.Count < 10)
                        throw new InvalidOperationException("Babepedia Top 100 page " + category +
                            " had fewer than 10 recognizable entries; prior index preserved.");
                    pages[category] = new BabepediaTopPage
                    {
                        Category = category, FetchedAtUtc = now, Profiles = profiles
                    };
                    fetched++;
                    progress.Report(5 + 40d * (i + 1) / selected.Count);
                }
                var wanted = pages.Values.Where(p => selected.Contains(p.Category))
                    .SelectMany(page => page.Profiles.Select(profile => profile.ProfileKey))
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                // One Stash GraphQL traversal for ALL eight lists. Filter is performed
                // in Stash's database by 'performer_filter.url INCLUDES babepedia.com'.
                var linked = await _stash.GetBabepediaLinkedPerformersAsync(ct).ConfigureAwait(false);
                var byProfile = new Dictionary<string, StashLinkedPerformer>(StringComparer.OrdinalIgnoreCase);
                foreach (var person in linked)
                    foreach (var link in person.Urls)
                    {
                        var key = NormalizeProfile(link);
                        if (!string.IsNullOrEmpty(key) && wanted.Contains(key) && !byProfile.ContainsKey(key))
                            byProfile[key] = person;
                    }
                progress.Report(75);

                var byStash = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);
                // Prefer synced Jellyfin Person URL in Stash when it matches the configured
                // Jellyfin origin AND the Person still has the correct Stash provider ID.
                foreach (var performer in byProfile.Values.DistinctBy(x => x.StashId))
                {
                    foreach (var link in performer.Urls)
                    {
                        if (!TryJellyfinPersonId(link, cfg.JellyfinBaseUrl, out var id)) continue;
                        if (_library.GetItemById(id) is Person person &&
                            person.ProviderIds?.TryGetValue("Stash", out var stashId) == true &&
                            string.Equals(stashId, performer.StashId, StringComparison.OrdinalIgnoreCase))
                        {
                            byStash[performer.StashId] = id;
                            break;
                        }
                    }
                }
                foreach (var match in StashProviderIdBatchLookup.FindPeople(
                    _library, byProfile.Values.Select(p => p.StashId)
                        .Where(id => !byStash.ContainsKey(id)), ct))
                    byStash.TryAdd(match.StashId, match.ItemId);

                // Exact URL-based matches always win. Name fallback is opt-in and
                // only considered if a profile has NO corresponding Babepedia URL
                // in Stash. Never silently join two different linked profiles.
                var byName = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);
                if (cfg.BabepediaTopFallbackToJellyfinName)
                {
                    var reserved = byStash.Values.ToHashSet();
                    var linkedByStash = linked
                        .GroupBy(x => x.StashId, StringComparer.OrdinalIgnoreCase)
                        .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
                    var cachedNames = new Dictionary<string, Guid?>(StringComparer.Ordinal);
                    foreach (var profile in pages.Values
                        .Where(p => selected.Contains(p.Category))
                        .SelectMany(p => p.Profiles).DistinctBy(x => x.ProfileKey))
                    {
                        ct.ThrowIfCancellationRequested();
                        if (byProfile.ContainsKey(profile.ProfileKey)) continue;
                        var name = string.IsNullOrWhiteSpace(profile.Name)
                            ? profile.ProfileKey.Replace('_', ' ') : profile.Name;
                        var normalized = NormalizeActorName(name);
                        if (normalized.Length < 3) continue;
                        if (!cachedNames.TryGetValue(normalized, out var candidateId))
                        {
                            // Search the Jellyfin database, never fetch every Person.
                            // SearchTerm may be fuzzy: the final check is exact.
                            var result = _library.GetItemList(new InternalItemsQuery
                            {
                                Recursive = true,
                                IncludeItemTypes = new[] { BaseItemKind.Person },
                                SearchTerm = name.Trim(),
                                Limit = 100,
                                EnableTotalRecordCount = false
                            });
                            var exact = result.OfType<Person>()
                                .Where(x => NormalizeActorName(x.Name) == normalized)
                                .Take(2).ToArray();
                            candidateId = result.Count < 100 && exact.Length == 1 ? exact[0].Id : null;
                            cachedNames[normalized] = candidateId;
                        }
                        if (candidateId is not Guid id || reserved.Contains(id)) continue;
                        if (_library.GetItemById(id) is not Person person) continue;
                        // A Stash-linked performer with a *different* Babepedia URL
                        // must not be assigned by name to this chart entry.
                        if (person.ProviderIds?.TryGetValue("Stash", out var linkedId) == true &&
                            linkedByStash.TryGetValue(linkedId, out var linkedPerson) &&
                            linkedPerson.Urls.Any(url => NormalizeProfile(url) is { Length: > 0 } key &&
                                !string.Equals(key, profile.ProfileKey, StringComparison.OrdinalIgnoreCase)))
                            continue;
                        byName[profile.ProfileKey] = id;
                        reserved.Add(id);
                    }
                }

                var items = new List<BabepediaTopItem>();
                foreach (var category in selected)
                {
                    if (!pages.TryGetValue(category, out var page)) continue;
                    foreach (var profile in page.Profiles)
                    {
                        if (byProfile.TryGetValue(profile.ProfileKey, out var stash) &&
                            byStash.TryGetValue(stash.StashId, out var personId))
                            items.Add(new BabepediaTopItem(personId, category, profile.Rank));
                        else if (!byProfile.ContainsKey(profile.ProfileKey) &&
                            byName.TryGetValue(profile.ProfileKey, out var byNamePersonId))
                            items.Add(new BabepediaTopItem(byNamePersonId, category, profile.Rank));
                    }
                }
                progress.Report(90);
                var next = new BabepediaTopSnapshot
                {
                    Version = FormatVersion, StashSignature = Signature(cfg),
                    NameFallbackEnabled = cfg.BabepediaTopFallbackToJellyfinName,
                    UpdatedAtUtc = now,
                    Pages = pages.Values.OrderBy(p => p.Category).ToList(),
                    Items = items.GroupBy(x => (x.Category, x.PersonId))
                        .Select(g => g.OrderBy(x => x.Rank).First())
                        .OrderBy(x => x.Category).ThenBy(x => x.Rank).ToList()
                };
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                var temp = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try
                {
                    await File.WriteAllTextAsync(temp, JsonConvert.SerializeObject(next), ct)
                        .ConfigureAwait(false);
                    File.Move(temp, _path, true);
                }
                finally { if (File.Exists(temp)) File.Delete(temp); }
                lock (_readGate) _snapshot = next;
                progress.Report(100);
                _logger.LogInformation("JFToStashSync: Babepedia Top 100 refreshed ({Fetched} remote charts, {Matched} matched Jellyfin actors).",
                    fetched, next.Items.Count);
                return next.Items.Count;
            }
            finally { StashDbIndexRefreshGate.Shared.Release(); }
        }
        finally { _refreshGate.Release(); }
    }

    /// <summary>Conservative name normalization for opt-in fallback: no partial or fuzzy matching.</summary>
    private static string NormalizeActorName(string? name)
        => Regex.Replace(WebUtility.HtmlDecode(name ?? string.Empty)
            .Normalize(NormalizationForm.FormKC).Trim(), @"[\s_-]+", " ").ToUpperInvariant();

    /// <summary>Canonical /babe/slug identity for the primary exact match.</summary>
    public static string NormalizeProfile(string? url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            !string.Equals(uri.Scheme, "https", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(uri.Scheme, "http", StringComparison.OrdinalIgnoreCase)) return string.Empty;
        if (!string.Equals(uri.Host, "babepedia.com", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(uri.Host, "www.babepedia.com", StringComparison.OrdinalIgnoreCase)) return string.Empty;
        if (uri.UserInfo.Length != 0) return string.Empty;
        var path = Uri.UnescapeDataString(uri.AbsolutePath).TrimEnd('/');
        if (!path.StartsWith("/babe/", StringComparison.OrdinalIgnoreCase)) return string.Empty;
        var slug = path.Substring(6);
        if (slug.Length < 1 || slug.Contains('/')) return string.Empty;
        return slug.Replace(' ', '_').ToLowerInvariant();
    }

    public static List<BabepediaTopProfile> ParseTop100(string html)
    {
        if (string.IsNullOrWhiteSpace(html) || html.Length > 2_000_000)
            throw new InvalidOperationException("Invalid Babepedia Top 100 response.");
        var matches = Regex.Matches(html,
            @"<span\b[^>]*class\s*=\s*['\""][^'\""]*\bthumbshot\b[^'\""]*['\""][^>]*>(?<card>.*?)</span>",
            RegexOptions.IgnoreCase | RegexOptions.Singleline, RegexTimeout);
        var results = new List<BabepediaTopProfile>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match match in matches)
        {
            var body = match.Groups["card"].Value;
            var href = Regex.Match(body, @"<a\b[^>]*href\s*=\s*['\""](?<link>/babe/[^'\""?#]+)[^'\""]*['\""]",
                RegexOptions.IgnoreCase | RegexOptions.Singleline, RegexTimeout);
            var position = Regex.Match(body, @"<div\b[^>]*class\s*=\s*['\""][^'\""]*\bthumbtext\b[^'\""]*['\""][^>]*>\s*#\s*(?<rank>\d{1,3})\s*:",
                RegexOptions.IgnoreCase | RegexOptions.Singleline, RegexTimeout);
            if (!href.Success || !position.Success ||
                !int.TryParse(position.Groups["rank"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var rank) ||
                rank < 1 || rank > 100) continue;
            var key = NormalizeProfile("https://www.babepedia.com" + WebUtility.HtmlDecode(href.Groups["link"].Value));
            if (!string.IsNullOrEmpty(key) && seen.Add(key))
            {
                // The link text is the display name, unlike the normalized URL slug.
                var nameMatch = Regex.Match(body,
                    @"<div\b[^>]*class\s*=\s*['""'][^'""']*\bthumbtext\b[^'""']*['""'][^>]*>.*?<a\b[^>]*>(?<name>[^<]+)</a>",
                    RegexOptions.IgnoreCase | RegexOptions.Singleline, RegexTimeout);
                var name = nameMatch.Success ? WebUtility.HtmlDecode(nameMatch.Groups["name"].Value).Trim() :
                    key.Replace('_', ' ');
                results.Add(new BabepediaTopProfile { ProfileKey = key, Rank = rank, Name = name });
            }
        }
        return results.OrderBy(x => x.Rank).ToList();
    }

    private static bool TryJellyfinPersonId(string link, string? jellyfinBaseUrl, out Guid id)
    {
        id = Guid.Empty;
        if (!Uri.TryCreate(link, UriKind.Absolute, out var uri) ||
            !Uri.TryCreate(jellyfinBaseUrl, UriKind.Absolute, out var expected)) return false;
        if (uri.Scheme != expected.Scheme || uri.Host != expected.Host || uri.Port != expected.Port ||
            !uri.AbsolutePath.StartsWith(expected.AbsolutePath.TrimEnd('/'), StringComparison.OrdinalIgnoreCase)) return false;
        // Stash saved URL is typically /web/#/details?id=<guid>.
        var part = uri.Fragment.TrimStart('#');
        var question = part.IndexOf('?');
        var query = question >= 0 ? part[(question + 1)..] : uri.Query.TrimStart('?');
        foreach (var component in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var pair = component.Split('=', 2);
            if (pair.Length == 2 && pair[0].Equals("id", StringComparison.OrdinalIgnoreCase) &&
                Guid.TryParse(Uri.UnescapeDataString(pair[1]), out id)) return true;
        }
        id = Guid.Empty;
        return false;
    }
}

public sealed class BabepediaTopSnapshot
{
    public int Version { get; set; }
    public string StashSignature { get; set; } = string.Empty;
    public bool NameFallbackEnabled { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
    public List<BabepediaTopPage> Pages { get; set; } = new();
    public List<BabepediaTopItem> Items { get; set; } = new();
}
public sealed class BabepediaTopPage
{
    public string Category { get; set; } = string.Empty;
    public DateTimeOffset FetchedAtUtc { get; set; }
    public List<BabepediaTopProfile> Profiles { get; set; } = new();
}
public sealed class BabepediaTopProfile
{
    public string ProfileKey { get; set; } = string.Empty;
    public int Rank { get; set; }
    public string Name { get; set; } = string.Empty;
}
public sealed record BabepediaTopItem(Guid PersonId, string Category, int Rank);
