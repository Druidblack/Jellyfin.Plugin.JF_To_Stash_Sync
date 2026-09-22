using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;
using StashWatchSync.Services;

namespace StashWatchSync.Tasks;

/// <summary>
/// Periodically synchronizes direct Jellyfin item URLs into Stash scene URLs.
/// The schedule can be changed from Jellyfin Dashboard -> Scheduled Tasks.
/// </summary>
public sealed class SyncJellyfinUrlsTask : IScheduledTask, IConfigurableScheduledTask
{
    private readonly JellyfinUrlSyncService _syncService;
    private readonly ILogger<SyncJellyfinUrlsTask> _logger;

    public SyncJellyfinUrlsTask(
        JellyfinUrlSyncService syncService,
        ILogger<SyncJellyfinUrlsTask> logger)
    {
        _syncService = syncService;
        _logger = logger;
    }

    public string Name => "Sync Jellyfin links to Stash";

    public string Description =>
        "Writes a direct Jellyfin details URL to each matching Stash scene and replaces stale links from the same Jellyfin server.";

    public string Category => "JF To Stash Sync";

    public bool IsHidden => false;

    public bool IsEnabled => true;

    public bool IsLogged => true;

    public string Key => "JFToStashSyncJellyfinUrls";

    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        var result = await _syncService.SyncAllAsync(progress, cancellationToken).ConfigureAwait(false);
        _logger.LogInformation("StashWatchSync: scheduled Jellyfin URL sync: {Message}", result.Message);
        progress.Report(100);
    }

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
        => new[]
        {
            new TaskTriggerInfo
            {
                Type = TaskTriggerInfoType.IntervalTrigger,
                IntervalTicks = TimeSpan.FromHours(24).Ticks,
            },
        };
}
