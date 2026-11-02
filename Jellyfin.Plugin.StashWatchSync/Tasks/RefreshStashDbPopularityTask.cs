using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using JFToStashSync.Services;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace JFToStashSync.Tasks;

/// <summary>Builds the local Jellyfin item index independently of web requests.</summary>
public sealed class RefreshStashDbPopularityTask : IScheduledTask, IConfigurableScheduledTask
{
    private readonly PopularityPreparedIndexService _service;
    private readonly ILogger<RefreshStashDbPopularityTask> _logger;

    public RefreshStashDbPopularityTask(PopularityPreparedIndexService service, ILogger<RefreshStashDbPopularityTask> logger)
    {
        _service = service;
        _logger = logger;
    }

    public string Name => "Refresh StashDB Popularity index";
    public string Description => "Fetches StashDB Popularity through the configured Stash connection and matches Stash scenes to Jellyfin video IDs. The home row reads only the saved index.";
    public string Category => "JF To Stash Sync";
    public bool IsHidden => false;
    public bool IsEnabled => true;
    public bool IsLogged => true;
    public string Key => "JFToStashSyncRefreshPopularityIndex";

    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        var count = await _service.RefreshAsync(progress, cancellationToken).ConfigureAwait(false);
        _logger.LogInformation("JFToStashSync: scheduled Popularity index task completed ({Count} Jellyfin videos).", count);
    }

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() => new[]
    {
        new TaskTriggerInfo { Type = TaskTriggerInfoType.StartupTrigger },
        new TaskTriggerInfo { Type = TaskTriggerInfoType.IntervalTrigger, IntervalTicks = TimeSpan.FromHours(6).Ticks },
    };
}
