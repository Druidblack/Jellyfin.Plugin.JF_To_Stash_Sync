using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using JFToStashSync.Services;
using MediaBrowser.Model.Tasks;

namespace JFToStashSync.Tasks;

/// <summary>Updates Babepedia Top 100 cache and performs one Stash → Jellyfin matching pass.</summary>
public sealed class RefreshBabepediaTopTask : IScheduledTask, IConfigurableScheduledTask
{
    private readonly BabepediaTopPreparedIndexService _index;
    public RefreshBabepediaTopTask(BabepediaTopPreparedIndexService index) => _index = index;
    public string Name => "Refresh Babepedia Top 100 indexes";
    public string Description => "Download enabled Babepedia Top 100 lists when stale and prepare actor rows using Stash external links.";
    public string Category => "JF To Stash Sync";
    public bool IsHidden => false;
    public bool IsEnabled => true;
    public bool IsLogged => true;
    public string Key => "JFToStashSyncRefreshBabepediaTop100";
    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        await _index.RefreshAsync(progress, cancellationToken).ConfigureAwait(false);
    }
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() => new[]
    {
        new TaskTriggerInfo { Type = TaskTriggerInfoType.IntervalTrigger,
            IntervalTicks = TimeSpan.FromHours(6).Ticks }
    };
}
