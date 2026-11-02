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
public sealed class PornhubActorStatsController : ControllerBase
{
    private readonly PornhubActorStatsService _service;
    public PornhubActorStatsController(PornhubActorStatsService service) => _service = service;

    [HttpGet("PornhubActorCached")]
    [Authorize]
    public IActionResult GetCached()
    {
        var config = Plugin.Instance?.Configuration;
        if (config is null || !config.Enabled || (!config.EnablePornhubActorStats && !config.EnablePornhubFeaturedBanner))
            return Ok(Array.Empty<PornhubActorStat>());
        return Ok(_service.GetCached());
    }

    [HttpPost("PornhubActorStatsFetch")]
    [Authorize(Policy = Policies.RequiresElevation)]
    public async Task<IActionResult> Fetch([FromQuery] string personId, [FromQuery] bool force,
        CancellationToken cancellationToken)
    {
        try { return Ok(await _service.FetchOneAsync(personId, force, cancellationToken).ConfigureAwait(false)); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or TimeoutException or System.Net.Http.HttpRequestException)
        { return BadRequest(new { message = ex.Message }); }
    }
}
