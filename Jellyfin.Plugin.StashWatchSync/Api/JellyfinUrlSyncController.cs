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

    public JellyfinUrlSyncController(JellyfinUrlSyncService syncService)
    {
        _syncService = syncService;
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
}
