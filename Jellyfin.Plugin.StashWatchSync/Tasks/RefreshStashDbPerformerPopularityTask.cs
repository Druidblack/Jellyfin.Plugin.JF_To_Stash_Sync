using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using JFToStashSync.Services;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace JFToStashSync.Tasks;

/// <summary>Prepares all selected performer-gender rows in one scheduled operation.</summary>
public sealed class RefreshStashDbPerformerPopularityTask : IScheduledTask, IConfigurableScheduledTask
{
    private readonly PerformerPopularityPreparedIndexService _index;
    private readonly ILogger<RefreshStashDbPerformerPopularityTask> _logger;

    public RefreshStashDbPerformerPopularityTask(
        PerformerPopularityPreparedIndexService index,
        ILogger<RefreshStashDbPerformerPopularityTask> logger)
    {
        _index = index;
        _logger = logger;
    }

    public string Name => "Refresh StashDB performer Popularity index";
    public string Description => "Prepare Jellyfin person IDs in separate StashDB Popularity rankings for each selected gender.";
    public string Category => "JF To Stash Sync";
    public bool IsHidden => false;
    public bool IsEnabled => true;
    public bool IsLogged => true;
    public string Key => "JFToStashSyncRefreshPerformerPopularityIndex";

    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        var count = await _index.RefreshAsync(progress, cancellationToken).ConfigureAwait(false);
        _logger.LogInformation("JFToStashSync: performer Popularity scheduled task completed ({Count} Jellyfin people).", count);
    }

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() => new[]
    {
        new TaskTriggerInfo { Type = TaskTriggerInfoType.StartupTrigger },
        new TaskTriggerInfo { Type = TaskTriggerInfoType.IntervalTrigger, IntervalTicks = TimeSpan.FromHours(6).Ticks },
    };
}
