using System;
using System.IO;
using System.Linq;
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

    // Always serve the same locale helper before the requested UI module; each module
    // can also run on its own with its built-in English/Russian fallback.
    private static string LocalizedWebScript(string script)
    {
        using var stream = typeof(PlayerOCounterController).Assembly
            .GetManifestResourceStream("JFToStashSync.Web.ui-i18n.js");
        if (stream is null) return script;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd() + "\n" + script;
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
        _logger.LogDebug("JFToStashSync: serving Jellyfin Web integration script.");
        return Content(LocalizedWebScript(reader.ReadToEnd()), "application/javascript; charset=utf-8");
    }

    /// <summary>Publicly loadable script; all data endpoints still require Jellyfin authentication.</summary>
    [HttpGet("TrendingHomeScript")]
    [AllowAnonymous]
    public IActionResult GetTrendingHomeScript()
    {
        using var stream = typeof(PlayerOCounterController).Assembly
            .GetManifestResourceStream("JFToStashSync.Web.trending-home.js");
        if (stream is null)
        {
            return NotFound();
        }

        using var reader = new StreamReader(stream);
        return Content(LocalizedWebScript(reader.ReadToEnd()), "application/javascript; charset=utf-8");
    }

    [HttpGet("PerformerPopularityHomeScript")]
    [AllowAnonymous]
    public IActionResult GetPerformerPopularityHomeScript()
    {
        using var stream = typeof(PlayerOCounterController).Assembly
            .GetManifestResourceStream("JFToStashSync.Web.performer-popularity-home.js");
        if (stream is null)
        {
            _logger.LogWarning("JFToStashSync: performer Popularity home script embedded resource not found.");
            return NotFound();
        }
        using var reader = new StreamReader(stream);
        return Content(LocalizedWebScript(reader.ReadToEnd()), "application/javascript; charset=utf-8");
    }

    [HttpGet("PopularityHomeScript")]
    [AllowAnonymous]
    public IActionResult GetPopularityHomeScript()
    {
        using var stream = typeof(PlayerOCounterController).Assembly
            .GetManifestResourceStream("JFToStashSync.Web.popularity-home.js");
        if (stream is null)
        {
            _logger.LogWarning("JFToStashSync: Popularity home script embedded resource not found.");
            return NotFound();
        }

        using var reader = new StreamReader(stream);
        return Content(LocalizedWebScript(reader.ReadToEnd()), "application/javascript; charset=utf-8");
    }

    [HttpGet("RankedPerformerFlagsScript")]
    [AllowAnonymous]
    public IActionResult GetRankedPerformerFlagsScript()
    {
        using var stream = typeof(PlayerOCounterController).Assembly
            .GetManifestResourceStream("JFToStashSync.Web.ranked-performer-flags.js");
        if (stream is null) return NotFound();
        using var reader = new StreamReader(stream);
        return Content(LocalizedWebScript(reader.ReadToEnd()), "application/javascript; charset=utf-8");
    }

    [HttpGet("BabepediaActorStatsScript")]
    [AllowAnonymous]
    public IActionResult GetBabepediaActorStatsScript()
    {
        using var stream = typeof(PlayerOCounterController).Assembly
            .GetManifestResourceStream("JFToStashSync.Web.babepedia-actor-stats.js");
        if (stream is null) return NotFound();
        using var reader = new StreamReader(stream);
        return Content(LocalizedWebScript(reader.ReadToEnd()), "application/javascript; charset=utf-8");
    }

    [HttpGet("PersonDetailEnhancementsScript")]
    [AllowAnonymous]
    public IActionResult GetPersonDetailEnhancementsScript()
    {
        using var stream = typeof(PlayerOCounterController).Assembly
            .GetManifestResourceStream("JFToStashSync.Web.person-detail-enhancements.js");
        if (stream is null) return NotFound();
        using var reader = new StreamReader(stream);
        return Content(LocalizedWebScript(reader.ReadToEnd()), "application/javascript; charset=utf-8");
    }

    [HttpGet("PornhubActorStatsScript")]
    [AllowAnonymous]
    public IActionResult GetPornhubActorStatsScript()
    {
        using var stream = typeof(PlayerOCounterController).Assembly
            .GetManifestResourceStream("JFToStashSync.Web.pornhub-actor-stats.js");
        if (stream is null) return NotFound();
        using var reader = new StreamReader(stream);
        return Content(LocalizedWebScript(reader.ReadToEnd()), "application/javascript; charset=utf-8");
    }

    [HttpGet("BabepediaTopHomeScript")]
    [AllowAnonymous]
    public IActionResult GetBabepediaTopHomeScript()
    {
        using var stream = typeof(PlayerOCounterController).Assembly
            .GetManifestResourceStream("JFToStashSync.Web.babepedia-top-home.js");
        if (stream is null) return NotFound();
        using var reader = new StreamReader(stream);
        return Content(LocalizedWebScript(reader.ReadToEnd()), "application/javascript; charset=utf-8");
    }

    [HttpGet("HomeDataWarmupScript")]
    [AllowAnonymous]
    public IActionResult GetHomeDataWarmupScript()
    {
        using var stream = typeof(PlayerOCounterController).Assembly
            .GetManifestResourceStream("JFToStashSync.Web.home-data-warmup.js");
        if (stream is null) return NotFound();
        using var reader = new StreamReader(stream);
        return Content(LocalizedWebScript(reader.ReadToEnd()), "application/javascript; charset=utf-8");
    }

    [HttpGet("HomeInitialLoaderScript")]
    [AllowAnonymous]
    public IActionResult GetHomeInitialLoaderScript()
    {
        using var stream = typeof(PlayerOCounterController).Assembly
            .GetManifestResourceStream("JFToStashSync.Web.home-initial-loader.js");
        if (stream is null) return NotFound();
        using var reader = new StreamReader(stream);
        return Content(LocalizedWebScript(reader.ReadToEnd()), "application/javascript; charset=utf-8");
    }

    [HttpGet("FeaturedMediaBannerScript")]
    [AllowAnonymous]
    public IActionResult GetFeaturedMediaBannerScript()
    {
        using var stream = typeof(PlayerOCounterController).Assembly
            .GetManifestResourceStream("JFToStashSync.Web.featured-media-banner.js");
        if (stream is null) return NotFound();
        using var reader = new StreamReader(stream);
        return Content(LocalizedWebScript(reader.ReadToEnd()), "application/javascript; charset=utf-8");
    }

    [HttpGet("PornhubFeaturedBannerScript")]
    [AllowAnonymous]
    public IActionResult GetPornhubFeaturedBannerScript()
    {
        using var stream = typeof(PlayerOCounterController).Assembly
            .GetManifestResourceStream("JFToStashSync.Web.pornhub-featured-banner.js");
        if (stream is null) return NotFound();
        using var reader = new StreamReader(stream);
        return Content(LocalizedWebScript(reader.ReadToEnd()), "application/javascript; charset=utf-8");
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
        var trendingEnabled = cfg is not null && cfg.Enabled && cfg.EnableStashDbTrendingRow;
        var popularityEnabled = cfg is not null && cfg.Enabled && cfg.EnableStashDbPopularityRow;

        _logger.LogDebug(
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
            PersonCoStarsTabsEnabled = cfg is not null && cfg.Enabled && cfg.EnablePersonCoStarsTabs,
            PersonSocialIconsEnabled = personSocialIconsEnabled,
            TrendingEnabled = trendingEnabled,
            TrendingLibraryId = trendingEnabled ? cfg?.StashDbTrendingLibraryId ?? string.Empty : string.Empty,
            TrendingPages = trendingEnabled ? Math.Clamp(cfg?.StashDbTrendingPages ?? 5, 1, 10) : 0,
            PopularityEnabled = popularityEnabled,
            PopularityLibraryId = popularityEnabled ? cfg?.StashDbPopularityLibraryId ?? string.Empty : string.Empty,
            PopularityPages = popularityEnabled ? Math.Clamp(cfg?.StashDbPopularityPages ?? 5, 1, 10) : 0,
            HighlightActorPortraits = cfg is not null && cfg.Enabled && cfg.HighlightActorPortraits,
            HighlightRankedVideoStates = cfg is not null && cfg.Enabled && cfg.HighlightRankedVideoStates,
            ShowRankedPerformerBirthFlags = cfg is not null && cfg.Enabled && cfg.ShowRankedPerformerBirthFlags,
            BabepediaActorStatsEnabled = cfg is not null && cfg.Enabled && cfg.EnableBabepediaActorStats,
            PornhubActorStatsEnabled = cfg is not null && cfg.Enabled && cfg.EnablePornhubActorStats,
            FeaturedPornhubBannerEnabled = cfg is not null && cfg.Enabled && cfg.EnablePornhubFeaturedBanner,
            FeaturedMediaEnabled = cfg is not null && cfg.Enabled && cfg.EnableFeaturedMediaBanner,
            FeaturedMediaIncludeWatched = cfg?.FeaturedMediaIncludeWatched ?? false,
            FeaturedMediaRequireLogo = cfg?.FeaturedMediaRequireLogo ?? false,
            FeaturedMediaShowOverview = cfg?.FeaturedMediaShowOverview ?? true,
            FeaturedMediaShowRatings = cfg?.FeaturedMediaShowRatings ?? true,
            FeaturedMediaShowProgress = cfg?.FeaturedMediaShowProgress ?? true,
            FeaturedMediaPauseOnHover = cfg?.FeaturedMediaPauseOnHover ?? false,
            FeaturedMediaSortBy = cfg?.FeaturedMediaSortBy ?? "Random",
            FeaturedMediaSlideSeconds = Math.Clamp(cfg?.FeaturedMediaSlideSeconds ?? 15, 5, 120),
            FeaturedMediaMaxItems = Math.Clamp(cfg?.FeaturedMediaMaxItems ?? 30, 1, 100),
            FeaturedMediaLibraryIds = cfg?.FeaturedMediaLibraryIds ?? string.Empty,
            FeaturedBannerSlideSeconds = Math.Clamp(cfg?.FeaturedBannerSlideSeconds ?? 15, 5, 120),
            FeaturedBannerMaxItems = Math.Clamp(cfg?.FeaturedBannerMaxItems ?? 30, 1, 100),
            FeaturedBannerShowProgress = cfg?.FeaturedBannerShowProgress ?? true,
            FeaturedBannerPauseOnHover = cfg?.FeaturedBannerPauseOnHover ?? false,
            FeaturedBannerOrder = cfg?.FeaturedBannerOrder == "Ranked" ? "Ranked" : "Random",
            FeaturedBannerRankingSource = cfg?.FeaturedBannerRankingSource == "Babepedia" ? "Babepedia" : "Pornhub",
            FeaturedBannerShowPornhubModelRank = cfg?.FeaturedBannerShowPornhubModelRank ?? false,
            FeaturedBannerShowPornhubWeeklyRank = cfg?.FeaturedBannerShowPornhubWeeklyRank ?? false,
            FeaturedBannerShowPornhubMonthlyRank = cfg?.FeaturedBannerShowPornhubMonthlyRank ?? false,
            FeaturedBannerShowPornhubLastMonthRank = cfg?.FeaturedBannerShowPornhubLastMonthRank ?? false,
            FeaturedBannerShowPornhubYearlyRank = cfg?.FeaturedBannerShowPornhubYearlyRank ?? false,
            FeaturedBannerShowPornhubVideoViews = cfg?.FeaturedBannerShowPornhubVideoViews ?? false,
            FeaturedBannerShowPornhubSubscribers = cfg?.FeaturedBannerShowPornhubSubscribers ?? false,
            FeaturedBannerShowBabepediaRating = cfg?.FeaturedBannerShowBabepediaRating ?? false,
            FeaturedBannerShowBabepediaRank = cfg?.FeaturedBannerShowBabepediaRank ?? false,
            FeaturedBannerShowBabepediaFavorites = cfg?.FeaturedBannerShowBabepediaFavorites ?? false,
            FeaturedBannerShowBabepediaVotes = cfg?.FeaturedBannerShowBabepediaVotes ?? false,
            BabepediaTopEnabled = cfg is not null && cfg.Enabled && cfg.EnableBabepediaTopRows,
            BabepediaTopCategories = cfg is not null && cfg.Enabled && cfg.EnableBabepediaTopRows
                ? BabepediaTopPreparedIndexService.SelectedCategories(cfg).ToArray()
                : Array.Empty<string>(),
            PerformerPopularityEnabled = cfg is not null && cfg.Enabled && cfg.EnableStashDbPerformerPopularityRows,
            PerformerPopularityGenders = cfg is not null && cfg.Enabled && cfg.EnableStashDbPerformerPopularityRows
                ? PerformerPopularityPreparedIndexService.SelectedGenders(cfg).ToArray()
                : Array.Empty<string>(),
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

    public bool PersonCoStarsTabsEnabled { get; set; }

    public bool PersonOverviewLinksEnabled { get; set; }

    public bool PersonSocialIconsEnabled { get; set; }

    public bool TrendingEnabled { get; set; }

    public string TrendingLibraryId { get; set; } = string.Empty;

    public int TrendingPages { get; set; }

    public bool PopularityEnabled { get; set; }

    public string PopularityLibraryId { get; set; } = string.Empty;

    public int PopularityPages { get; set; }

    public bool HighlightActorPortraits { get; set; }

    public bool HighlightRankedVideoStates { get; set; }

    public bool ShowRankedPerformerBirthFlags { get; set; }

    public bool BabepediaActorStatsEnabled { get; set; }

    public bool PornhubActorStatsEnabled { get; set; }

    public bool FeaturedPornhubBannerEnabled { get; set; }
    public bool FeaturedMediaEnabled { get; set; }
    public bool FeaturedMediaIncludeWatched { get; set; }
    public bool FeaturedMediaRequireLogo { get; set; }
    public bool FeaturedMediaShowOverview { get; set; }
    public bool FeaturedMediaShowRatings { get; set; }
    public bool FeaturedMediaShowProgress { get; set; }
    public bool FeaturedMediaPauseOnHover { get; set; }
    public string FeaturedMediaSortBy { get; set; } = "Random";
    public int FeaturedMediaSlideSeconds { get; set; }
    public int FeaturedMediaMaxItems { get; set; }
    public string FeaturedMediaLibraryIds { get; set; } = string.Empty;


    public int FeaturedBannerSlideSeconds { get; set; }
    public int FeaturedBannerMaxItems { get; set; }
    public bool FeaturedBannerShowProgress { get; set; }
    public bool FeaturedBannerPauseOnHover { get; set; }
    public string FeaturedBannerOrder { get; set; } = "Random";
    public string FeaturedBannerRankingSource { get; set; } = "Pornhub";
    public bool FeaturedBannerShowPornhubModelRank { get; set; }
    public bool FeaturedBannerShowPornhubWeeklyRank { get; set; }
    public bool FeaturedBannerShowPornhubMonthlyRank { get; set; }
    public bool FeaturedBannerShowPornhubLastMonthRank { get; set; }
    public bool FeaturedBannerShowPornhubYearlyRank { get; set; }
    public bool FeaturedBannerShowPornhubVideoViews { get; set; }
    public bool FeaturedBannerShowPornhubSubscribers { get; set; }
    public bool FeaturedBannerShowBabepediaRating { get; set; }
    public bool FeaturedBannerShowBabepediaRank { get; set; }
    public bool FeaturedBannerShowBabepediaFavorites { get; set; }
    public bool FeaturedBannerShowBabepediaVotes { get; set; }

    public bool BabepediaTopEnabled { get; set; }

    public string[] BabepediaTopCategories { get; set; } = Array.Empty<string>();

    public bool PerformerPopularityEnabled { get; set; }

    public string[] PerformerPopularityGenders { get; set; } = Array.Empty<string>();
}
