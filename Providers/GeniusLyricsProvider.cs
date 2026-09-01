using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using GeniusLyricsPlugin.Services;
using MediaBrowser.Controller.Lyrics;
using MediaBrowser.Model.Lyrics;
using MediaBrowser.Model.Providers;
using Microsoft.Extensions.Logging;

namespace GeniusLyricsPlugin.Providers
{
    public class GeniusLyricsProvider : ILyricProvider
    {
        private readonly ILogger<GeniusLyricsProvider> _logger;
        private readonly ScraperService _scraperService;

        public GeniusLyricsProvider(ILogger<GeniusLyricsProvider> logger)
        {
            _logger = logger;
            _scraperService = new ScraperService(logger as ILogger<ScraperService>);
        }

        public string Name => "Genius Lyrics Provider";

        public async Task<IEnumerable<RemoteLyricInfo>> SearchAsync(LyricSearchRequest request, CancellationToken cancellationToken)
        {
            var config = Plugin.Instance.Configuration;
            if (!config.EnableGenius)
            {
                return Array.Empty<RemoteLyricInfo>();
            }

            var artist = request.ArtistNames?.Count > 0 ? request.ArtistNames[0] : ""; 
            var title = request.SongName ?? "";
            
            _logger.LogInformation($"Searching Genius for lyrics: {title}");

            var url = await _scraperService.SearchSongUrlAsync(artist, title, config.GeniusApiKey, cancellationToken);
            if (string.IsNullOrEmpty(url))
            {
                return Array.Empty<RemoteLyricInfo>();
            }

            return new[]
            {
                new RemoteLyricInfo
                {
                    Id = url,
                    ProviderName = Name,
                    Metadata = default!,
                    Lyrics = default!
                }
            };
        }

        public async Task<LyricResponse?> GetLyricsAsync(string id, CancellationToken cancellationToken)
        {
            var url = id;
            if (string.IsNullOrWhiteSpace(url) || !url.StartsWith("http"))
            {
                return null;
            }

            _logger.LogInformation($"Scraping lyrics from: {url}");
            var lyricsText = await _scraperService.ScrapeLyricsAsync(url, cancellationToken);
            
            if (string.IsNullOrWhiteSpace(lyricsText))
            {
                return null;
            }

            var stream = new MemoryStream(Encoding.UTF8.GetBytes(lyricsText));
            return new LyricResponse
            {
                Stream = stream,
                Format = "txt"
            };
        }
    }
}
