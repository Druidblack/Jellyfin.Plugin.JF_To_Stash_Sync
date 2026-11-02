using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using JFToStashSync.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Jellyfin.Data.Enums;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;

namespace JFToStashSync.Services;

public sealed class PopularityPreparedIndexService
{
    private const int FormatVersion = 1;
    private readonly ILibraryManager _library;
    private readonly StashDbTrendingService _rankings;
    private readonly ILogger<PopularityPreparedIndexService> _logger;
    private readonly string _path;
    private readonly SemaphoreSlim _updateLock = new(1, 1);
    private readonly object _readLock = new();
    private PopularityIndexSnapshot? _current;

    public PopularityPreparedIndexService(
        ILibraryManager library,
        StashDbTrendingService rankings,
        IApplicationPaths paths,
        ILogger<PopularityPreparedIndexService> logger)
    {
        _library = library;
        _rankings = rankings;
        _logger = logger;
        // Stable across plugin upgrades; do not store the snapshot inside a versioned DLL directory.
        _path = Path.Combine(paths.DataPath, "JFToStashSync", "popularity-index.json");
        ReadSnapshot();
    }

    private void ReadSnapshot()
    {
        try
        {
            if (!File.Exists(_path)) return;
            var contents = File.ReadAllText(_path);
            var snapshot = JsonConvert.DeserializeObject<PopularityIndexSnapshot>(contents);
            if (snapshot?.Version == FormatVersion && snapshot.Items is not null && snapshot.Items.Count <= 100000)
            {
                lock (_readLock) _current = snapshot;
                _logger.LogDebug("JFToStashSync: Popularity index restored: {Count} items, updated {Updated} UTC.",
                    snapshot.Items.Count, snapshot.UpdatedAtUtc);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "JFToStashSync: cannot read Popularity index; run the scheduled task to rebuild it.");
        }
    }

    public PopularityIndexSnapshot? GetSnapshot()
    {
        lock (_readLock) return _current;
    }

    public bool IsCurrent(PopularityIndexSnapshot snapshot, PluginConfiguration cfg)
        => snapshot.Version == FormatVersion && snapshot.ConfigurationSignature == Signature(cfg);

    // No API key, password or full media metadata on disk.
    private static string Signature(PluginConfiguration cfg)
    {
        var text = (cfg.StashEndpoint ?? "").Trim().TrimEnd('/').ToLowerInvariant()
            + "|" + (cfg.StashDbPopularityLibraryId ?? "").Trim().ToLowerInvariant()
            + "|" + Math.Clamp(cfg.StashDbPopularityPages, 1, 10).ToString(System.Globalization.CultureInfo.InvariantCulture);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    }

    public async Task<int> RefreshAsync(IProgress<double> progress, CancellationToken ct)
    {
        var cfg = Plugin.Instance?.Configuration;
        if (cfg is null || !cfg.Enabled || !cfg.EnableStashDbPopularityRow)
        {
            _logger.LogDebug("JFToStashSync: Popularity index refresh skipped; Popularity is disabled.");
            progress.Report(100);
            return 0;
        }

        await _updateLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Avoid concurrent library scans if both rankings refresh at startup.
            await StashDbIndexRefreshGate.Shared.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                _logger.LogDebug("JFToStashSync: Popularity index refresh started (pages={Pages}, library={Library}).",
                    cfg.StashDbPopularityPages, string.IsNullOrWhiteSpace(cfg.StashDbPopularityLibraryId) ? "all" : cfg.StashDbPopularityLibraryId);
                progress.Report(5);
                var scenes = await _rankings.GetTopEntriesAsync(cfg.StashDbPopularityPages, "POPULARITY", ct).ConfigureAwait(false);
                progress.Report(45);
                var wanted = scenes.GroupBy(e => e.StashSceneId, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(group => group.Key, group => group.Min(e => e.Rank), StringComparer.OrdinalIgnoreCase);

                // Jellyfin 12 filters Stash ProviderIds in its database. Never
                // load the entire video library into managed memory.
                Guid libraryId = Guid.Empty;
                if (!string.IsNullOrWhiteSpace(cfg.StashDbPopularityLibraryId))
                {
                    if (!Guid.TryParse(cfg.StashDbPopularityLibraryId, out libraryId))
                        throw new InvalidOperationException("Invalid Popularity library ID in plugin settings.");
                    if (_library.GetItemById(libraryId) is not Folder)
                        throw new InvalidOperationException("Selected Popularity library no longer exists.");
                }
                var items = new List<PopularityPreparedItem>();
                var scanned = 0;
                foreach (var candidate in StashProviderIdBatchLookup.FindVideos(
                    _library, wanted.Keys, libraryId, ct))
                {
                    scanned++;
                    // Verify the returned provider ID independently of the DB
                    // predicate; do not include unrelated provider matches.
                    if (wanted.TryGetValue(candidate.StashId, out var rank))
                        items.Add(new PopularityPreparedItem(candidate.ItemId, rank));
                }
                _logger.LogDebug("JFToStashSync: Popularity matched-item lookup returned {Count} candidates (no full-library scan).", scanned);
                progress.Report(85);
                // Keep multiple matching Jellyfin video versions; remove duplicate IDs.
                items = items.GroupBy(i => i.ItemId)
                    .Select(group => group.OrderBy(i => i.Rank).First())
                    .OrderBy(i => i.Rank).ThenBy(i => i.ItemId).ToList();
                var snapshot = new PopularityIndexSnapshot
                {
                    Version = FormatVersion,
                    UpdatedAtUtc = DateTimeOffset.UtcNow,
                    ConfigurationSignature = Signature(cfg),
                    ScannedVideos = scanned,
                    LocalStashMatches = wanted.Count,
                    Items = items
                };
                // Write fully before replacing the previous good index. Failed updates preserve it.
                var directory = Path.GetDirectoryName(_path)!;
                Directory.CreateDirectory(directory);
                var temp = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try
                {
                    await File.WriteAllTextAsync(temp, JsonConvert.SerializeObject(snapshot), ct).ConfigureAwait(false);
                    File.Move(temp, _path, true);
                }
                finally
                {
                    if (File.Exists(temp)) File.Delete(temp);
                }
                lock (_readLock) _current = snapshot;
                progress.Report(100);
                _logger.LogDebug(
                    "JFToStashSync: Popularity index refreshed: {LocalMatches} Stash scenes, {Scanned} matching Jellyfin candidates examined, {Items} matching videos. Stored at {Path}.",
                    wanted.Count, scanned, items.Count, _path);
                return items.Count;
            }
            finally
            {
                StashDbIndexRefreshGate.Shared.Release();
            }
        }
        finally
        {
            _updateLock.Release();
        }
    }
}

public sealed class PopularityIndexSnapshot
{
    public int Version { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
    public string ConfigurationSignature { get; set; } = string.Empty;
    public int ScannedVideos { get; set; }
    public int LocalStashMatches { get; set; }
    public List<PopularityPreparedItem> Items { get; set; } = new();
}

public sealed record PopularityPreparedItem(Guid ItemId, int Rank);
