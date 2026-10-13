using GeniusLyricsPlugin.Services;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace GeniusLyricsPlugin
{
    // Singletons so the provider and the backfill task share one HTTP client, one cache and one rate limiter.
    public class PluginServiceRegistrator : IPluginServiceRegistrator
    {
        public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
        {
            serviceCollection.AddSingleton<ScraperService>();
            serviceCollection.AddSingleton<LrcLibClient>();
        }
    }
}
