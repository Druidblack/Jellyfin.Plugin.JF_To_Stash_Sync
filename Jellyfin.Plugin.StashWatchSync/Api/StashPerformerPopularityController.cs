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
public sealed class StashPerformerPopularityController : ControllerBase
{
    private readonly PerformerPopularityPreparedIndexService _index;
    private readonly ILibraryManager _library;

    public StashPerformerPopularityController(
        PerformerPopularityPreparedIndexService index, ILibraryManager library)
    {
        _index = index;
        _library = library;
    }

    [HttpGet("PerformerPopularityPrepared")]
    public IActionResult GetPrepared()
    {
        var cfg = Plugin.Instance?.Configuration;
        if (cfg is null || !cfg.Enabled || !cfg.EnableStashDbPerformerPopularityRows)
            return NotFound();

        var claim = User.Claims.FirstOrDefault(c =>
            c.Type.Equals("Jellyfin-UserId", StringComparison.OrdinalIgnoreCase))?.Value;
        if (!Guid.TryParse(claim, out var userId) || userId == Guid.Empty)
            return Forbid();

        var selected = PerformerPopularityPreparedIndexService.SelectedGenders(cfg);
        var snapshot = _index.GetSnapshot();
        if (snapshot is null)
            return Ok(new { status = "not_ready", message = "Run 'Refresh StashDB performer Popularity index' in Jellyfin Scheduled Tasks.", entries = Array.Empty<object>() });
        if (!_index.IsCurrent(snapshot, cfg))
            return Ok(new { status = "outdated", message = "Performer gender or page settings changed. Run the performer Popularity task again.", entries = Array.Empty<object>() });

        // Read only saved Person GUIDs. User authorization is checked on every call;
        // cached ID lists are never trusted as proof of item access.
        var entries = snapshot.Items
            .Where(row => selected.Contains(row.Gender) &&
                _library.GetItemById<Person>(row.PersonId, userId) is not null)
            .Select(row => new { personId = row.PersonId.ToString("N"), gender = row.Gender, rank = row.Rank })
            .ToArray();
        return Ok(new
        {
            status = "ready",
            updatedAt = snapshot.UpdatedAtUtc,
            scannedPeople = snapshot.ScannedPeople,
            entries
        });
    }
}
