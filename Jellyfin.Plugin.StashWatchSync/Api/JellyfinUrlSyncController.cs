using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Common.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using StashWatchSync.Services;

namespace StashWatchSync.Api;

[ApiController]
[Route("JFToStashSync")]
[Authorize(Policy = Policies.RequiresElevation)]
public sealed class JellyfinUrlSyncController : ControllerBase
{
    private readonly JellyfinUrlSyncService _syncService;
    private readonly JellyfinPerformerUrlSyncService _performerSyncService;

    public JellyfinUrlSyncController(
        JellyfinUrlSyncService syncService,
        JellyfinPerformerUrlSyncService performerSyncService)
    {
        _syncService = syncService;
        _performerSyncService = performerSyncService;
    }

    [HttpPost("SyncJellyfinUrl")]
    public async Task<ActionResult<JellyfinUrlSyncResult>> SyncJellyfinUrl(
        [FromQuery] string itemId,
        CancellationToken cancellationToken)
    {
        var result = await _syncService.SyncByItemIdAsync(itemId, cancellationToken).ConfigureAwait(false);
        if (!result.Success)
        {
            return BadRequest(result);
        }

        return Ok(result);
    }

    [HttpPost("SyncJellyfinPerformerUrl")]
    public async Task<ActionResult<JellyfinPerformerUrlSyncResult>> SyncJellyfinPerformerUrl(
        [FromQuery] string personId,
        CancellationToken cancellationToken)
    {
        var result = await _performerSyncService
            .SyncByPersonIdAsync(personId, cancellationToken)
            .ConfigureAwait(false);

        if (!result.Success)
        {
            return BadRequest(result);
        }

        return Ok(result);
    }
}
