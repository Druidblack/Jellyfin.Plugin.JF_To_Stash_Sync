using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Jellyfin.Data.Enums;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;

namespace JFToStashSync.Services;

/// <summary>
/// Persistent, bounded scheduled-job state. Image timestamps are independent of
/// Pornhub ratings and of each other. Retry cooldowns avoid starving subsequent
/// actors with a repeatedly unavailable profile. Manual tools do not use cooldowns.
/// </summary>
public sealed class PornhubScheduledStateService
{
    private sealed class Entry
    {
        public string PersonId { get; set; } = string.Empty;
        public DateTime? LogoRefreshedUtc { get; set; }
        public DateTime? BannerRefreshedUtc { get; set; }
        public DateTime? StatsRetryAfterUtc { get; set; }
        public string? StatsFailureKind { get; set; } // MissingProfile or TemporaryError (legacy: null)
        public DateTime? ArtworkRetryAfterUtc { get; set; }
    }

    private readonly string _path;
    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _items = new(StringComparer.OrdinalIgnoreCase);
    private readonly ILogger<PornhubScheduledStateService> _logger;

    public PornhubScheduledStateService(IApplicationPaths paths, ILogger<PornhubScheduledStateService> logger)
    {
        _logger = logger;
        _path = Path.Combine(paths.DataPath, "JFToStashSync", "pornhub-scheduled-state.json");
        try
        {
            if (!File.Exists(_path)) return;
            var entries = JsonConvert.DeserializeObject<List<Entry>>(File.ReadAllText(_path));
            if (entries is null || entries.Count > 100000) return;
            foreach (var entry in entries)
            {
                if (Guid.TryParse(entry.PersonId, out var id))
                    _items[id.ToString("N")] = entry;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "JFToStashSync: Pornhub scheduled state could not be read.");
        }
    }

    private Entry GetOrCreateLocked(string id)
    {
        if (!_items.TryGetValue(id, out var entry))
            _items[id] = entry = new Entry { PersonId = id };
        return entry;
    }

    public bool IsDeferred(string personId, bool statistics)
    {
        lock (_gate)
        {
            if (!_items.TryGetValue(personId, out var entry)) return false;
            var retryAt = statistics ? entry.StatsRetryAfterUtc : entry.ArtworkRetryAfterUtc;
            return retryAt > DateTime.UtcNow;
        }
    }

    /// <summary>
    /// Priority 0: never checked; 1: expired successful cache;
    /// 2: previously confirmed missing profile; 3: retryable failure.
    /// Only used by the scheduled job; manual fetching bypasses retries.
    /// Legacy retry entries without a failure kind are treated as temporary failures.
    /// </summary>
    public int GetStatsQueuePriority(string personId, bool hasCachedRating)
    {
        if (hasCachedRating) return 1;
        lock (_gate)
        {
            if (!_items.TryGetValue(personId, out var entry) ||
                (entry.StatsRetryAfterUtc is null && string.IsNullOrEmpty(entry.StatsFailureKind)))
                return 0;
            return entry.StatsFailureKind == "MissingProfile" ? 2 : 3;
        }
    }

    /// <summary>A known missing profile must not prompt artwork-only network attempts during its TTL.</summary>
    public bool IsMissingProfileDeferred(string personId)
    {
        lock (_gate)
            return _items.TryGetValue(personId, out var entry) &&
                   entry.StatsFailureKind == "MissingProfile" && entry.StatsRetryAfterUtc > DateTime.UtcNow;
    }

    public void Defer(string personId, bool statistics, bool missingProfile = false)
    {
        lock (_gate)
        {
            var entry = GetOrCreateLocked(personId);
            if (statistics)
            {
                entry.StatsFailureKind = missingProfile ? "MissingProfile" : "TemporaryError";
                entry.StatsRetryAfterUtc = DateTime.UtcNow.AddDays(missingProfile ? 30 : 1);
            }
            else entry.ArtworkRetryAfterUtc = DateTime.UtcNow.AddDays(1);
            PersistLocked();
        }
    }

    public void ClearDefer(string personId, bool statistics)
    {
        lock (_gate)
        {
            if (!_items.TryGetValue(personId, out var entry)) return;
            if (statistics)
            {
                if (entry.StatsRetryAfterUtc is null && entry.StatsFailureKind is null) return;
                entry.StatsRetryAfterUtc = null;
                entry.StatsFailureKind = null;
            }
            else
            {
                if (entry.ArtworkRetryAfterUtc is null) return;
                entry.ArtworkRetryAfterUtc = null;
            }
            PersistLocked();
        }
    }

    /// <summary>Determines which images require downloading without any HTTP request.</summary>
    public (bool Logo, bool Banner) GetDueArtwork(Person person, string updateMode, int cacheDays)
    {
        var personId = person.Id.ToString("N");
        lock (_gate)
        {
            if (_items.TryGetValue(personId, out var entry) &&
                entry.ArtworkRetryAfterUtc > DateTime.UtcNow) return (false, false);
            var days = cacheDays is 1 or 7 or 30 ? cacheDays : 7;
            return (
                IsImageDue(person, ImageType.Logo, entry?.LogoRefreshedUtc, updateMode, days),
                IsImageDue(person, ImageType.Banner, entry?.BannerRefreshedUtc, updateMode, days)
            );
        }
    }

    private static bool IsImageDue(Person person, ImageType type, DateTime? lastSavedUtc, string mode, int days)
    {
        var image = person.GetImageInfo(type, 0);
        if (image is null) return true;
        if (image.IsLocalFile && !string.IsNullOrEmpty(image.Path) && !File.Exists(image.Path)) return true;
        if (mode != "AfterCacheExpiry") return false;

        // When upgrading from the manual-only plugin or when an image was edited
        // outside the plugin, use the existing Jellyfin file's modification date
        // as the effective cache timestamp. Do not needlessly replace new artwork.
        var updatedAt = lastSavedUtc ?? DateTime.MinValue;
        if (image.IsLocalFile && !string.IsNullOrEmpty(image.Path) && File.Exists(image.Path))
        {
            var fileTime = File.GetLastWriteTimeUtc(image.Path);
            if (fileTime > updatedAt) updatedAt = fileTime;
        }
        return updatedAt <= DateTime.UtcNow.AddDays(-days);
    }

    public void RecordImageSaved(string personId, ImageType type)
    {
        lock (_gate)
        {
            var entry = GetOrCreateLocked(personId);
            if (type == ImageType.Logo) entry.LogoRefreshedUtc = DateTime.UtcNow;
            else if (type == ImageType.Banner) entry.BannerRefreshedUtc = DateTime.UtcNow;
            entry.ArtworkRetryAfterUtc = null;
            // Persist after each successful type, so partial success survives failures
            // or a server shutdown before the second image is downloaded.
            PersistLocked();
        }
    }

    private void PersistLocked()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var temp = _path + ".tmp";
        File.WriteAllText(temp, JsonConvert.SerializeObject(_items.Values.ToArray(), Formatting.Indented));
        File.Move(temp, _path, true);
    }
}
