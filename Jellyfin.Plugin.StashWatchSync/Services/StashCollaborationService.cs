using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Newtonsoft.Json.Linq;

namespace JFToStashSync.Services;

/// <summary>Bounded Stash collaboration snapshot, never cached with a Jellyfin user's permissions.</summary>
public sealed class StashCollaborationService
{
    private readonly StashClient _stash;
    private readonly ILibraryManager _library;
    private readonly ConcurrentDictionary<string, (DateTime Until, Task<Snapshot> Loading)> _cache = new();
    private static readonly Regex LinkId = new(@"(?:[?&]id=|/Items/)(?<id>[0-9a-fA-F-]{32,36})(?:[&#/?]|$)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly HashSet<string> Genders = new(StringComparer.OrdinalIgnoreCase) {
        "FEMALE", "MALE", "INTERSEX", "NON_BINARY", "TRANSGENDER_FEMALE", "TRANSGENDER_MALE"
    };

    public StashCollaborationService(StashClient stash, ILibraryManager library)
    {
        _stash = stash;
        _library = library;
    }

    public async Task<Snapshot> GetSnapshotAsync(string stashId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(stashId)) throw new ArgumentException("Stash ID is empty.");
        // A completed snapshot lives 15 minutes; a failed request is never cached.
        var cacheKey = (Plugin.Instance?.Configuration.StashEndpoint ?? string.Empty) + ":" + stashId;
        var entry = _cache.AddOrUpdate(cacheKey,
            _ => (DateTime.UtcNow.AddMinutes(15), FetchAsync(stashId)),
            (_, prior) => prior.Until > DateTime.UtcNow ? prior :
                (DateTime.UtcNow.AddMinutes(15), FetchAsync(stashId)));
        try { return await entry.Loading.WaitAsync(ct).ConfigureAwait(false); }
        catch
        {
            if (entry.Loading.IsFaulted || entry.Loading.IsCanceled)
                _cache.TryRemove(cacheKey, out _);
            throw;
        }
    }

    private async Task<Snapshot> FetchAsync(string stashId)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(80));
        var (rows, truncated) = await _stash.GetCollaborationScenesAsync(stashId, timeout.Token).ConfigureAwait(false);
        var list = new List<CollaborationScene>();
        foreach (var row in rows)
        {
            var sceneId = (string?)row["id"];
            if (string.IsNullOrWhiteSpace(sceneId)) continue;
            var people = new List<CollaborationPerson>();
            if (row["performers"] is JArray performers)
                foreach (var performer in performers.OfType<JObject>())
                {
                    var personId = (string?)performer["id"];
                    if (string.IsNullOrWhiteSpace(personId)) continue;
                    people.Add(new CollaborationPerson(personId,
                        (string?)performer["name"] ?? string.Empty,
                        ((string?)performer["gender"] ?? string.Empty).ToUpperInvariant(),
                        (string?)performer["image_path"] ?? string.Empty,
                        performer["urls"] is JArray links ? links.Values<string>().OfType<string>().ToArray() : Array.Empty<string>()));
                }
            // Defensive check in case a legacy Stash build interprets INCLUDES differently.
            if (!people.Any(p => p.Id == stashId)) continue;
            list.Add(new CollaborationScene(sceneId, (string?)row["title"] ?? string.Empty,
                (string?)row["date"] ?? string.Empty,
                row["urls"] is JArray urls ? urls.Values<string>().OfType<string>().ToArray() : Array.Empty<string>(),
                people));
        }
        return new Snapshot(list, truncated);
    }

    public Person? AllowedPerson(string? personGuid, Guid userGuid)
    {
        return Guid.TryParse(personGuid, out var id) &&
            _library.GetItemById<Person>(id, userGuid) is Person person ? person : null;
    }

    public string? ResolveAccessibleVideoId(CollaborationScene scene, Guid userGuid)
    {
        foreach (var link in scene.Urls)
        {
            var match = LinkId.Match(link);
            if (!match.Success || !Guid.TryParse(match.Groups["id"].Value, out var id)) continue;
            var item = _library.GetItemById<BaseItem>(id, userGuid);
            if (item is not Video) continue;
            // The generic BaseItem in Jellyfin 12 has ProviderIds, not GetProviderId.
            if (item.ProviderIds is not null &&
                item.ProviderIds.TryGetValue("Stash", out var idFromJellyfin) &&
                !string.IsNullOrWhiteSpace(idFromJellyfin) &&
                !string.Equals(idFromJellyfin, scene.Id, StringComparison.Ordinal)) continue;
            return id.ToString("N");
        }
        return null;
    }

    public static IReadOnlyCollection<string> ChosenGenders(JFToStashSync.Configuration.PluginConfiguration cfg)
    {
        var genders = new List<string>();
        if (cfg.CoStarsGenderFemale) genders.Add("FEMALE");
        if (cfg.CoStarsGenderMale) genders.Add("MALE");
        if (cfg.CoStarsGenderIntersex) genders.Add("INTERSEX");
        if (cfg.CoStarsGenderNonBinary) genders.Add("NON_BINARY");
        if (cfg.CoStarsGenderTransgenderFemale) genders.Add("TRANSGENDER_FEMALE");
        if (cfg.CoStarsGenderTransgenderMale) genders.Add("TRANSGENDER_MALE");
        return genders;
    }

    public static bool GenderAllowed(string gender, IReadOnlyCollection<string> selected) =>
        selected.Any(s => string.Equals(s, gender, StringComparison.OrdinalIgnoreCase)) && Genders.Contains(gender);

    public static string? JellyfinIdFromUrls(IReadOnlyList<string> urls)
    {
        foreach (var url in urls)
        {
            var match = LinkId.Match(url);
            if (match.Success && Guid.TryParse(match.Groups["id"].Value, out var id)) return id.ToString("N");
        }
        return null;
    }

    public sealed record CollaborationPerson(string Id, string Name, string Gender, string ImagePath, IReadOnlyList<string> Urls);
    public sealed record CollaborationScene(string Id, string Title, string Date, IReadOnlyList<string> Urls, IReadOnlyList<CollaborationPerson> People);
    public sealed record Snapshot(IReadOnlyList<CollaborationScene> Scenes, bool Truncated);
}
