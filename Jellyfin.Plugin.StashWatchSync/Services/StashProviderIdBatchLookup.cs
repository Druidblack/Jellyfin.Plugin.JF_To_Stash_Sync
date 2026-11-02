using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;

namespace JFToStashSync.Services;

/// <summary>
/// Looks up *only* the Stash provider IDs in a ranking, using Jellyfin 12's
/// database-side provider ID filtering. The old implementation paged over the
/// entire library, materializing every media object just to inspect one ID.
/// </summary>
internal static class StashProviderIdBatchLookup
{
    // Small enough to avoid constructing huge EF OR expressions; large enough
    // to avoid one database round-trip per ranked entry.
    private const int ProviderIdsPerQuery = 20;
    private const int ResultPageSize = 200;
    private const int MaxPagesPerQuery = 100;

    private static readonly BaseItemKind[] VideoKinds =
    {
        BaseItemKind.Movie, BaseItemKind.Episode, BaseItemKind.Video,
        BaseItemKind.MusicVideo, BaseItemKind.Trailer, BaseItemKind.Recording
    };

    internal static IEnumerable<StashItemMatch> FindVideos(
        ILibraryManager library,
        IEnumerable<string> stashIds,
        Guid libraryId,
        CancellationToken cancellationToken)
        => Find(library, stashIds, VideoKinds, libraryId, false, cancellationToken);

    internal static IEnumerable<StashItemMatch> FindPeople(
        ILibraryManager library,
        IEnumerable<string> stashIds,
        CancellationToken cancellationToken)
        => Find(library, stashIds, new[] { BaseItemKind.Person }, Guid.Empty, true, cancellationToken);

    private static IEnumerable<StashItemMatch> Find(
        ILibraryManager library,
        IEnumerable<string> stashIds,
        BaseItemKind[] kinds,
        Guid libraryId,
        bool people,
        CancellationToken cancellationToken)
    {
        // Take IDs only, never retain BaseItem / Video / Person objects in the
        // persistent prepared index or service state.
        var distinctIds = stashIds.Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        foreach (var ids in distinctIds.Chunk(ProviderIdsPerQuery))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var query = new InternalItemsQuery
            {
                Recursive = true,
                IncludeItemTypes = kinds,
                ParentId = libraryId,
                HasAnyProviderIds = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
                {
                    ["Stash"] = ids
                },
                Limit = ResultPageSize,
                EnableTotalRecordCount = false,
            };

            // Preserve the previous video filters. Person queries did not
            // restrict IsVirtualItem, so do not accidentally exclude people.
            if (!people)
            {
                query.IsFolder = false;
                query.IsVirtualItem = false;
            }

            for (var page = 0; page < MaxPagesPerQuery; page++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                query.StartIndex = page * ResultPageSize;
                // Only the selected provider-ID matches are deserialized.
                var batch = library.GetItemList(query);
                foreach (var item in batch)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (people ? item is not Person : item is not Video)
                        continue;
                    if (item.ProviderIds is null ||
                        !item.ProviderIds.TryGetValue("Stash", out var stashId) ||
                        string.IsNullOrWhiteSpace(stashId))
                        continue;
                    yield return new StashItemMatch(item.Id, stashId.Trim());
                }
                if (batch.Count < ResultPageSize)
                    break;
                if (page == MaxPagesPerQuery - 1)
                    throw new InvalidOperationException("Stash provider-ID query exceeded its safety paging limit. Existing index retained.");
            }
        }
    }
}

internal sealed record StashItemMatch(Guid ItemId, string StashId);
