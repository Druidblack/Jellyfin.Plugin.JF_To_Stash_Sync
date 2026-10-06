using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using JFToStashSync.Services;

namespace JFToStashSync.Api;

[ApiController]
[Route("JFToStashSync")]
public sealed class PlayerOCounterController : ControllerBase
{
    private readonly OCounterSyncService _syncService;
    private readonly ILogger<PlayerOCounterController> _logger;

    public PlayerOCounterController(
        OCounterSyncService syncService,
        ILogger<PlayerOCounterController> logger)
    {
        _syncService = syncService;
        _logger = logger;
    }

    /// <summary>
    /// Browser-loaded client script. A script tag cannot reliably attach Jellyfin API auth,
    /// so this one endpoint is intentionally anonymous; all mutating API calls remain authorized.
    /// </summary>
    [HttpGet("PlayerOCounterScript")]
    [AllowAnonymous]
    public IActionResult GetPlayerOCounterScript()
    {
        const string resourceName = "JFToStashSync.Web.player-o-button.js";
        var assembly = typeof(PlayerOCounterController).Assembly;
        using var stream = assembly.GetManifestResourceStream(resourceName);
        if (stream is null)
        {
            _logger.LogWarning(
                "JFToStashSync: embedded Jellyfin Web integration script resource {ResourceName} was not found.",
                resourceName);
            return NotFound();
        }

        using var reader = new StreamReader(stream);
        _logger.LogInformation("JFToStashSync: serving Jellyfin Web integration script.");
        return Content(reader.ReadToEnd(), "application/javascript; charset=utf-8");
    }

    [HttpGet("PlayerOCounterConfig")]
    [Authorize]
    public ActionResult<PlayerOCounterConfigResult> GetPlayerOCounterConfig()
    {
        var cfg = Plugin.Instance?.Configuration;
        var enabled = cfg is not null && cfg.Enabled && cfg.EnablePlayerOCounterButton;
        var actorListEnabled = cfg is not null && cfg.Enabled && cfg.EnablePlayerActorListButton;
        var actorGenderIconsEnabled = cfg is not null && cfg.Enabled && cfg.ShowActorGenderIcons;
        var personOverviewLinksEnabled = cfg is not null && cfg.Enabled && cfg.LinkifyPersonOverviewUrls;
        var personSocialIconsEnabled = cfg is not null && cfg.Enabled && cfg.ShowPersonSocialIcons;

        _logger.LogInformation(
            "JFToStashSync: Jellyfin Web client requested configuration. oCounterEnabled={OCounterEnabled} actorListEnabled={ActorListEnabled} actorGenderIconsEnabled={ActorGenderIconsEnabled} personOverviewLinksEnabled={PersonOverviewLinksEnabled} personSocialIconsEnabled={PersonSocialIconsEnabled}",
            enabled,
            actorListEnabled,
            actorGenderIconsEnabled,
            personOverviewLinksEnabled,
            personSocialIconsEnabled);

        return Ok(new PlayerOCounterConfigResult
        {
            Enabled = enabled,
            ActorListEnabled = actorListEnabled,
            ActorGenderIconsEnabled = actorGenderIconsEnabled,
            PersonOverviewLinksEnabled = personOverviewLinksEnabled,
            PersonSocialIconsEnabled = personSocialIconsEnabled,
        });
    }

    [HttpPost("IncrementO")]
    [Authorize]
    public async Task<ActionResult<OCounterSyncResult>> IncrementO(
        [FromQuery] string itemId,
        [FromQuery] string userId,
        CancellationToken cancellationToken)
    {
        var result = await _syncService
            .IncrementByItemIdAsync(itemId, userId, cancellationToken)
            .ConfigureAwait(false);

        if (!result.Success)
        {
            return BadRequest(result);
        }

        return Ok(result);
    }
}

public sealed class PlayerOCounterConfigResult
{
    public bool Enabled { get; set; }

    public bool ActorListEnabled { get; set; }

    public bool ActorGenderIconsEnabled { get; set; }

    public bool PersonOverviewLinksEnabled { get; set; }

    public bool PersonSocialIconsEnabled { get; set; }
}
