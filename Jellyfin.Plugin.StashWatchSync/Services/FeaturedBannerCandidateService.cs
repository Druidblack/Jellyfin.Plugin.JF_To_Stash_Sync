using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;

namespace JFToStashSync.Services;

/// <summary>
/// An in-memory ID-only candidate index. Scans Person IDs at most once per five minutes
/// across all client sessions. Does not require Stash/Pornhub/Babepedia or any scheduled task.
/// </summary>
public sealed class FeaturedBannerCandidateService
{
    private readonly ILibraryManager _library;
    private readonly object _gate = new();
    private Guid[] _cached = Array.Empty<Guid>();
    private DateTime _expiresUtc = DateTime.MinValue;

    public FeaturedBannerCandidateService(ILibraryManager library) => _library = library;

    public Guid[] GetIds()
    {
        lock (_gate)
        {
            if (DateTime.UtcNow < _expiresUtc) return _cached;
            var ids = _library.GetItemIds(new InternalItemsQuery
            {
                IncludeItemTypes = new[] { BaseItemKind.Person },
                Recursive = true,
                EnableTotalRecordCount = false
            });
            var output = new List<Guid>();
            foreach (var id in ids)
            {
                if (_library.GetItemById(id) is Person person &&
                    person.GetImageInfo(ImageType.Banner, 0) is not null)
                    output.Add(id);
            }
            _cached = output.ToArray();
            _expiresUtc = DateTime.UtcNow.AddMinutes(5);
            return _cached;
        }
    }
}
