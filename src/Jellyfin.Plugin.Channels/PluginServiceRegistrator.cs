using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.Channels;

public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddSingleton<BlockResolver>();

        // Singleton because the snapshot cache is the point: a per-request
        // instance would rebuild every running order on every tune-in.
        serviceCollection.AddSingleton<ChannelService>();

        serviceCollection.AddHostedService<ScheduleInvalidationListener>();
    }
}
