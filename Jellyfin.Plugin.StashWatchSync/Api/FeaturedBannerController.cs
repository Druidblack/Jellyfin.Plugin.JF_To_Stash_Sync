using System;
using System.Linq;
using JFToStashSync.Services;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace JFToStashSync.Api;

[ApiController]
[Authorize]
[Route("JFToStashSync")]
public sealed class FeaturedBannerController : ControllerBase
{
    private readonly FeaturedBannerCandidateService _candidates;
    private readonly ILibraryManager _library;

    public FeaturedBannerController(FeaturedBannerCandidateService candidates, ILibraryManager library)
    {
        _candidates = candidates;
        _library = library;
    }

    [HttpGet("FeaturedBannerCandidates")]
    public IActionResult GetCandidates()
    {
        var config = Plugin.Instance?.Configuration;
        if (config is null || !config.Enabled || !config.EnablePornhubFeaturedBanner) return NotFound();
        var claim = User.Claims.FirstOrDefault(x =>
            x.Type.Equals("Jellyfin-UserId", StringComparison.OrdinalIgnoreCase))?.Value;
        if (!Guid.TryParse(claim, out var userId) || userId == Guid.Empty) return Forbid();
        var ids = _candidates.GetIds()
            .Where(id => _library.GetItemById<Person>(id, userId) is not null)
            .Select(id => id.ToString("N"))
            .ToArray();
        return Ok(new { ids });
    }
}
