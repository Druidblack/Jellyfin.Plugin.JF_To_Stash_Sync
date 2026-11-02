using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using JFToStashSync.Configuration;
using Jellyfin.Data.Enums;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;

namespace JFToStashSync.Services;

/// <summary>
/// Disk-persisted StashDB performer popularity index. One scheduled traversal of
/// Jellyfin Person records; no full-library work in a browser request.
/// </summary>
public sealed class PerformerPopularityPreparedIndexService
{
    private const int FormatVersion = 1;
    private readonly ILibraryManager _library;
    private readonly StashDbTrendingService _rankings;
    private readonly ILogger<PerformerPopularityPreparedIndexService> _logger;
    private readonly string _path;
    private readonly SemaphoreSlim _updateLock = new(1, 1);
    private readonly object _readLock = new();
    private PerformerPopularitySnapshot? _current;

    public static IReadOnlyList<string> SelectedGenders(PluginConfiguration cfg)
    {
        var genders = new List<string>(6);
        if (cfg.StashDbPerformerGenderFemale) genders.Add("FEMALE");
        if (cfg.StashDbPerformerGenderIntersex) genders.Add("INTERSEX");
        if (cfg.StashDbPerformerGenderMale) genders.Add("MALE");
        if (cfg.StashDbPerformerGenderNonBinary) genders.Add("NON_BINARY");
        if (cfg.StashDbPerformerGenderTransgenderFemale) genders.Add("TRANSGENDER_FEMALE");
        if (cfg.StashDbPerformerGenderTransgenderMale) genders.Add("TRANSGENDER_MALE");
        return genders;
    }

    public PerformerPopularityPreparedIndexService(
        ILibraryManager library,
        StashDbTrendingService rankings,
        IApplicationPaths paths,
        ILogger<PerformerPopularityPreparedIndexService> logger)
    {
        _library = library;
        _rankings = rankings;
        _logger = logger;
        _path = Path.Combine(paths.DataPath, "JFToStashSync", "performer-popularity-index.json");
        ReadSnapshot();
    }

    private void ReadSnapshot()
    {
        try
        {
            if (!File.Exists(_path)) return;
            var contents = File.ReadAllText(_path);
            var snapshot = JsonConvert.DeserializeObject<PerformerPopularitySnapshot>(contents);
            if (snapshot?.Version == FormatVersion && snapshot.Items is not null && snapshot.Items.Count <= 100000)
            {
                lock (_readLock) _current = snapshot;
                _logger.LogDebug("JFToStashSync: performer Popularity index restored: {Count} people, updated {Updated} UTC.",
                    snapshot.Items.Count, snapshot.UpdatedAtUtc);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "JFToStashSync: cannot read performer Popularity index; run the scheduled task.");
        }
    }

    public PerformerPopularitySnapshot? GetSnapshot()
    {
        lock (_readLock) return _current;
    }

    public bool IsCurrent(PerformerPopularitySnapshot snapshot, PluginConfiguration cfg)
        => snapshot.Version == FormatVersion && snapshot.ConfigurationSignature == Signature(cfg);

    private static string Signature(PluginConfiguration cfg)
    {
        var text = (cfg.StashEndpoint ?? "").Trim().TrimEnd('/').ToLowerInvariant()
            + "|" + Math.Clamp(cfg.StashDbPerformerPopularityPages, 1, 10)
            + "|" + string.Join(",", SelectedGenders(cfg));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    }

    public async Task<int> RefreshAsync(IProgress<double> progress, CancellationToken ct)
    {
        var cfg = Plugin.Instance?.Configuration;
        if (cfg is null || !cfg.Enabled || !cfg.EnableStashDbPerformerPopularityRows)
        {
            _logger.LogDebug("JFToStashSync: performer Popularity refresh skipped (disabled).");
            progress.Report(100);
            return 0;
        }
        var genders = SelectedGenders(cfg);
        if (genders.Count == 0)
        {
            _logger.LogDebug("JFToStashSync: performer Popularity refresh skipped (no genders selected).");
            progress.Report(100);
            return 0;
        }

        await _updateLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Shared with the scene rankings: avoid three simultaneous Jellyfin scans.
            await StashDbIndexRefreshGate.Shared.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                _logger.LogDebug("JFToStashSync: performer Popularity refresh started. Genders={Genders}, pages={Pages}.",
                    string.Join(",", genders), cfg.StashDbPerformerPopularityPages);
                progress.Report(5);
                var remote = await _rankings.GetPopularPerformersAsync(
                    genders, cfg.StashDbPerformerPopularityPages, ct).ConfigureAwait(false);
                progress.Report(60);
                var wanted = remote.GroupBy(row => row.StashPerformerId, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(group => group.Key, group => group.OrderBy(row => row.Rank).First(),
                        StringComparer.OrdinalIgnoreCase);
                var matched = new List<PerformerPopularityPreparedItem>();
                var scanned = 0;
                // Filter Stash provider IDs in Jellyfin's database: never
                // hydrate thousands of unrelated Person records.
                foreach (var candidate in StashProviderIdBatchLookup.FindPeople(
                    _library, wanted.Keys, ct))
                {
                    scanned++;
                    if (wanted.TryGetValue(candidate.StashId, out var rank))
                        matched.Add(new PerformerPopularityPreparedItem(candidate.ItemId, rank.Gender, rank.Rank));
                }
                _logger.LogDebug("JFToStashSync: performer Popularity provider-ID lookup returned {Count} candidates (no full Person scan).", scanned);
                progress.Report(90);
                var rows = matched.GroupBy(row => row.PersonId)
                    .Select(group => group.OrderBy(row => row.Rank).First())
                    .OrderBy(row => row.Gender, StringComparer.Ordinal).ThenBy(row => row.Rank)
                    .ThenBy(row => row.PersonId).ToList();
                var snapshot = new PerformerPopularitySnapshot
                {
                    Version = FormatVersion,
                    ConfigurationSignature = Signature(cfg),
                    UpdatedAtUtc = DateTimeOffset.UtcNow,
                    ScannedPeople = scanned,
                    LocalStashMatches = wanted.Count,
                    Items = rows
                };
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
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
                    "JFToStashSync: performer Popularity saved. Local Stash matches={Matches}, matching Jellyfin people examined={Scanned}, matched={Items}.",
                    wanted.Count, scanned, rows.Count);
                return rows.Count;
            }
            finally { StashDbIndexRefreshGate.Shared.Release(); }
        }
        finally { _updateLock.Release(); }
    }
}

public sealed class PerformerPopularitySnapshot
{
    public int Version { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
    public string ConfigurationSignature { get; set; } = string.Empty;
    public int ScannedPeople { get; set; }
    public int LocalStashMatches { get; set; }
    public List<PerformerPopularityPreparedItem> Items { get; set; } = new();
}

public sealed record PerformerPopularityPreparedItem(Guid PersonId, string Gender, int Rank);
