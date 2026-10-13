using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
        private static readonly string[] LyricFileExtensions = { ".lrc", ".elrc", ".txt" };

        private readonly ILogger<GeniusLyricsProvider> _logger;
        private readonly ScraperService _scraperService;

        public GeniusLyricsProvider(ILogger<GeniusLyricsProvider> logger, ScraperService scraperService)
        {
            _logger = logger;
            _scraperService = scraperService;
        }

        public string Name => "Genius Lyrics Provider";

        public async Task<IEnumerable<RemoteLyricInfo>> SearchAsync(LyricSearchRequest request, CancellationToken cancellationToken)
        {
            var config = Plugin.CurrentConfiguration;
            if (!config.EnableGenius)
            {
                return Array.Empty<RemoteLyricInfo>();
            }

            // Genius only has plain lyrics, so it can never "upgrade" a track that already has a lyric file.
            // Skipping these keeps background tasks that re-search the whole library from hammering Genius.
            if (request.IsAutomated && HasLyricFile(request.MediaPath))
            {
                _logger.LogDebug("Skipping automated Genius search for {Path}: a lyric file already exists", request.MediaPath);
                return Array.Empty<RemoteLyricInfo>();
            }

            var artists = (request.ArtistNames ?? Array.Empty<string>())
                .Concat(request.AlbumArtistsNames ?? Array.Empty<string>())
                .Where(a => !string.IsNullOrWhiteSpace(a))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            var title = request.SongName ?? string.Empty;

            _logger.LogDebug("Searching Genius for {Artist} - {Title}", string.Join(", ", artists), title);

            // Jellyfin 10.11 parses search results immediately, so the lyrics must be included here, not fetched later.
            var result = await _scraperService.FindLyricsAsync(artists, title, cancellationToken).ConfigureAwait(false);
            if (result.Status != LyricsLookupStatus.Found || result.Url == null || result.Lyrics == null)
            {
                return Array.Empty<RemoteLyricInfo>();
            }

            return new[]
            {
                new RemoteLyricInfo
                {
                    Id = result.Url,
                    ProviderName = Name,
                    Metadata = new LyricMetadata
                    {
                        Artist = artists.FirstOrDefault(),
                        Album = request.AlbumName,
                        Title = title,
                        IsSynced = false
                    },
                    Lyrics = ToResponse(result.Lyrics)
                }
            };
        }

        public async Task<LyricResponse?> GetLyricsAsync(string id, CancellationToken cancellationToken)
        {
            // The id reaches us from API clients, so only ever fetch Genius pages with it.
            if (!ScraperService.IsGeniusUrl(id))
            {
                return null;
            }

            var result = await _scraperService.GetLyricsByUrlAsync(id, cancellationToken).ConfigureAwait(false);
            return result.Status == LyricsLookupStatus.Found && result.Lyrics != null ? ToResponse(result.Lyrics) : null;
        }

        private static LyricResponse ToResponse(string lyrics)
        {
            return new LyricResponse
            {
                Stream = new MemoryStream(Encoding.UTF8.GetBytes(lyrics)),
                Format = "txt"
            };
        }

        private static bool HasLyricFile(string? mediaPath)
        {
            return !string.IsNullOrEmpty(mediaPath)
                && LyricFileExtensions.Any(extension => File.Exists(Path.ChangeExtension(mediaPath, extension)));
        }
    }
}
