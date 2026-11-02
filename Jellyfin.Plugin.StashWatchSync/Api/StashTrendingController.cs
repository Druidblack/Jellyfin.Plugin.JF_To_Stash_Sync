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
public sealed class StashTrendingController : ControllerBase
{
    private readonly TrendingPreparedIndexService _prepared;
    private readonly ILibraryManager _library;

    public StashTrendingController(TrendingPreparedIndexService prepared, ILibraryManager library)
    {
        _prepared = prepared;
        _library = library;
    }

    /// <summary>
    /// Returns only precomputed, user-authorized Jellyfin IDs.
    /// No calls to Stash, StashDB, or a full library query occur here.
    /// </summary>
    [HttpGet("TrendingPrepared")]
    public IActionResult GetPrepared()
    {
        var cfg = Plugin.Instance?.Configuration;
        if (cfg is null || !cfg.Enabled || !cfg.EnableStashDbTrendingRow)
            return NotFound();

        // Never accept a user ID from the request. Authenticated Jellyfin users
        // have their identity encoded in an internal server-issued claim.
        var claim = User.Claims.FirstOrDefault(c =>
            c.Type.Equals("Jellyfin-UserId", StringComparison.OrdinalIgnoreCase))?.Value;
        if (!Guid.TryParse(claim, out var userId) || userId == Guid.Empty)
            return Forbid();

        var snapshot = _prepared.GetSnapshot();
        if (snapshot is null)
            return Ok(new { status = "not_ready", message = "Run 'Refresh StashDB Trending index' in Jellyfin Scheduled Tasks.", entries = Array.Empty<object>() });
        if (!_prepared.IsCurrent(snapshot, cfg))
            return Ok(new { status = "outdated", message = "Trending settings changed. Run the scheduled Trending index task again.", entries = Array.Empty<object>() });

        var accessible = snapshot.Items
            .Where(row => _library.GetItemById<Video>(row.ItemId, userId) is not null)
            .Select(row => new { itemId = row.ItemId.ToString("N"), rank = row.Rank })
            .ToArray();
        return Ok(new
        {
            status = "ready",
            updatedAt = snapshot.UpdatedAtUtc,
            scannedVideos = snapshot.ScannedVideos,
            entries = accessible
        });
    }
}
