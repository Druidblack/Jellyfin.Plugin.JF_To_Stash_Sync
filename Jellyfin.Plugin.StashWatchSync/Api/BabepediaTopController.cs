using System;
using System.Linq;
using JFToStashSync.Services;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace JFToStashSync.Api;

[ApiController]
[Authorize]
[Route("JFToStashSync")]
public sealed class BabepediaTopController : ControllerBase
{
    private readonly BabepediaTopPreparedIndexService _index;
    private readonly ILibraryManager _library;

    public BabepediaTopController(BabepediaTopPreparedIndexService index, ILibraryManager library)
    {
        _index = index;
        _library = library;
    }

    [HttpGet("BabepediaTopPrepared")]
    public IActionResult GetPrepared()
    {
        var cfg = Plugin.Instance?.Configuration;
        if (cfg is null || !cfg.Enabled || !cfg.EnableBabepediaTopRows) return NotFound();
        var claim = User.Claims.FirstOrDefault(c =>
            c.Type.Equals("Jellyfin-UserId", StringComparison.OrdinalIgnoreCase))?.Value;
        if (!Guid.TryParse(claim, out var userId) || userId == Guid.Empty) return Forbid();

        var snapshot = _index.GetSnapshot();
        if (snapshot is null)
            return Ok(new { status = "not_ready", message = "Run Refresh Babepedia Top 100 indexes in Scheduled Tasks.", entries = Array.Empty<object>() });
        if (!_index.IsCurrent(snapshot, cfg))
            return Ok(new { status = "outdated", message = "Stash connection changed. Refresh Babepedia Top 100 indexes.", entries = Array.Empty<object>() });
        var selected = BabepediaTopPreparedIndexService.SelectedCategories(cfg);
        var entries = snapshot.Items.Where(x => selected.Contains(x.Category) &&
                _library.GetItemById<Person>(x.PersonId, userId) is not null)
            .Select(x => new { personId = x.PersonId.ToString("N"), category = x.Category, rank = x.Rank }).ToArray();
        var cachedPages = snapshot.Pages.Where(p => selected.Contains(p.Category))
            .Select(p => new { category = p.Category, updatedAt = p.FetchedAtUtc }).ToArray();
        return Ok(new
        {
            status = "ready", updatedAt = snapshot.UpdatedAtUtc,
            pendingCategories = selected.Where(x => !snapshot.Pages.Any(p => p.Category == x)).ToArray(),
            cachedPages, entries
        });
    }
}
