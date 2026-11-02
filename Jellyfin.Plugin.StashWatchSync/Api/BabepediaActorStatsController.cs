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
public sealed class BabepediaActorStatsController : ControllerBase
{
    private readonly BabepediaActorStatsService _service;
    public BabepediaActorStatsController(BabepediaActorStatsService service) => _service = service;

    [HttpGet("BabepediaActorCached")]
    [Authorize]
    public IActionResult GetCached()
    {
        var cfg = Plugin.Instance?.Configuration;
        if (cfg is null || !cfg.Enabled || (!cfg.EnableBabepediaActorStats && !cfg.EnablePornhubFeaturedBanner))
            return Ok(Array.Empty<BabepediaActorStat>());
        return Ok(_service.GetCached());
    }

    [HttpPost("BabepediaActorFetch")]
    [Authorize(Policy = Policies.RequiresElevation)]
    public async Task<IActionResult> Fetch([FromQuery] string personId, CancellationToken cancellationToken)
    {
        try { return Ok(await _service.FetchOneAsync(personId, cancellationToken).ConfigureAwait(false)); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or TimeoutException or System.Net.Http.HttpRequestException or OperationCanceledException)
        { return BadRequest(new { message = ex.Message }); }
    }
}
