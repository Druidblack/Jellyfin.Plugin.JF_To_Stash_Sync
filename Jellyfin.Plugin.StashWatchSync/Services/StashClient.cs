using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
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

namespace JFToStashSync.Services;

/// <summary>
/// Minimal Stash GraphQL client used only for syncing activity.
/// </summary>
public sealed class StashClient
{
    private const string ProviderIdKey = "Stash";

    // Lightweight Stash-specific query used by the settings-page connection test.
    // __typename keeps the test compatible across Stash versions while still
    // proving that the endpoint exposes Stash's `version` GraphQL field.
    private const string ConnectionTestQuery = @"query JFToStashSyncConnectionTest {
  version { __typename }
}";

    private const string MetadataScanMutation = @"mutation metadataScan($input: ScanMetadataInput!) {
  metadataScan(input: $input)
}";

    private const string FindScenesQuery = @"query findScenes($filter: FindFilterType, $scene_filter: SceneFilterType) {
  findScenes(filter: $filter, scene_filter: $scene_filter) {
    scenes {
      id
      files { path }
    }
  }
}";

    private const string SimilarBaseSceneQuery = @"query JFToStashSyncSimilarBaseScene($id: ID!) {
  findScene(id: $id) {
    id
    title
    performers { id name favorite }
    tags { id name }
  }
}";

    private const string SimilarScenesByPerformersQuery = @"query JFToStashSyncSimilarByPerformers($ids: [ID!], $limit: Int!) {
  findScenes(
    scene_filter: { performers: { value: $ids, modifier: INCLUDES } }
    filter: { per_page: $limit }
  ) {
    scenes {
      id
      title
      performers { id name favorite }
      tags { id name }
    }
  }
}";

    private const string SimilarScenesByTagQuery = @"query JFToStashSyncSimilarByTag($name: String!, $limit: Int!) {
  findScenes(
    scene_filter: { tags_filter: { name: { value: $name, modifier: INCLUDES } } }
    filter: { per_page: $limit }
  ) {
    scenes {
      id
      title
      performers { id name favorite }
      tags { id name }
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

    // Stash's dedicated playback-activity mutation updates the player resume point
    // without modifying scene metadata or adding any play_duration.
    private const string SaveResumeActivityMutation = @"mutation sceneSaveActivity($id: ID!, $resume_time: Float!) {
  sceneSaveActivity(id: $id, resume_time: $resume_time)
}";

    private const string SceneUpdateMutation = @"mutation sceneUpdate($input: SceneUpdateInput!) {
  sceneUpdate(input: $input) {
    id
    title
    resume_time
    play_duration
  }
}";

    private const string FindSceneUrlsQuery = @"query findScene($id: ID!) {
  findScene(id: $id) {
    id
    title
    urls
  }
}";

    private const string SceneUrlsUpdateMutation = @"mutation sceneUpdate($input: SceneUpdateInput!) {
  sceneUpdate(input: $input) {
    id
    title
    urls
  }
}";

    private const string FindPerformerUrlsQuery = @"query findPerformer($id: ID!) {
  findPerformer(id: $id) {
    id
    name
    urls
  }
}";

    private const string PerformerUrlsUpdateMutation = @"mutation performerUpdate($input: PerformerUpdateInput!) {
  performerUpdate(input: $input) {
    id
    name
    urls
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

    // Stash's current O-counter API. sceneAddO also records the event in o_history.
    private const string SceneAddOMutation = @"mutation sceneAddO($id: ID!) {
  sceneAddO(id: $id) {
    count
  }
}";

    // Compatibility fallback for older Stash builds.
    private const string SceneIncrementOMutation = @"mutation sceneIncrementO($id: ID!) {
  sceneIncrementO(id: $id)
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

    /// <summary>
    /// Tests the currently configured Stash endpoint and API key.
    /// This deliberately ignores the plugin Enabled switch so the connection can
    /// be verified before enabling synchronization.
    /// </summary>
    public async Task<StashConnectionTestResult> TestConnectionAsync(CancellationToken ct)
    {
        var cfg = Plugin.Instance?.Configuration;
        var endpoint = cfg?.StashEndpoint?.Trim() ?? string.Empty;
        var apiKey = cfg?.StashApiKey ?? string.Empty;

        if (string.IsNullOrWhiteSpace(endpoint))
        {
            return StashConnectionTestResult.Fail("Stash endpoint is empty.");
        }

        var graphqlUrl = NormalizeGraphQlUrl(endpoint);
        if (!Uri.TryCreate(graphqlUrl, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return StashConnectionTestResult.Fail("Stash endpoint must be a valid HTTP or HTTPS URL.", endpoint);
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(10));
        var stopwatch = Stopwatch.StartNew();

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, uri);
            req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/graphql-response+json"));

            if (!string.IsNullOrWhiteSpace(apiKey))
            {
                req.Headers.TryAddWithoutValidation("ApiKey", apiKey);
            }

            req.Content = new StringContent(
                JsonConvert.SerializeObject(new { query = ConnectionTestQuery, variables = new { } }),
                Encoding.UTF8,
                "application/json");

            var client = _httpClientFactory.CreateClient();
            using var response = await client.SendAsync(req, timeoutCts.Token).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(timeoutCts.Token).ConfigureAwait(false);
            stopwatch.Stop();

            if (!response.IsSuccessStatusCode)
            {
                var message = response.StatusCode == System.Net.HttpStatusCode.Unauthorized
                    ? "Stash returned HTTP 401 Unauthorized. Check the API key."
                    : $"Stash returned HTTP {(int)response.StatusCode} ({response.ReasonPhrase}).";

                _logger.LogWarning(
                    "JFToStashSync: Stash connection test failed. endpoint={Endpoint} status={StatusCode}",
                    endpoint,
                    (int)response.StatusCode);

                return StashConnectionTestResult.Fail(message, endpoint, (int)response.StatusCode, stopwatch.ElapsedMilliseconds);
            }

            JObject parsed;
            try
            {
                parsed = JObject.Parse(body);
            }
            catch (JsonException)
            {
                return StashConnectionTestResult.Fail(
                    "The server responded, but the response was not valid Stash GraphQL JSON.",
                    endpoint,
                    (int)response.StatusCode,
                    stopwatch.ElapsedMilliseconds);
            }

            if (parsed["errors"] is JArray errors && errors.Count > 0)
            {
                var errorText = string.Join(
                    " | ",
                    errors
                        .Select(e => e?["message"]?.ToString())
                        .Where(m => !string.IsNullOrWhiteSpace(m))
                        .Take(3));

                if (string.IsNullOrWhiteSpace(errorText))
                {
                    errorText = "Stash GraphQL returned an error.";
                }

                return StashConnectionTestResult.Fail(
                    errorText,
                    endpoint,
                    (int)response.StatusCode,
                    stopwatch.ElapsedMilliseconds);
            }

            var typeName = parsed["data"]?["version"]?["__typename"]?.ToString();
            if (!string.Equals(typeName, "Version", StringComparison.Ordinal))
            {
                return StashConnectionTestResult.Fail(
                    "The GraphQL endpoint responded, but it does not look like a compatible Stash server.",
                    endpoint,
                    (int)response.StatusCode,
                    stopwatch.ElapsedMilliseconds);
            }

            _logger.LogInformation(
                "JFToStashSync: Stash connection test succeeded. endpoint={Endpoint} elapsedMs={ElapsedMs}",
                endpoint,
                stopwatch.ElapsedMilliseconds);

            return StashConnectionTestResult.Ok(endpoint, stopwatch.ElapsedMilliseconds);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            stopwatch.Stop();
            return StashConnectionTestResult.Fail(
                "Connection to Stash timed out after 10 seconds.",
                endpoint,
                null,
                stopwatch.ElapsedMilliseconds);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (HttpRequestException ex)
        {
            stopwatch.Stop();
            _logger.LogWarning(ex, "JFToStashSync: Stash connection test failed. endpoint={Endpoint}", endpoint);
            return StashConnectionTestResult.Fail(
                "Could not connect to Stash: " + ex.Message,
                endpoint,
                null,
                stopwatch.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            _logger.LogError(ex, "JFToStashSync: unexpected Stash connection test error. endpoint={Endpoint}", endpoint);
            return StashConnectionTestResult.Fail(
                "Unexpected error while testing Stash: " + ex.Message,
                endpoint,
                null,
                stopwatch.ElapsedMilliseconds);
        }
    }

    /// <summary>
    /// Returns Stash similar scenes using the same candidate selection and scoring rules
    /// as the original stashSimilarScenes userscript.
    /// </summary>
    public async Task<IReadOnlyList<StashSimilarSceneMatch>> GetSimilarScenesAsync(
        string baseSceneId,
        CancellationToken ct)
    {
        const int TargetScenes = 10;
        const int QueryLimit = 40;
        const int TagFallbackCount = 3;
        const int FavoritePerformerWeight = 100;
        const int SharedPerformerWeight = 25;
        const int SharedTagWeight = 10;

        if (string.IsNullOrWhiteSpace(baseSceneId) || !IsConfigured())
        {
            return Array.Empty<StashSimilarSceneMatch>();
        }

        var baseResponse = await SendAsync<GraphQlModels.SimilarFindSceneData>(
            SimilarBaseSceneQuery,
            new { id = baseSceneId },
            ct).ConfigureAwait(false);
        var baseScene = baseResponse?.Data?.FindScene;
        if (baseScene is null)
        {
            _logger.LogDebug(
                "JFToStashSync: Stash Similar Scenes base scene not found. sceneId={SceneId}",
                baseSceneId);
            return Array.Empty<StashSimilarSceneMatch>();
        }

        var basePerformerIds = new HashSet<string>(
            baseScene.Performers
                .Select(p => p.Id)
                .Where(id => !string.IsNullOrWhiteSpace(id)),
            StringComparer.Ordinal);
        var baseTagIds = new HashSet<string>(
            baseScene.Tags
                .Select(t => t.Id)
                .Where(id => !string.IsNullOrWhiteSpace(id)),
            StringComparer.Ordinal);

        int Score(GraphQlModels.SimilarScene scene)
        {
            var score = 0;
            foreach (var performer in scene.Performers)
            {
                if (performer.Favorite)
                {
                    score += FavoritePerformerWeight;
                }

                if (!string.IsNullOrWhiteSpace(performer.Id) && basePerformerIds.Contains(performer.Id))
                {
                    score += SharedPerformerWeight;
                }
            }

            foreach (var tag in scene.Tags)
            {
                if (!string.IsNullOrWhiteSpace(tag.Id) && baseTagIds.Contains(tag.Id))
                {
                    score += SharedTagWeight;
                }
            }

            return score;
        }

        var scored = new Dictionary<string, StashSimilarSceneMatch>(StringComparer.Ordinal);

        void AddCandidate(GraphQlModels.SimilarScene scene)
        {
            if (string.IsNullOrWhiteSpace(scene.Id)
                || string.Equals(scene.Id, baseScene.Id, StringComparison.Ordinal)
                || scored.ContainsKey(scene.Id))
            {
                return;
            }

            scored[scene.Id] = new StashSimilarSceneMatch
            {
                SceneId = scene.Id,
                Score = Score(scene),
            };
        }

        var performerIds = baseScene.Performers
            .Select(p => p.Id)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        if (performerIds.Length > 0)
        {
            var performerResponse = await SendAsync<GraphQlModels.SimilarFindScenesData>(
                SimilarScenesByPerformersQuery,
                new { ids = performerIds, limit = QueryLimit },
                ct).ConfigureAwait(false);

            foreach (var scene in performerResponse?.Data?.FindScenes?.Scenes ?? [])
            {
                AddCandidate(scene);
            }
        }

        if (scored.Count < TargetScenes)
        {
            var tagNames = baseScene.Tags
                .Select(t => t.Name)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Take(TagFallbackCount)
                .ToArray();

            foreach (var tagName in tagNames)
            {
                ct.ThrowIfCancellationRequested();

                var tagResponse = await SendAsync<GraphQlModels.SimilarFindScenesData>(
                    SimilarScenesByTagQuery,
                    new { name = tagName, limit = QueryLimit },
                    ct).ConfigureAwait(false);

                foreach (var scene in tagResponse?.Data?.FindScenes?.Scenes ?? [])
                {
                    AddCandidate(scene);
                }

                if (scored.Count >= TargetScenes)
                {
                    break;
                }
            }
        }

        var result = scored.Values
            .OrderByDescending(x => x.Score)
            .Take(TargetScenes)
            .ToArray();

        _logger.LogDebug(
            "JFToStashSync: Stash Similar Scenes resolved {Count} candidates for sceneId={SceneId}",
            result.Length,
            baseSceneId);

        return result;
    }

    public async Task<string?> ResolveSceneIdAsync(BaseItem item, CancellationToken ct)
    {
        // 1) Prefer provider id
        if (item.ProviderIds is not null && item.ProviderIds.TryGetValue(ProviderIdKey, out var providerId) && !string.IsNullOrWhiteSpace(providerId))
        {
            _logger.LogDebug("JFToStashSync: resolved scene via providerId. itemId={ItemId} sceneId={SceneId}", item.Id, providerId);
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
            _logger.LogDebug("JFToStashSync: cannot resolve scene by path (empty path). itemId={ItemId}", item.Id);
            return null;
        }

        _logger.LogDebug(
            "JFToStashSync: resolving scene by path. itemId={ItemId} mappedPath={MappedPath} fullPath={FullPath}",
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
    /// Starts a normal Stash metadata scan for the parent folder of a Jellyfin video path,
    /// then waits for the exact mapped file path to become visible as a Stash scene.
    /// Used only by the manual scene-link workflow.
    /// </summary>
    public async Task<StashFolderScanResult> ScanParentFolderAndWaitForSceneAsync(
        string? jellyfinFilePath,
        CancellationToken ct)
    {
        var cfg = Plugin.Instance?.Configuration;
        if (cfg is null || !cfg.Enabled)
        {
            return StashFolderScanResult.Fail("JF To Stash Sync is disabled.");
        }

        if (!TryMapPathForFolderScan(
                jellyfinFilePath,
                cfg.JellyfinPathPrefix,
                cfg.StashPathPrefix,
                out var mappedFilePath,
                out var mapError))
        {
            return StashFolderScanResult.Fail(mapError);
        }

        var slash = mappedFilePath.LastIndexOf('/');
        if (slash <= 0)
        {
            return StashFolderScanResult.Fail(
                $"Could not determine the Stash parent folder for mapped path '{mappedFilePath}'.");
        }

        var scanPath = mappedFilePath.Substring(0, slash).TrimEnd('/');
        if (string.IsNullOrWhiteSpace(scanPath))
        {
            return StashFolderScanResult.Fail("The mapped Stash parent folder is empty.");
        }

        var variables = new
        {
            input = new
            {
                paths = new[] { scanPath },
                // This is a normal targeted scan. New files are discovered without forcing
                // Stash to re-process every unchanged file already present in the folder.
                rescan = false,
            }
        };

        var response = await SendAsync<GraphQlModels.MetadataScanData>(
            MetadataScanMutation,
            variables,
            ct).ConfigureAwait(false);

        var jobId = response?.Data?.MetadataScan;
        if (string.IsNullOrWhiteSpace(jobId))
        {
            return StashFolderScanResult.Fail(
                $"Stash did not start a metadata scan for '{scanPath}'.",
                mappedFilePath,
                scanPath);
        }

        _logger.LogInformation(
            "JFToStashSync: started targeted Stash folder scan. jobId={JobId} scanPath={ScanPath} mappedFile={MappedFile}",
            jobId,
            scanPath,
            mappedFilePath);

        // The scan mutation is asynchronous. Poll for the one exact file we care about rather
        // than waiting for (or depending on) Stash's whole global job queue.
        var deadline = DateTime.UtcNow.AddSeconds(60);
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();

            var sceneId = await FindSceneIdByPathAsync(mappedFilePath, true, ct).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(sceneId))
            {
                _logger.LogInformation(
                    "JFToStashSync: targeted Stash folder scan discovered the video. jobId={JobId} sceneId={SceneId} scanPath={ScanPath}",
                    jobId,
                    sceneId,
                    scanPath);

                return StashFolderScanResult.Found(jobId, mappedFilePath, scanPath, sceneId);
            }

            await Task.Delay(TimeSpan.FromSeconds(1), ct).ConfigureAwait(false);
        }

        _logger.LogWarning(
            "JFToStashSync: targeted Stash folder scan was started, but the video did not appear within 60 seconds. jobId={JobId} scanPath={ScanPath} mappedFile={MappedFile}",
            jobId,
            scanPath,
            mappedFilePath);

        return StashFolderScanResult.StartedButNotFound(jobId, mappedFilePath, scanPath);
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
                _logger.LogWarning("JFToStashSync: failed to increment play count in Stash. sceneId={SceneId} delta={Delta}", sceneId, playCountDelta);
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
                "JFToStashSync: scene activity updated. sceneId={SceneId} title=\"{Title}\" resume_time={Resume} play_duration {Before} -> {After} (+{Added})",
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
                "JFToStashSync: scene activity updated. sceneId={SceneId} title=\"{Title}\" resume_time={Resume}",
                sceneId,
                after?.Title ?? string.Empty,
                after?.ResumeTime);
        }
        return true;
    }

    public async Task<bool> SyncResumeAsync(string sceneId, double resumeSeconds, CancellationToken ct, bool verify = false)
    {
        var cfg = Plugin.Instance?.Configuration;
        if (cfg is null || !cfg.Enabled || !cfg.SyncResumePosition
            || string.IsNullOrWhiteSpace(sceneId) || !double.IsFinite(resumeSeconds) || resumeSeconds < 0)
        {
            return false;
        }

        // sceneSaveActivity is the Stash API intended for playback progress. Sending
        // only resume_time leaves play_duration and play_count untouched.
        var response = await SendAsync<JObject>(SaveResumeActivityMutation,
            new { id = sceneId, resume_time = resumeSeconds }, ct).ConfigureAwait(false);
        if (response?.Errors is { Length: > 0 }
            || response?.Data?["sceneSaveActivity"]?.Value<bool>() != true)
        {
            _logger.LogWarning("JFToStashSync: Stash rejected sceneSaveActivity. sceneId={SceneId} seconds={Seconds:F2}", sceneId, resumeSeconds);
            return false;
        }

        if (verify)
        {
            var saved = await GetSceneActivityAsync(sceneId, ct).ConfigureAwait(false);
            if (saved?.ResumeTime is not double actual || Math.Abs(actual - resumeSeconds) > 1.0)
            {
                _logger.LogWarning(
                    "JFToStashSync: resume point verification failed. sceneId={SceneId} expected={Expected:F2} actual={Actual}",
                    sceneId, resumeSeconds, saved?.ResumeTime);
                return false;
            }
        }

        return true;
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
            _logger.LogWarning("JFToStashSync: failed to update play_duration (in-progress). sceneId={SceneId} add={Add}", sceneId, addSeconds);
            return false;
        }

        var after = await GetSceneActivityAsync(sceneId, ct).ConfigureAwait(false);
        _logger.LogInformation(
            "JFToStashSync: in-progress play_duration updated. sceneId={SceneId} title='{Title}' play_duration {Before} -> {After} (+{Added})",
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
                    if (string.Equals(Path.GetFileName(fp), normSearchFile, StringComparison.OrdinalIgnoreCase))
                    {
                        return s.Id;
                    }
                }
            }
        }

        // Never guess. A non-exact path/filename match could update the wrong Stash scene.
        _logger.LogWarning(
            "JFToStashSync: path search returned candidates but none matched exactly. search={Search} fullPath={FullPath} candidates={Count}",
            search,
            fullPath,
            scenes.Count);
        return null;
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
                "JFToStashSync: sceneUpdate response. sceneId={SceneId} title=\"{Title}\" resume={Resume}",
                updated.Id,
                updated.Title ?? string.Empty,
                updated.ResumeTime);
        }

        return updated is not null && !string.IsNullOrWhiteSpace(updated.Id);
    }




    /// <summary>
    /// Adds or replaces the Jellyfin details URL for a Stash scene.
    /// All unrelated scene URLs are preserved. Existing Jellyfin details URLs
    /// from the same host and port are collapsed to the supplied URL.
    /// </summary>
    public async Task<JellyfinUrlUpsertResult> UpsertJellyfinUrlAsync(
        string sceneId,
        string jellyfinUrl,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(sceneId))
        {
            return JellyfinUrlUpsertResult.Failure("Stash scene id is empty.");
        }

        if (!Uri.TryCreate(jellyfinUrl, UriKind.Absolute, out var targetUri)
            || (targetUri.Scheme != Uri.UriSchemeHttp && targetUri.Scheme != Uri.UriSchemeHttps))
        {
            return JellyfinUrlUpsertResult.Failure("The Jellyfin URL is not a valid HTTP/HTTPS URL.");
        }

        var existingResponse = await SendAsync<GraphQlModels.FindSceneUrlsData>(
            FindSceneUrlsQuery,
            new { id = sceneId },
            ct).ConfigureAwait(false);

        if (existingResponse is null || existingResponse.Errors is { Length: > 0 })
        {
            return JellyfinUrlUpsertResult.Failure("Could not read the current Stash scene URLs.");
        }

        var scene = existingResponse.Data?.FindScene;
        if (scene is null || string.IsNullOrWhiteSpace(scene.Id))
        {
            return JellyfinUrlUpsertResult.Failure($"Stash scene {sceneId} was not found.");
        }

        var oldUrls = scene.Urls ?? new List<string>();
        var newUrls = new List<string>(oldUrls.Count + 1);
        var targetInserted = false;
        var replacedCount = 0;

        foreach (var rawUrl in oldUrls)
        {
            if (string.IsNullOrWhiteSpace(rawUrl))
            {
                // Do not keep empty URL values.
                continue;
            }

            var existingUrl = rawUrl.Trim();
            if (!IsJellyfinDetailsUrlForSameServer(existingUrl, targetUri))
            {
                newUrls.Add(existingUrl);
                continue;
            }

            if (!targetInserted)
            {
                newUrls.Add(jellyfinUrl);
                targetInserted = true;

                if (!UrlsEquivalent(existingUrl, jellyfinUrl))
                {
                    replacedCount++;
                }
            }
            else
            {
                // More than one Jellyfin URL for the same server: collapse duplicates.
                replacedCount++;
            }
        }

        if (!targetInserted)
        {
            newUrls.Add(jellyfinUrl);
        }

        var changed = !UrlListsEqual(oldUrls, newUrls);
        if (!changed)
        {
            return JellyfinUrlUpsertResult.SuccessResult(
                changed: false,
                replacedCount: 0,
                finalUrlCount: newUrls.Count,
                message: "The Stash scene already contains the current Jellyfin URL.");
        }

        var input = new JObject
        {
            ["id"] = sceneId,
            ["urls"] = JArray.FromObject(newUrls),
        };

        var updateResponse = await SendAsync<GraphQlModels.SceneUrlsUpdateData>(
            SceneUrlsUpdateMutation,
            new { input },
            ct).ConfigureAwait(false);

        if (updateResponse is null || updateResponse.Errors is { Length: > 0 })
        {
            return JellyfinUrlUpsertResult.Failure("Stash rejected the scene URL update.");
        }

        var updated = updateResponse.Data?.SceneUpdate;
        if (updated is null || string.IsNullOrWhiteSpace(updated.Id))
        {
            return JellyfinUrlUpsertResult.Failure("Stash did not return the updated scene.");
        }

        _logger.LogInformation(
            "JFToStashSync: Jellyfin URL synchronized. sceneId={SceneId} replaced={ReplacedCount} url={Url}",
            sceneId,
            replacedCount,
            jellyfinUrl);

        return JellyfinUrlUpsertResult.SuccessResult(
            changed: true,
            replacedCount: replacedCount,
            finalUrlCount: updated.Urls?.Count ?? newUrls.Count,
            message: replacedCount > 0
                ? "Jellyfin URL was replaced in Stash."
                : "Jellyfin URL was added to Stash.");
    }

    private static bool IsJellyfinDetailsUrlForSameServer(string candidate, Uri target)
    {
        if (!Uri.TryCreate(candidate, UriKind.Absolute, out var uri))
        {
            return false;
        }

        if (!string.Equals(uri.Host, target.Host, StringComparison.OrdinalIgnoreCase)
            || uri.Port != target.Port)
        {
            return false;
        }

        // Only replace links that look like Jellyfin web-client item details links.
        // This avoids deleting unrelated URLs that happen to use the same host.
        return uri.AbsolutePath.Contains("/web", StringComparison.OrdinalIgnoreCase)
            && uri.Fragment.Contains("details", StringComparison.OrdinalIgnoreCase)
            && uri.Fragment.Contains("id=", StringComparison.OrdinalIgnoreCase);
    }

    private static bool UrlsEquivalent(string left, string right)
        => string.Equals(left.TrimEnd('/'), right.TrimEnd('/'), StringComparison.OrdinalIgnoreCase);

    private static bool UrlListsEqual(IReadOnlyList<string> left, IReadOnlyList<string> right)
    {
        if (left.Count != right.Count)
        {
            return false;
        }

        for (var i = 0; i < left.Count; i++)
        {
            if (!string.Equals(left[i]?.Trim(), right[i]?.Trim(), StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Adds or replaces the Jellyfin details URL for a Stash performer.
    /// All unrelated performer URLs are preserved. Existing Jellyfin details URLs
    /// from the same host and port are collapsed to the supplied URL.
    /// </summary>
    public async Task<JellyfinUrlUpsertResult> UpsertJellyfinPerformerUrlAsync(
        string performerId,
        string jellyfinUrl,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(performerId))
        {
            return JellyfinUrlUpsertResult.Failure("Stash performer id is empty.");
        }

        if (!Uri.TryCreate(jellyfinUrl, UriKind.Absolute, out var targetUri)
            || (targetUri.Scheme != Uri.UriSchemeHttp && targetUri.Scheme != Uri.UriSchemeHttps))
        {
            return JellyfinUrlUpsertResult.Failure("The Jellyfin URL is not a valid HTTP/HTTPS URL.");
        }

        var existingResponse = await SendAsync<GraphQlModels.FindPerformerUrlsData>(
            FindPerformerUrlsQuery,
            new { id = performerId },
            ct).ConfigureAwait(false);

        if (existingResponse is null || existingResponse.Errors is { Length: > 0 })
        {
            return JellyfinUrlUpsertResult.Failure("Could not read the current Stash performer URLs.");
        }

        var performer = existingResponse.Data?.FindPerformer;
        if (performer is null || string.IsNullOrWhiteSpace(performer.Id))
        {
            return JellyfinUrlUpsertResult.Failure($"Stash performer {performerId} was not found.");
        }

        var oldUrls = performer.Urls ?? new List<string>();
        var newUrls = new List<string>(oldUrls.Count + 1);
        var targetInserted = false;
        var replacedCount = 0;

        foreach (var rawUrl in oldUrls)
        {
            if (string.IsNullOrWhiteSpace(rawUrl))
            {
                continue;
            }

            var existingUrl = rawUrl.Trim();
            if (!IsJellyfinDetailsUrlForSameServer(existingUrl, targetUri))
            {
                newUrls.Add(existingUrl);
                continue;
            }

            if (!targetInserted)
            {
                newUrls.Add(jellyfinUrl);
                targetInserted = true;

                if (!UrlsEquivalent(existingUrl, jellyfinUrl))
                {
                    replacedCount++;
                }
            }
            else
            {
                replacedCount++;
            }
        }

        if (!targetInserted)
        {
            newUrls.Add(jellyfinUrl);
        }

        var changed = !UrlListsEqual(oldUrls, newUrls);
        if (!changed)
        {
            return JellyfinUrlUpsertResult.SuccessResult(
                changed: false,
                replacedCount: 0,
                finalUrlCount: newUrls.Count,
                message: "The Stash performer already contains the current Jellyfin URL.");
        }

        var input = new JObject
        {
            ["id"] = performerId,
            ["urls"] = JArray.FromObject(newUrls),
        };

        var updateResponse = await SendAsync<GraphQlModels.PerformerUrlsUpdateData>(
            PerformerUrlsUpdateMutation,
            new { input },
            ct).ConfigureAwait(false);

        if (updateResponse is null || updateResponse.Errors is { Length: > 0 })
        {
            return JellyfinUrlUpsertResult.Failure("Stash rejected the performer URL update.");
        }

        var updated = updateResponse.Data?.PerformerUpdate;
        if (updated is null || string.IsNullOrWhiteSpace(updated.Id))
        {
            return JellyfinUrlUpsertResult.Failure("Stash did not return the updated performer.");
        }

        _logger.LogInformation(
            "JFToStashSync: Jellyfin performer URL synchronized. performerId={PerformerId} replaced={ReplacedCount} url={Url}",
            performerId,
            replacedCount,
            jellyfinUrl);

        return JellyfinUrlUpsertResult.SuccessResult(
            changed: true,
            replacedCount: replacedCount,
            finalUrlCount: updated.Urls?.Count ?? newUrls.Count,
            message: replacedCount > 0
                ? "Jellyfin URL was replaced on the Stash performer."
                : "Jellyfin URL was added to the Stash performer.");
    }

    /// <summary>
    /// Increment the Stash scene O-counter by one. Uses sceneAddO so Stash also
    /// records the timestamp in o_history. Returns the new counter value when available.
    /// </summary>
    public async Task<int?> IncrementOCounterAsync(string sceneId, CancellationToken ct)
    {
        var cfg = Plugin.Instance?.Configuration;
        if (cfg is null || !cfg.Enabled || string.IsNullOrWhiteSpace(sceneId))
        {
            return null;
        }

        var response = await SendAsync<JObject>(
            SceneAddOMutation,
            new { id = sceneId },
            ct).ConfigureAwait(false);

        if (response?.Errors is not { Length: > 0 })
        {
            var countToken = response?.Data?["sceneAddO"]?["count"];
            if (countToken is not null && countToken.Type != JTokenType.Null)
            {
                return countToken.Value<int>();
            }
        }

        _logger.LogDebug(
            "JFToStashSync: sceneAddO was unavailable or failed; trying legacy sceneIncrementO. sceneId={SceneId}",
            sceneId);

        var legacy = await SendAsync<JObject>(
            SceneIncrementOMutation,
            new { id = sceneId },
            ct).ConfigureAwait(false);

        if (legacy?.Errors is { Length: > 0 })
        {
            return null;
        }

        var legacyToken = legacy?.Data?["sceneIncrementO"];
        return legacyToken is not null && legacyToken.Type != JTokenType.Null
            ? legacyToken.Value<int>()
            : null;
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
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
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

    private static bool TryMapPathForFolderScan(
        string? jellyfinPath,
        string jellyfinPrefix,
        string stashPrefix,
        out string mappedPath,
        out string error)
    {
        mappedPath = string.Empty;
        error = string.Empty;

        if (string.IsNullOrWhiteSpace(jellyfinPath))
        {
            error = "The Jellyfin video has no file path, so a Stash folder scan cannot be started.";
            return false;
        }

        var p = NormalizePath(jellyfinPath);
        var hasJellyfinPrefix = !string.IsNullOrWhiteSpace(jellyfinPrefix);
        var hasStashPrefix = !string.IsNullOrWhiteSpace(stashPrefix);

        if (hasJellyfinPrefix != hasStashPrefix)
        {
            error = "For Stash folder scanning, configure both Jellyfin path prefix and Stash path prefix, or leave both empty when the paths are identical.";
            return false;
        }

        if (!hasJellyfinPrefix)
        {
            mappedPath = p;
            return true;
        }

        var jp = NormalizePath(jellyfinPrefix).TrimEnd('/');
        var sp = NormalizePath(stashPrefix).TrimEnd('/');
        if (string.IsNullOrWhiteSpace(jp) || string.IsNullOrWhiteSpace(sp))
        {
            error = "Jellyfin/Stash path prefixes are invalid for folder scanning.";
            return false;
        }

        if (!p.StartsWith(jp + "/", StringComparison.OrdinalIgnoreCase))
        {
            error = $"The Jellyfin file path '{p}' does not start with configured Jellyfin path prefix '{jp}'. Stash scan was not started.";
            return false;
        }

        mappedPath = sp + p.Substring(jp.Length);
        return true;
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

            if (!string.IsNullOrWhiteSpace(jp) && p.StartsWith(jp + "/", StringComparison.OrdinalIgnoreCase))
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

        _logger.LogDebug("JFToStashSync: {Mutation} executed. sceneId={SceneId} delta={Delta}", spec.FieldName, sceneId, delta);
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

public sealed class StashFolderScanResult
{
    public bool Started { get; init; }

    public bool FoundScene { get; init; }

    public string JobId { get; init; } = string.Empty;

    public string MappedFilePath { get; init; } = string.Empty;

    public string ScanPath { get; init; } = string.Empty;

    public string SceneId { get; init; } = string.Empty;

    public string Message { get; init; } = string.Empty;

    public static StashFolderScanResult Fail(
        string message,
        string? mappedFilePath = null,
        string? scanPath = null)
        => new()
        {
            Started = false,
            MappedFilePath = mappedFilePath ?? string.Empty,
            ScanPath = scanPath ?? string.Empty,
            Message = message,
        };

    public static StashFolderScanResult Found(
        string jobId,
        string mappedFilePath,
        string scanPath,
        string sceneId)
        => new()
        {
            Started = true,
            FoundScene = true,
            JobId = jobId,
            MappedFilePath = mappedFilePath,
            ScanPath = scanPath,
            SceneId = sceneId,
            Message = $"Stash scan found scene {sceneId}.",
        };

    public static StashFolderScanResult StartedButNotFound(
        string jobId,
        string mappedFilePath,
        string scanPath)
        => new()
        {
            Started = true,
            FoundScene = false,
            JobId = jobId,
            MappedFilePath = mappedFilePath,
            ScanPath = scanPath,
            Message = "Stash scan was started, but the video did not appear within 60 seconds.",
        };
}

