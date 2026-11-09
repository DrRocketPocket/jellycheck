#if JELLYFIN_10_11
using Jellyfin.Plugin.Jellycheck.ScheduledTasks;
using Jellyfin.Plugin.Jellycheck.Services;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Model.Tasks;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.Jellycheck
{
    public class JellycheckServiceRegistrator : IPluginServiceRegistrator
    {
        public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
        {
            // Core startup service (injects client.js into index.html on disk as fallback)
            serviceCollection.AddHostedService<PluginEntryPoint>();

            // In-memory script injection middleware (works even on read-only filesystems)
            serviceCollection.AddTransient<IStartupFilter, JellycheckStartupFilter>();

            // Auto-delete services
            serviceCollection.AddSingleton<WatchedAnalysisService>();
            serviceCollection.AddSingleton<WatchedStateStore>();
            serviceCollection.AddSingleton<SonarrService>();
            serviceCollection.AddSingleton<RadarrService>();
            serviceCollection.AddSingleton<OverseerrService>();
            serviceCollection.AddSingleton<StorageService>();

            // Scheduled task — Jellyfin discovers IScheduledTask via DI
            serviceCollection.AddSingleton<IScheduledTask, AutoDeleteWatchedTask>();
        }
    }
}
#endif
