using MediaBrowser.Model.Plugins;

namespace GeniusLyricsPlugin.Configuration
{
    public class PluginConfiguration : BasePluginConfiguration
    {
        public string GeniusApiKey { get; set; }
        public bool EnableGenius { get; set; }
        public bool EnableFallback { get; set; }
        public bool SaveLyricsInMediaFolder { get; set; }
        public bool RemoveSectionHeaders { get; set; }
        public int BackoffDays { get; set; }
        public int RequestDelayMs { get; set; }

        public PluginConfiguration()
        {
            GeniusApiKey = string.Empty;
            EnableGenius = true;
            EnableFallback = true;
            SaveLyricsInMediaFolder = true;
            RemoveSectionHeaders = false;
            BackoffDays = 30;
            RequestDelayMs = 1500;
        }
    }
}
