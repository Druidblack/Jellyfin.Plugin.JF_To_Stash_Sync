using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;
using StashWatchSync.Services;

namespace StashWatchSync.Tasks;

/// <summary>
/// Periodically synchronizes direct Jellyfin Person URLs into Stash performer URLs.
/// </summary>
public sealed class SyncJellyfinPerformerUrlsTask : IScheduledTask, IConfigurableScheduledTask
{
    private readonly JellyfinPerformerUrlSyncService _syncService;
    private readonly ILogger<SyncJellyfinPerformerUrlsTask> _logger;

    public SyncJellyfinPerformerUrlsTask(
        JellyfinPerformerUrlSyncService syncService,
        ILogger<SyncJellyfinPerformerUrlsTask> logger)
    {
        _syncService = syncService;
        _logger = logger;
    }

    public string Name => "Sync Jellyfin performer links to Stash";

    public string Description =>
        "Writes a direct Jellyfin Person details URL to each matching Stash performer and replaces stale links from the same Jellyfin server.";

    public string Category => "JF To Stash Sync";

    public bool IsHidden => false;

    public bool IsEnabled => true;

    public bool IsLogged => true;

    public string Key => "JFToStashSyncJellyfinPerformerUrls";

    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        var result = await _syncService.SyncAllAsync(progress, cancellationToken).ConfigureAwait(false);
        _logger.LogInformation(
            "StashWatchSync: scheduled Jellyfin performer URL sync: {Message}",
            result.Message);
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
