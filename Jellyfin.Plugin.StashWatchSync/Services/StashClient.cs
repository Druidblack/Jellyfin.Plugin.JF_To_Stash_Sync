using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace StashWatchSync.Services;

/// <summary>
/// Minimal Stash GraphQL client used only for syncing activity.
/// </summary>
public sealed class StashClient
{
    private const string ProviderIdKey = "Stash";

    private const string FindScenesQuery = @"query findScenes($filter: FindFilterType, $scene_filter: SceneFilterType) {
  findScenes(filter: $filter, scene_filter: $scene_filter) {
    scenes {
      id
      files { path }
    }
  }
}";

    private const string FindSceneQuery = @"query findScene($id: ID!) {
  findScene(id: $id) {
    id
    title
    resume_time
    play_duration
  }
}";

    private const string SceneUpdateMutation = @"mutation sceneUpdate($input: SceneUpdateInput!) {
  sceneUpdate(input: $input) {
    id
    title
    resume_time
    play_duration
  }
}";

    private const string PerformerUpdateMutation = @"mutation performerUpdate($input: PerformerUpdateInput!) {
  performerUpdate(input: $input) {
    id
    favorite
  }
}";

    private const string SceneUpdateMinimalMutation = @"mutation sceneUpdate($input: SceneUpdateInput!) {
  sceneUpdate(input: $input) {
    id
  }
}";

    // Introspection helpers to discover the correct signatures for play-count mutations.
    // Some Stash builds changed these signatures over time; introspection keeps us version-agnostic.
    private const string IntrospectMutationTypeQuery = @"query IntrospectMutationType($typeName: String!) {
  __type(name: $typeName) {
    fields {
      name
      args {
        name
        type { kind name ofType { kind name ofType { kind name ofType { kind name } } } }
      }
      type { kind name ofType { kind name ofType { kind name ofType { kind name } } } }
    }
  }
}";

    private const string IntrospectInputTypeQuery = @"query IntrospectInputType($name: String!) {
  __type(name: $name) {
    inputFields {
      name
      type { kind name ofType { kind name ofType { kind name ofType { kind name } } } }
    }
  }
}";

    private const string IncrementPlayCountField = "sceneIncrementPlayCount";
    private const string DecrementPlayCountField = "sceneDecrementPlayCount";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<StashClient> _logger;

    private readonly ConcurrentDictionary<string, PlayCountMutationSpec?> _playCountMutationSpecs = new(StringComparer.Ordinal);

    public StashClient(IHttpClientFactory httpClientFactory, ILogger<StashClient> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public bool IsConfigured()
    {
        var cfg = Plugin.Instance?.Configuration;
        return cfg is not null && cfg.Enabled && !string.IsNullOrWhiteSpace(cfg.StashEndpoint);
    }

    public async Task<string?> ResolveSceneIdAsync(BaseItem item, CancellationToken ct)
    {
        // 1) Prefer provider id
        if (item.ProviderIds is not null && item.ProviderIds.TryGetValue(ProviderIdKey, out var providerId) && !string.IsNullOrWhiteSpace(providerId))
        {
            _logger.LogDebug("StashWatchSync: resolved scene via providerId. itemId={ItemId} sceneId={SceneId}", item.Id, providerId);
            return providerId;
        }

        // 2) Fallback: search by path
        var cfg = Plugin.Instance?.Configuration;
        if (cfg is null || !cfg.EnablePathFallback)
        {
            return null;
        }

        var mappedPath = MapPath(item.Path, cfg.JellyfinPathPrefix, cfg.StashPathPrefix);
        if (string.IsNullOrWhiteSpace(mappedPath))
        {
            _logger.LogDebug("StashWatchSync: cannot resolve scene by path (empty path). itemId={ItemId}", item.Id);
            return null;
        }

        _logger.LogDebug(
            "StashWatchSync: resolving scene by path. itemId={ItemId} mappedPath={MappedPath} fullPath={FullPath}",
            item.Id,
            mappedPath,
            cfg.SearchByFullPath);

        return await FindSceneIdByPathAsync(mappedPath!, cfg.SearchByFullPath, ct).ConfigureAwait(false);
    }


    /// <summary>
    /// Resolve a Stash scene id using a cached provider id and/or a file path (used by background flush).
    /// </summary>
    public async Task<string?> ResolveSceneIdAsync(string? providerId, string? itemPath, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(providerId))
        {
            return providerId;
        }

        var cfg = Plugin.Instance?.Configuration;
        if (cfg is null || !cfg.EnablePathFallback)
        {
            return null;
        }

        var mappedPath = MapPath(itemPath, cfg.JellyfinPathPrefix, cfg.StashPathPrefix);
        if (string.IsNullOrWhiteSpace(mappedPath))
        {
            return null;
        }

        return await FindSceneIdByPathAsync(mappedPath!, cfg.SearchByFullPath, ct).ConfigureAwait(false);
    }


    /// <summary>
    /// Sync played state to Stash.
    /// </summary>
    /// <param name="sceneId">Stash scene id</param>
    /// <param name="playCountDelta">
    /// How many times the item was newly played since the last sync (usually 1).
    /// If 0, only "watched" (resume_time=0) will be updated.
    /// </param>
    public async Task<bool> SyncPlayedAsync(string sceneId, int playCountDelta, double? playedDurationSeconds, CancellationToken ct)
    {
        var cfg = Plugin.Instance?.Configuration;
        // Watched syncing is always enabled (UI option removed).
        if (cfg is null || !cfg.Enabled)
        {
            return false;
        }

        // 1) Increment play count if requested by caller.
        // Play-count syncing is always enabled (UI option removed).
        if (playCountDelta > 0)
        {
            var incOk = await IncrementPlayCountAsync(sceneId, playCountDelta, ct).ConfigureAwait(false);
            if (!incOk)
            {
                _logger.LogWarning("StashWatchSync: failed to increment play count in Stash. sceneId={SceneId} delta={Delta}", sceneId, playCountDelta);
            }
        }

        // 2) Mark watched in Stash: resume_time=0.
        // Optionally... add to play_duration.
        // (play_duration is cumulative time watched in Stash.)
        double? newPlayDuration = null;
        GraphQlModels.SceneActivity? before = null;
        if (cfg.SyncPlayDuration && playedDurationSeconds is not null && playedDurationSeconds.Value > 0)
        {
            before = await GetSceneActivityAsync(sceneId, ct).ConfigureAwait(false);
            var current = before?.PlayDuration ?? 0d;
            newPlayDuration = current + playedDurationSeconds.Value;
        }

        var ok = await UpdateSceneAsync(sceneId, resumeTimeSeconds: 0, playDurationSeconds: newPlayDuration, ct).ConfigureAwait(false);
        if (!ok)
        {
            return false;
        }

        var after = await GetSceneActivityAsync(sceneId, ct).ConfigureAwait(false);
        if (newPlayDuration is not null)
        {
            _logger.LogInformation(
                "StashWatchSync: scene activity updated. sceneId={SceneId} title=\"{Title}\" resume_time={Resume} play_duration {Before} -> {After} (+{Added})",
                sceneId,
                after?.Title ?? string.Empty,
                after?.ResumeTime,
                before?.PlayDuration ?? 0d,
                after?.PlayDuration ?? 0d,
                playedDurationSeconds ?? 0d);
        }
        else
        {
            _logger.LogInformation(
                "StashWatchSync: scene activity updated. sceneId={SceneId} title=\"{Title}\" resume_time={Resume}",
                sceneId,
                after?.Title ?? string.Empty,
                after?.ResumeTime);
        }
        return true;
    }

    public Task<bool> SyncResumeAsync(string sceneId, double resumeSeconds, CancellationToken ct)
    {
        var cfg = Plugin.Instance?.Configuration;
        if (cfg is null || !cfg.Enabled || !cfg.SyncResumePosition)
        {
            return Task.FromResult(false);
        }

        // Only resume_time update.
        return UpdateSceneAsync(sceneId, resumeTimeSeconds: resumeSeconds, playDurationSeconds: null, ct);
    }

    /// <summary>
    /// Add watched seconds to Stash play_duration without marking the scene as fully watched.
    /// Used for unfinished playback sessions.
    /// </summary>
    public async Task<bool> AddPlayDurationAsync(string sceneId, double addSeconds, CancellationToken ct)
    {
        var cfg = Plugin.Instance?.Configuration;
        if (cfg is null || !cfg.Enabled || !cfg.SyncPlayDuration || !cfg.SyncInProgressPlayDuration)
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(sceneId) || addSeconds <= 0.5)
        {
            return false;
        }

        GraphQlModels.SceneActivity? before = await GetSceneActivityAsync(sceneId, ct).ConfigureAwait(false);
        var current = before?.PlayDuration ?? 0d;
        var newPlayDuration = current + addSeconds;

        var ok = await UpdateSceneAsync(sceneId, resumeTimeSeconds: null, playDurationSeconds: newPlayDuration, ct).ConfigureAwait(false);
        if (!ok)
        {
            _logger.LogWarning("StashWatchSync: failed to update play_duration (in-progress). sceneId={SceneId} add={Add}", sceneId, addSeconds);
            return false;
        }

        var after = await GetSceneActivityAsync(sceneId, ct).ConfigureAwait(false);
        _logger.LogInformation(
            "StashWatchSync: in-progress play_duration updated. sceneId={SceneId} title='{Title}' play_duration {Before} -> {After} (+{Added})",
            sceneId,
            after?.Title ?? string.Empty,
            current,
            after?.PlayDuration ?? current,
            addSeconds);

        return true;
    }



    private async Task<GraphQlModels.SceneActivity?> GetSceneActivityAsync(string sceneId, CancellationToken ct)
    {
        var result = await SendAsync<GraphQlModels.FindSceneData>(FindSceneQuery, new { id = sceneId }, ct).ConfigureAwait(false);
        return result?.Data?.FindScene;
    }

    private async Task<string?> FindSceneIdByPathAsync(string mappedPath, bool fullPath, CancellationToken ct)
    {
        string search = fullPath ? mappedPath : Path.GetFileName(mappedPath);

        // Prefer exact match when using full path.
        var variables = new
        {
            filter = new { per_page = 20, page = 1 },
            scene_filter = new
            {
                path = new { value = search, modifier = fullPath ? "EQUALS" : "INCLUDES" }
            }
        };

        var resp = await SendAsync<GraphQlModels.FindScenesData>(FindScenesQuery, variables, ct).ConfigureAwait(false);
        var scenes = resp?.Data?.FindScenes?.Scenes;
        if (scenes is null || scenes.Count == 0)
        {
            return null;
        }

        string normSearchFull = NormalizePath(mappedPath);
        string normSearchFile = NormalizePath(Path.GetFileName(mappedPath));

        // Choose best matching scene.
        foreach (var s in scenes)
        {
            if (string.IsNullOrWhiteSpace(s.Id) || s.Files is null)
            {
                continue;
            }

            foreach (var f in s.Files)
            {
                var fp = NormalizePath(f.Path);
                if (string.IsNullOrWhiteSpace(fp))
                {
                    continue;
                }

                if (fullPath)
                {
                    if (string.Equals(fp, normSearchFull, StringComparison.Ordinal))
                    {
                        return s.Id;
                    }
                }
                else
                {
                    // filename match
                    if (fp.EndsWith(normSearchFile, StringComparison.Ordinal))
                    {
                        return s.Id;
                    }
                }
            }
        }

        // Fallback: first scene.
        return scenes[0].Id;
    }

    private async Task<bool> UpdateSceneAsync(string sceneId, double? resumeTimeSeconds, double? playDurationSeconds, CancellationToken ct)
    {
        var cfg = Plugin.Instance?.Configuration;
        if (cfg is null || !cfg.Enabled)
        {
            return false;
        }

        var input = new JObject
        {
            ["id"] = sceneId,
        };

        if (resumeTimeSeconds is not null)
        {
            input["resume_time"] = resumeTimeSeconds.Value;
        }

        if (playDurationSeconds is not null)
        {
            input["play_duration"] = playDurationSeconds.Value;
        }

        var resp = await SendAsync<GraphQlModels.SceneUpdateData>(SceneUpdateMutation, new { input }, ct).ConfigureAwait(false);

        if (resp is null)
        {
            return false;
        }

        if (resp.Errors is not null && resp.Errors.Length > 0)
        {
            return false;
        }

        var updated = resp.Data?.SceneUpdate;
        if (updated is not null)
        {
            _logger.LogDebug(
                "StashWatchSync: sceneUpdate response. sceneId={SceneId} title=\"{Title}\" resume={Resume}",
                updated.Id,
                updated.Title ?? string.Empty,
                updated.ResumeTime);
        }

        return updated is not null && !string.IsNullOrWhiteSpace(updated.Id);
    }

    

/// <summary>
/// Set performer favorite state in Stash.
/// </summary>
public async Task<bool> SetPerformerFavoriteAsync(string performerId, bool isFavorite, CancellationToken ct)
{
    if (string.IsNullOrWhiteSpace(performerId))
    {
        return false;
    }

    var input = new JObject
    {
        ["id"] = performerId,
        ["favorite"] = isFavorite
    };

    var resp = await SendAsync<GraphQlModels.PerformerUpdateData>(PerformerUpdateMutation, new { input }, ct).ConfigureAwait(false);
    if (resp is null)
    {
        return false;
    }

    if (resp.Errors is not null && resp.Errors.Length > 0)
    {
        return false;
    }

    var updated = resp.Data?.PerformerUpdate;
    return updated is not null && !string.IsNullOrWhiteSpace(updated.Id);
}

    public async Task<bool> SetSceneRatingAsync(string sceneId, int rating5, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(sceneId))
        {
            return false;
        }

        // Stash typically stores rating on a 0..100 scale (rating100).
        // We still try both rating100 and rating for compatibility.
        rating5 = Math.Clamp(rating5, 0, 5);

        var ok = await TryUpdateSceneRatingAsync(sceneId, rating5, preferRating100: true, ct).ConfigureAwait(false);
        if (ok)
        {
            return true;
        }

        return await TryUpdateSceneRatingAsync(sceneId, rating5, preferRating100: false, ct).ConfigureAwait(false);
    }

    private async Task<bool> TryUpdateSceneRatingAsync(string sceneId, int rating5, bool preferRating100, CancellationToken ct)
    {
        var cfg = Plugin.Instance?.Configuration;
        if (cfg is null || !cfg.Enabled)
        {
            return false;
        }

        var input = new JObject
        {
            ["id"] = sceneId,
        };

        if (preferRating100)
        {
            // Map 0..5 => 0..100 in steps of 20.
            input["rating100"] = rating5 <= 0 ? 0 : rating5 * 20;
        }
        else
        {
            input["rating"] = rating5;
        }

        var resp = await SendAsync<GraphQlModels.SceneUpdateData>(SceneUpdateMinimalMutation, new { input }, ct).ConfigureAwait(false);
        if (resp is null)
        {
            return false;
        }

        if (resp.Errors is not null && resp.Errors.Length > 0)
        {
            // Errors are already logged by SendAsync.
            return false;
        }

        var updated = resp.Data?.SceneUpdate;
        return updated is not null && !string.IsNullOrWhiteSpace(updated.Id);
    }

private async Task<GraphQlModels.GraphQlResponse<T>?> SendAsync<T>(string query, object variables, CancellationToken ct)
    {
        var cfg = Plugin.Instance?.Configuration;
        if (cfg is null || !cfg.Enabled || string.IsNullOrWhiteSpace(cfg.StashEndpoint))
        {
            return null;
        }

        var graphqlUrl = NormalizeGraphQlUrl(cfg.StashEndpoint);

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, graphqlUrl);
            req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            // Some Stash versions advertise this response type.
            req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/graphql-response+json"));

            if (!string.IsNullOrWhiteSpace(cfg.StashApiKey))
            {
                // Stash uses API key header.
                req.Headers.TryAddWithoutValidation("ApiKey", cfg.StashApiKey);
            }

            var payload = new { query, variables };
            req.Content = new StringContent(JsonConvert.SerializeObject(payload), Encoding.UTF8, "application/json");

            var client = _httpClientFactory.CreateClient();
            using var res = await client.SendAsync(req, ct).ConfigureAwait(false);
            var body = await res.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

            if (!res.IsSuccessStatusCode)
            {
                _logger.LogWarning("Stash GraphQL HTTP {StatusCode}: {Body}", (int)res.StatusCode, Truncate(body, 500));
                return null;
            }

            var parsed = JsonConvert.DeserializeObject<GraphQlModels.GraphQlResponse<T>>(body);
            if (parsed?.Errors is not null && parsed.Errors.Length > 0)
            {
                _logger.LogWarning("Stash GraphQL errors: {Message}", string.Join(" | ", parsed.Errors.Select(e => e.Message)));
            }

            return parsed;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Stash GraphQL request failed");
            return null;
        }
    }

    private static string NormalizeGraphQlUrl(string endpoint)
    {
        var e = (endpoint ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(e))
        {
            return string.Empty;
        }

        e = e.TrimEnd('/');

        // Allow both base URL (http://host:9999) and full URL (http://host:9999/graphql).
        if (e.EndsWith("/graphql", StringComparison.OrdinalIgnoreCase))
        {
            return e;
        }

        return e + "/graphql";
    }

    private static string? MapPath(string? jellyfinPath, string jellyfinPrefix, string stashPrefix)
    {
        if (string.IsNullOrWhiteSpace(jellyfinPath))
        {
            return null;
        }

        var p = NormalizePath(jellyfinPath);

        if (!string.IsNullOrWhiteSpace(jellyfinPrefix) && !string.IsNullOrWhiteSpace(stashPrefix))
        {
            var jp = NormalizePath(jellyfinPrefix).TrimEnd('/');
            var sp = NormalizePath(stashPrefix).TrimEnd('/');

            if (!string.IsNullOrWhiteSpace(jp) && p.StartsWith(jp + "/", StringComparison.Ordinal))
            {
                p = sp + p.Substring(jp.Length);
            }
        }

        return p;
    }

    private static string NormalizePath(string? p)
    {
        if (string.IsNullOrWhiteSpace(p))
        {
            return string.Empty;
        }

        // Normalize to forward slashes for Stash.
        return p.Replace('\\', '/').Trim();
    }

    private static string Truncate(string s, int max)
        => s.Length <= max ? s : s.Substring(0, max) + "…";

    
    /// <summary>
    /// Increment Stash scene play_count without touching resume_time or play_duration.
    /// Used to record a playback session even when the scene is not fully watched in Jellyfin.
    /// </summary>
    public async Task<bool> IncrementPlayCountOnlyAsync(string sceneId, int delta, CancellationToken ct)
    {
        var cfg = Plugin.Instance?.Configuration;
        // Play-count syncing is always enabled (UI option removed).
        if (cfg is null || !cfg.Enabled)
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(sceneId) || delta <= 0)
        {
            return false;
        }

        return await IncrementPlayCountAsync(sceneId, delta, ct).ConfigureAwait(false);
    }

    private async Task<bool> IncrementPlayCountAsync(string sceneId, int delta, CancellationToken ct)
        => await ChangePlayCountAsync(IncrementPlayCountField, sceneId, delta, ct).ConfigureAwait(false);

    private async Task<bool> DecrementPlayCountAsync(string sceneId, int delta, CancellationToken ct)
        => await ChangePlayCountAsync(DecrementPlayCountField, sceneId, delta, ct).ConfigureAwait(false);

    private async Task<bool> ChangePlayCountAsync(string mutationField, string sceneId, int delta, CancellationToken ct)
    {
        if (delta <= 0)
        {
            return true;
        }

        var spec = await GetPlayCountMutationSpecAsync(mutationField, ct).ConfigureAwait(false);
        if (spec is null)
        {
            // Fallback to a few common signatures if introspection is unavailable.
            return await TryCommonPlayCountMutationsAsync(mutationField, sceneId, delta, ct).ConfigureAwait(false);
        }

        // If the schema supports passing a count, do it in a single call.
        if (spec.SupportsDelta)
        {
            return await ExecutePlayCountMutationAsync(spec, sceneId, delta, ct).ConfigureAwait(false);
        }

        // Otherwise call it delta times.
        var remaining = Math.Min(delta, 50); // safety cap
        for (int i = 0; i < remaining; i++)
        {
            var ok = await ExecutePlayCountMutationAsync(spec, sceneId, 1, ct).ConfigureAwait(false);
            if (!ok)
            {
                return false;
            }
        }

        return true;
    }

    private async Task<bool> ExecutePlayCountMutationAsync(PlayCountMutationSpec spec, string sceneId, int delta, CancellationToken ct)
    {
        var (query, variables) = spec.Build(sceneId, delta);
        var resp = await SendAsync<JObject>(query, variables, ct).ConfigureAwait(false);
        if (resp is null)
        {
            return false;
        }

        if (resp.Errors is not null && resp.Errors.Length > 0)
        {
            return false;
        }

        _logger.LogDebug("StashWatchSync: {Mutation} executed. sceneId={SceneId} delta={Delta}", spec.FieldName, sceneId, delta);
        return true;
    }

    private async Task<bool> TryCommonPlayCountMutationsAsync(string mutationField, string sceneId, int delta, CancellationToken ct)
    {
        // Most common signatures seen across Stash versions.
        var candidates = new (string Query, object Vars)[]
        {
            // Newer Stash: returns Int, argument is typically "id" (ID!).
            ($"mutation($id: ID!) {{ {mutationField}(id: $id) }}", new { id = sceneId }),

            // Older/alternate schemas: try common argument names.
            ($"mutation($scene_id: ID!) {{ {mutationField}(scene_id: $scene_id) }}", new { scene_id = sceneId }),
            ($"mutation($sceneId: ID!) {{ {mutationField}(sceneId: $sceneId) }}", new { sceneId = sceneId }),
        };

        // Try single-call delta, then loop if needed.
        for (int i = 0; i < Math.Min(delta, 50); i++)
        {
            bool anyOk = false;
            foreach (var c in candidates)
            {
                var resp = await SendAsync<JObject>(c.Query, c.Vars, ct).ConfigureAwait(false);
                if (resp is not null && (resp.Errors is null || resp.Errors.Length == 0))
                {
                    anyOk = true;
                    break;
                }
            }

            if (!anyOk)
            {
                return false;
            }
        }

        return true;
    }

    private async Task<PlayCountMutationSpec?> GetPlayCountMutationSpecAsync(string mutationField, CancellationToken ct)
    {
        if (_playCountMutationSpecs.TryGetValue(mutationField, out var cached))
        {
            return cached;
        }

        // Introspect Mutation type.
        var resp = await SendAsync<JObject>(IntrospectMutationTypeQuery, new { typeName = "Mutation" }, ct).ConfigureAwait(false);
        if (resp is null || resp.Errors is not null && resp.Errors.Length > 0)
        {
            _playCountMutationSpecs[mutationField] = null;
            return null;
        }

        var fields = resp.Data?["__type"]?["fields"] as JArray;
        if (fields is null)
        {
            _playCountMutationSpecs[mutationField] = null;
            return null;
        }

        JObject? field = null;
        foreach (var f in fields)
        {
            if (string.Equals(f?["name"]?.ToString(), mutationField, StringComparison.Ordinal))
            {
                field = f as JObject;
                break;
            }
        }

        if (field is null)
        {
            _playCountMutationSpecs[mutationField] = null;
            return null;
        }

        var spec = new PlayCountMutationSpec(mutationField);
        spec.ReadFromField(field);

        // If input object is used, introspect input fields so we can populate id/count correctly.
        if (spec.InputTypeName is not null)
        {
            var iresp = await SendAsync<JObject>(IntrospectInputTypeQuery, new { name = spec.InputTypeName }, ct).ConfigureAwait(false);
            var inputFields = iresp?.Data?["__type"]?["inputFields"] as JArray;
            if (inputFields is not null)
            {
                spec.ReadInputFields(inputFields);
            }
        }

        _playCountMutationSpecs[mutationField] = spec;
        return spec;
    }

    private sealed class PlayCountMutationSpec
    {
        public string FieldName { get; }
        public string? InputArgName { get; private set; }
        public string? InputTypeName { get; private set; }
        public string? IdArgName { get; private set; }
        public string? CountArgName { get; private set; }
        public string? InputIdFieldName { get; private set; }
        public string? InputCountFieldName { get; private set; }
        public bool ReturnNeedsSelection { get; private set; }
        public bool SupportsDelta => CountArgName is not null || InputCountFieldName is not null;

        public PlayCountMutationSpec(string fieldName)
        {
            FieldName = fieldName;
        }

        public void ReadFromField(JObject field)
        {
            // Return type: determine whether we need a selection set.
            var (kind, _name, _nn, _list) = UnwrapType(field["type"]);
            ReturnNeedsSelection = string.Equals(kind, "OBJECT", StringComparison.Ordinal) || string.Equals(kind, "INTERFACE", StringComparison.Ordinal) || string.Equals(kind, "UNION", StringComparison.Ordinal);

            var args = field["args"] as JArray;
            if (args is null)
            {
                return;
            }

            foreach (var a in args)
            {
                var an = a?["name"]?.ToString();
                if (string.IsNullOrWhiteSpace(an))
                {
                    continue;
                }

                var (akind, abaseName, _, _) = UnwrapType(a?["type"]);
                if (string.Equals(akind, "INPUT_OBJECT", StringComparison.Ordinal) && !string.IsNullOrWhiteSpace(abaseName))
                {
                    InputArgName = an;
                    InputTypeName = abaseName;
                    continue;
                }

                if (string.Equals(abaseName, "ID", StringComparison.Ordinal))
                {
                    IdArgName = an;
                    continue;
                }

                if (string.Equals(abaseName, "Int", StringComparison.Ordinal) || string.Equals(abaseName, "Int", StringComparison.OrdinalIgnoreCase))
                {
                    CountArgName = an;
                }
            }
        }

        public void ReadInputFields(JArray inputFields)
        {
            foreach (var f in inputFields)
            {
                var fn = f?["name"]?.ToString();
                if (string.IsNullOrWhiteSpace(fn))
                {
                    continue;
                }

                var (_, baseName, _, _) = UnwrapType(f?["type"]);
                if (string.Equals(baseName, "ID", StringComparison.Ordinal))
                {
                    InputIdFieldName = fn;
                }

                if (string.Equals(baseName, "Int", StringComparison.OrdinalIgnoreCase))
                {
                    InputCountFieldName = fn;
                }
            }

            // Common defaults if schema info is incomplete.
            InputIdFieldName ??= "id";
        }

        public (string Query, object Variables) Build(string sceneId, int delta)
        {
            if (InputTypeName is not null && InputArgName is not null)
            {
                var input = new JObject
                {
                    [InputIdFieldName ?? "id"] = sceneId,
                };

                if (InputCountFieldName is not null)
                {
                    input[InputCountFieldName] = delta;
                }

                var sel = ReturnNeedsSelection ? " { id }" : string.Empty;
                var q = $"mutation($input: {InputTypeName}!) {{ {FieldName}({InputArgName}: $input){sel} }}";
                return (q, new { input });
            }

            // Direct args.
            var sel2 = ReturnNeedsSelection ? " { id }" : string.Empty;
            var defs = new List<string>();
            var callArgs = new List<string>();
            var vars = new JObject();

            if (!string.IsNullOrWhiteSpace(IdArgName))
            {
                defs.Add($"${IdArgName}: ID!");
                callArgs.Add($"{IdArgName}: ${IdArgName}");
                vars[IdArgName!] = sceneId;
            }

            if (!string.IsNullOrWhiteSpace(CountArgName))
            {
                defs.Add($"${CountArgName}: Int!");
                callArgs.Add($"{CountArgName}: ${CountArgName}");
                vars[CountArgName!] = delta;
            }

            var defStr = defs.Count > 0 ? "(" + string.Join(", ", defs) + ")" : string.Empty;
            var argStr = callArgs.Count > 0 ? "(" + string.Join(", ", callArgs) + ")" : string.Empty;
            var q2 = $"mutation{defStr} {{ {FieldName}{argStr}{sel2} }}";
            return (q2, vars);
        }

        private static (string? BaseKind, string? BaseName, bool NonNull, bool List) UnwrapType(JToken? type)
        {
            bool nonNull = false;
            bool list = false;
            var t = type;
            while (t is not null)
            {
                var kind = t?["kind"]?.ToString();
                var name = t?["name"]?.ToString();
                if (string.Equals(kind, "NON_NULL", StringComparison.Ordinal))
                {
                    nonNull = true;
                    t = t?["ofType"];
                    continue;
                }

                if (string.Equals(kind, "LIST", StringComparison.Ordinal))
                {
                    list = true;
                    t = t?["ofType"];
                    continue;
                }

                return (kind, name, nonNull, list);
            }

            return (null, null, nonNull, list);
        }
    }
}
