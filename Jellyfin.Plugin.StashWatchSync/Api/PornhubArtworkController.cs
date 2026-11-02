using System;
using System.Threading;
using System.Threading.Tasks;
using JFToStashSync.Services;
using MediaBrowser.Common.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace JFToStashSync.Api;

[ApiController]
[Route("JFToStashSync")]
public sealed class PornhubArtworkController : ControllerBase
{
    private readonly PornhubActorArtworkService _artwork;
    public PornhubArtworkController(PornhubActorArtworkService artwork) => _artwork = artwork;

    [HttpPost("PornhubArtworkInspect")]
    [Authorize(Policy = Policies.RequiresElevation)]
    public async Task<IActionResult> Inspect([FromQuery] string personId, CancellationToken cancellationToken)
    {
        try { return Ok(await _artwork.InspectAsync(personId, cancellationToken).ConfigureAwait(false)); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or TimeoutException or System.Net.Http.HttpRequestException or OperationCanceledException)
        { return BadRequest(new { message = ex.Message }); }
    }

    [HttpGet("PornhubArtworkPreview")]
    [Authorize(Policy = Policies.RequiresElevation)]
    public IActionResult Preview([FromQuery] string personId, [FromQuery] string kind)
    {
        var image = _artwork.GetPreview(personId, kind);
        if (image is null) return NotFound();
        Response.Headers["Cache-Control"] = "private, no-store, max-age=0";
        Response.Headers["X-Content-Type-Options"] = "nosniff";
        return File(image.Value.Data, image.Value.Mime);
    }

    [HttpPost("PornhubArtworkApply")]
    [Authorize(Policy = Policies.RequiresElevation)]
    public async Task<IActionResult> Apply([FromQuery] string personId, [FromQuery] bool logo,
        [FromQuery] bool banner, CancellationToken cancellationToken)
    {
        try { return Ok(await _artwork.ApplyAsync(personId, logo, banner, cancellationToken).ConfigureAwait(false)); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or TimeoutException)
        { return BadRequest(new { message = ex.Message }); }
    }
}
