using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using JFToStashSync.Services;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace JFToStashSync.Tasks;

/// <summary>Builds the local Jellyfin item index independently of web requests.</summary>
public sealed class RefreshStashDbTrendingTask : IScheduledTask, IConfigurableScheduledTask
{
    private readonly TrendingPreparedIndexService _service;
    private readonly ILogger<RefreshStashDbTrendingTask> _logger;

    public RefreshStashDbTrendingTask(TrendingPreparedIndexService service, ILogger<RefreshStashDbTrendingTask> logger)
    {
        _service = service;
        _logger = logger;
    }

    public string Name => "Refresh StashDB Trending index";
    public string Description => "Fetches StashDB Trending through the configured Stash connection and matches Stash scenes to Jellyfin video IDs. The home row reads only the saved index.";
    public string Category => "JF To Stash Sync";
    public bool IsHidden => false;
    public bool IsEnabled => true;
    public bool IsLogged => true;
    public string Key => "JFToStashSyncRefreshTrendingIndex";

    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        var count = await _service.RefreshAsync(progress, cancellationToken).ConfigureAwait(false);
        _logger.LogInformation("JFToStashSync: scheduled Trending index task completed ({Count} Jellyfin videos).", count);
    }

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() => new[]
    {
        new TaskTriggerInfo { Type = TaskTriggerInfoType.StartupTrigger },
        new TaskTriggerInfo { Type = TaskTriggerInfoType.IntervalTrigger, IntervalTicks = TimeSpan.FromHours(6).Ticks },
    };
}
