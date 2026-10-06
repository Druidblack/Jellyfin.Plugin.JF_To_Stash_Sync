using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Configuration;
using Microsoft.Extensions.Logging;
using JFToStashSync.Services;

namespace JFToStashSync.Providers;

/// <summary>
/// Supplies Jellyfin movie similarity results from Stash.
/// </summary>
public sealed class StashSimilarScenesProvider : IRemoteSimilarItemsProvider<Movie>
{
    private const string StashProviderName = "Stash";

    private readonly StashClient _stashClient;
    private readonly ILogger<StashSimilarScenesProvider> _logger;

    public StashSimilarScenesProvider(
        StashClient stashClient,
        ILogger<StashSimilarScenesProvider> logger)
    {
        _stashClient = stashClient;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => "Stash Similar Scenes";

    /// <inheritdoc />
    public MetadataPluginType Type => MetadataPluginType.SimilarityProvider;

    /// <summary>
    /// Keep results live like the original Stash userscript. Jellyfin will therefore ask
    /// Stash again when similar items are requested instead of using a disk cache.
    /// </summary>
    public TimeSpan? CacheDuration => null;

    /// <inheritdoc />
    public async IAsyncEnumerable<SimilarItemReference> GetSimilarItemsAsync(
        Movie item,
        SimilarItemsQuery query,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(query);

        if (!_stashClient.IsConfigured())
        {
            yield break;
        }

        if (item.ProviderIds is null
            || !item.ProviderIds.TryGetValue(StashProviderName, out var stashSceneId)
            || string.IsNullOrWhiteSpace(stashSceneId))
        {
            _logger.LogDebug(
                "JFToStashSync: Stash Similar Scenes skipped because movie has no Stash provider id. itemId={ItemId} name={Name}",
                item.Id,
                item.Name);
            yield break;
        }

        IReadOnlyList<StashSimilarSceneMatch> matches;
        try
        {
            matches = await _stashClient
                .GetSimilarScenesAsync(stashSceneId, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            yield break;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "JFToStashSync: Stash Similar Scenes failed. itemId={ItemId} sceneId={SceneId}",
                item.Id,
                stashSceneId);
            yield break;
        }

        if (matches.Count == 0)
        {
            yield break;
        }

        var maxScore = 0;
        foreach (var match in matches)
        {
            if (match.Score > maxScore)
            {
                maxScore = match.Score;
            }
        }

        foreach (var match in matches)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Jellyfin expects a score from 0..1. The Stash userscript uses an open-ended
            // integer score, so normalize against the strongest candidate while retaining
            // exactly the same ordering.
            var normalizedScore = maxScore > 0
                ? Math.Clamp((float)match.Score / maxScore, 0f, 1f)
                : (float?)null;

            yield return new SimilarItemReference
            {
                ProviderName = StashProviderName,
                ProviderId = match.SceneId,
                Score = normalizedScore,
            };
        }
    }
}
