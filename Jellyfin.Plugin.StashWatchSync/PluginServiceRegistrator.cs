using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;
using StashWatchSync.Services;
using StashWatchSync.Sync;

namespace StashWatchSync;

/// <summary>
/// Registers services with Jellyfin's dependency injection container.
/// </summary>
public sealed class PluginServiceRegistrator : IPluginServiceRegistrator
{
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        // GraphQL client to talk to Stash.
        serviceCollection.AddSingleton<StashClient>();

        // Direct Jellyfin-link synchronization used by the scheduled tasks and manual API.
        serviceCollection.AddSingleton<JellyfinUrlSyncService>();
        serviceCollection.AddSingleton<JellyfinPerformerUrlSyncService>();

        // Background sync service that hooks UserDataSaved.
        serviceCollection.AddHostedService<UserDataSyncHostedService>();
    }
}
