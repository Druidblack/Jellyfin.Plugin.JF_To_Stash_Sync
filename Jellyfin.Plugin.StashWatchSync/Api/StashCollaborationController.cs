using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using JFToStashSync.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace JFToStashSync.Api;

/// <summary>Per-user authorized view of Stash co-performers, restricted to accessible Jellyfin videos.</summary>
[ApiController]
[Authorize]
[Route("JFToStashSync")]
public sealed class StashCollaborationController : ControllerBase
{
    private readonly StashCollaborationService _service;
    private readonly StashClient _stash;
    private readonly ILogger<StashCollaborationController> _logger;

    public StashCollaborationController(StashCollaborationService service, StashClient stash,
        ILogger<StashCollaborationController> logger)
    {
        _service = service;
        _stash = stash;
        _logger = logger;
    }

    private bool TryContext(string personId, out string stashId, out Guid userId)
    {
        stashId = string.Empty;
        userId = Guid.Empty;
        var cfg = Plugin.Instance?.Configuration;
        if (cfg is null || !cfg.Enabled || !cfg.EnablePersonCoStarsTabs || !_stash.IsConfigured()) return false;
        var claim = User.Claims.FirstOrDefault(c => c.Type.Equals("Jellyfin-UserId", StringComparison.OrdinalIgnoreCase))?.Value;
        if (!Guid.TryParse(claim, out userId) || userId == Guid.Empty) return false;
        var person = _service.AllowedPerson(personId, userId);
        if (person is null) return false;
        // Jellyfin 12 no longer exposes GetProviderId on Person.
        // Read the same ProviderIds dictionary already used throughout this plugin.
        if (person.ProviderIds is null ||
            !person.ProviderIds.TryGetValue("Stash", out var providerId) ||
            string.IsNullOrWhiteSpace(providerId)) return false;
        stashId = providerId;
        return true;
    }

    private async Task<(StashCollaborationService.Snapshot Snapshot, List<(StashCollaborationService.CollaborationScene Scene, string JellyfinId)> Visible)> VisibleAsync(string stashId, Guid userId, CancellationToken ct)
    {
        var snapshot = await _service.GetSnapshotAsync(stashId, ct).ConfigureAwait(false);
        var visible = snapshot.Scenes
            .Select(scene => (Scene: scene, JellyfinId: _service.ResolveAccessibleVideoId(scene, userId)))
            .Where(row => row.JellyfinId is not null)
            .Select(row => (row.Scene, JellyfinId: row.JellyfinId!))
            .ToList();
        return (snapshot, visible);
    }

    [HttpGet("PersonCoStars")]
    public async Task<IActionResult> GetCoStars([FromQuery] string personId, CancellationToken ct)
    {
        if (!TryContext(personId, out var stashId, out var userId)) return NotFound();
        try
        {
            var (snapshot, visible) = await VisibleAsync(stashId, userId, ct).ConfigureAwait(false);
            var selected = StashCollaborationService.ChosenGenders(Plugin.Instance!.Configuration);
            var coStars = visible.SelectMany(row => row.Scene.People)
                .Where(p => p.Id != stashId && StashCollaborationService.GenderAllowed(p.Gender, selected))
                .GroupBy(p => p.Id)
                .Select(group => new { person = group.First(), count = group.Count() })
                .OrderByDescending(row => row.count).ThenBy(row => row.person.Name, StringComparer.OrdinalIgnoreCase)
                .Select(row => new
                {
                    stashId = row.person.Id,
                    name = row.person.Name,
                    gender = row.person.Gender,
                    count = row.count,
                    // A Stash image is available via the authorized proxy, not an exposed API key.
                    hasImage = !string.IsNullOrWhiteSpace(row.person.ImagePath),
                    jellyfinPersonId = _service.AllowedPerson(StashCollaborationService.JellyfinIdFromUrls(row.person.Urls), userId)?.Id.ToString("N")
                }).ToArray();
            return Ok(new { actors = coStars, sceneCount = visible.Count, partial = snapshot.Truncated });
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) {
            _logger.LogWarning(ex, "JFToStashSync: unable to load co-stars for Person {PersonId}", personId);
            return StatusCode(502, new { message = "Не удалось получить сцены из Stash." });
        }
    }

    [HttpGet("PersonCoStarScenes")]
    public async Task<IActionResult> GetScenes([FromQuery] string personId, [FromQuery] string coStashId,
        [FromQuery] int page = 1, CancellationToken ct = default)
    {
        if (!TryContext(personId, out var stashId, out var userId) || string.IsNullOrWhiteSpace(coStashId)) return NotFound();
        try
        {
            var snapshot = await _service.GetSnapshotAsync(stashId, ct).ConfigureAwait(false);
            var selected = StashCollaborationService.ChosenGenders(Plugin.Instance!.Configuration);
            var shared = snapshot.Scenes
                .Where(scene => scene.People.Any(p => p.Id == coStashId && StashCollaborationService.GenderAllowed(p.Gender, selected)))
                .Select(scene => (Scene: scene, JellyfinId: _service.ResolveAccessibleVideoId(scene, userId)))
                .Where(row => row.JellyfinId is not null)
                .Select(row => (row.Scene, JellyfinId: row.JellyfinId!))
                .OrderByDescending(row => row.Scene.Date, StringComparer.Ordinal)
                .ThenBy(row => row.Scene.Title, StringComparer.OrdinalIgnoreCase)
                .ToList();
            const int size = 60;
            var safePage = Math.Clamp(page, 1, 10000);
            var items = shared.Skip((safePage - 1) * size).Take(size)
                .Select(row => new { sceneId = row.Scene.Id, jellyfinId = row.JellyfinId,
                    title = row.Scene.Title, date = row.Scene.Date }).ToArray();
            return Ok(new { items, count = shared.Count, page = safePage, pageSize = size });
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) {
            _logger.LogWarning(ex, "JFToStashSync: unable to load shared scenes for Person {PersonId}", personId);
            return StatusCode(502, new { message = "Не удалось получить совместные сцены." });
        }
    }

    [HttpGet("PersonCoStarImage")]
    public async Task<IActionResult> GetImage([FromQuery] string personId, [FromQuery] string coStashId, CancellationToken ct)
    {
        if (!TryContext(personId, out var stashId, out var userId) || string.IsNullOrWhiteSpace(coStashId)) return NotFound();
        try
        {
            var snapshot = await _service.GetSnapshotAsync(stashId, ct).ConfigureAwait(false);
            var selected = StashCollaborationService.ChosenGenders(Plugin.Instance!.Configuration);
            // Inspect only shared scenes; stop at the first video authorized to this user.
            var performer = snapshot.Scenes
                .Where(scene => scene.People.Any(p => p.Id == coStashId && StashCollaborationService.GenderAllowed(p.Gender, selected)))
                .Where(scene => _service.ResolveAccessibleVideoId(scene, userId) is not null)
                .SelectMany(scene => scene.People)
                .FirstOrDefault(p => p.Id == coStashId);
            if (performer is null || string.IsNullOrWhiteSpace(performer.ImagePath)) return NotFound();
            var image = await _stash.ReadCollaborationImageAsync(performer.ImagePath, ct).ConfigureAwait(false);
            return image is null ? NotFound() : File(image.Value.Bytes, image.Value.ContentType);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception) { return NotFound(); }
    }
}
