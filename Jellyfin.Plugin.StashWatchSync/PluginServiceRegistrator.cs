using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.DependencyInjection;
using JFToStashSync.Services;
using JFToStashSync.Providers;
using JFToStashSync.Sync;
using JFToStashSync.Web;

namespace JFToStashSync;

/// <summary>
/// Registers services with Jellyfin's dependency injection container.
/// </summary>
public sealed class PluginServiceRegistrator : IPluginServiceRegistrator
{
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        // GraphQL client to talk to Stash.
        serviceCollection.AddSingleton<StashClient>();
        serviceCollection.AddSingleton<StashCollaborationService>();

        // Standalone StashDB Trending service with its own non-redirecting HTTP client.
        serviceCollection.AddSingleton<StashDbTrendingService>();

        // Persistent precomputed Trending index consumed by both scheduled task and HTTP API.
        serviceCollection.AddSingleton<TrendingPreparedIndexService>();
        serviceCollection.AddSingleton<PopularityPreparedIndexService>();
        serviceCollection.AddSingleton<PerformerPopularityPreparedIndexService>();
        serviceCollection.AddSingleton<BabepediaActorStatsService>();
        serviceCollection.AddSingleton<PornhubActorArtworkService>();
        serviceCollection.AddSingleton<PornhubActorStatsService>();
        serviceCollection.AddSingleton<PornhubScheduledStateService>();
        serviceCollection.AddSingleton<FeaturedBannerCandidateService>();
        serviceCollection.AddSingleton<BabepediaTopPreparedIndexService>();

        // Direct Jellyfin-link synchronization used by the scheduled tasks and manual API.
        serviceCollection.AddSingleton<JellyfinUrlSyncService>();
        serviceCollection.AddSingleton<ManualSceneLinkJobService>();
        serviceCollection.AddSingleton<JellyfinPerformerUrlSyncService>();
        serviceCollection.AddSingleton<OCounterSyncService>();

        // Jellyfin 12 remote similar-items provider for Movies. It is selectable per library
        // as "Stash Similar Scenes" and resolves recommendations by Stash provider IDs.
        serviceCollection.AddSingleton<ISimilarItemsProvider, StashSimilarScenesProvider>();

        // Inject the O+ button into Jellyfin Web via the File Transformation plugin.
        serviceCollection.AddHostedService<FileTransformationRegistrationService>();

        // Background sync service that hooks UserDataSaved.
        serviceCollection.AddHostedService<UserDataSyncHostedService>();
    }
}
