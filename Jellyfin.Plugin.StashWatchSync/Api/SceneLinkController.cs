using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using JFToStashSync.Services;

namespace JFToStashSync.Api;

/// <summary>
/// Authenticated Jellyfin Web endpoints used by the manual Stash scene-link button
/// on a video's details page.
/// </summary>
[ApiController]
[Route("JFToStashSync")]
[Authorize]
public sealed class SceneLinkController : ControllerBase
{
    private readonly JellyfinUrlSyncService _syncService;
    private readonly ManualSceneLinkJobService _jobService;

    public SceneLinkController(
        JellyfinUrlSyncService syncService,
        ManualSceneLinkJobService jobService)
    {
        _syncService = syncService;
        _jobService = jobService;
    }

    [HttpGet("SceneLinkStatus")]
    public ActionResult<SceneLinkStatusResult> GetSceneLinkStatus([FromQuery] string itemId)
    {
        var result = _syncService.GetSceneLinkStatus(itemId);
        if (!result.Valid)
        {
            return BadRequest(result);
        }

        var activeJob = _jobService.GetActiveForItem(result.ItemId);
        if (activeJob is not null)
        {
            result.IsProcessing = true;
            result.JobId = activeJob.JobId;
        }

        return Ok(result);
    }

    /// <summary>
    /// Starts the long manual metadata-refresh / targeted-scan workflow as a server-side job.
    /// The job deliberately does not use HttpContext.RequestAborted because Jellyfin Web can
    /// rebuild the details page while metadata is refreshed, which aborts the browser request.
    /// </summary>
    [HttpPost("ResolveAndLinkScene")]
    public ActionResult<ManualSceneLinkJobStatus> ResolveAndLinkScene([FromQuery] string itemId)
    {
        var status = _syncService.GetSceneLinkStatus(itemId);
        if (!status.Valid || !status.IsVideo)
        {
            return BadRequest(status);
        }

        var job = _jobService.StartOrGet(itemId);
        if (!job.Valid)
        {
            return BadRequest(job);
        }

        return Ok(job);
    }

    [HttpGet("ResolveAndLinkSceneStatus")]
    public ActionResult<ManualSceneLinkJobStatus> GetResolveAndLinkSceneStatus([FromQuery] string jobId)
    {
        var job = _jobService.Get(jobId);
        if (job is null)
        {
            return NotFound(new ManualSceneLinkJobStatus
            {
                Message = "Manual Stash scene-link job was not found or has expired.",
            });
        }

        return Ok(job);
    }
}
