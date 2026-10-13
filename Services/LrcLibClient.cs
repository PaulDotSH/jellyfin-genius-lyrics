using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace GeniusLyricsPlugin.Services
{
    // Fallback source used by the backfill task when Genius has nothing. lrclib.net is a free, open lyrics API.
    public sealed class LrcLibClient : IDisposable
    {
        private const double MaxDurationDifferenceSeconds = 3;
        private const int MaxAttempts = 3;
        private static readonly TimeSpan MinRequestSpacing = TimeSpan.FromSeconds(1);

        private readonly RequestThrottle _throttle = new();
        private readonly HttpClient _httpClient;
        private readonly ILogger<LrcLibClient> _logger;

        public LrcLibClient(ILogger<LrcLibClient> logger)
        {
            _logger = logger;
            _httpClient = new HttpClient(new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(10) })
            {
                BaseAddress = new Uri("https://lrclib.net"),
                Timeout = TimeSpan.FromSeconds(30)
            };

            // LRCLIB asks clients to identify themselves.
            _httpClient.DefaultRequestHeaders.Add("User-Agent", "GeniusLyricsPlugin (https://github.com/PaulDotSH/jellyfin-genius-lyrics)");
        }

        public async Task<LyricsLookupResult> FindLyricsAsync(IReadOnlyList<string> artists, string title, double? durationSeconds, CancellationToken cancellationToken)
        {
            if (artists.Count == 0 || string.IsNullOrWhiteSpace(title))
            {
                return LyricsLookupResult.NotFound;
            }

            var url = $"/api/search?artist_name={Uri.EscapeDataString(SongMatcher.CleanArtist(artists[0]))}&track_name={Uri.EscapeDataString(SongMatcher.CleanTitle(title))}";
            var records = await SearchAsync(url, cancellationToken).ConfigureAwait(false);
            if (records == null)
            {
                _logger.LogWarning("LRCLIB lookup failed for {Artist} - {Title}", artists[0], title);
                return LyricsLookupResult.Failed;
            }

            var match = records
                .Where(r => durationSeconds == null || r.Duration == null || Math.Abs(r.Duration.Value - durationSeconds.Value) <= MaxDurationDifferenceSeconds)
                .Where(r => SongMatcher.ArtistScore(artists, r.ArtistName ?? string.Empty) >= 0.6 && SongMatcher.TitleScore(title, r.TrackName ?? string.Empty) >= 0.5)
                .OrderByDescending(r => !string.IsNullOrWhiteSpace(r.SyncedLyrics))
                .ThenByDescending(r => !string.IsNullOrWhiteSpace(r.PlainLyrics))
                .FirstOrDefault();

            if (match == null)
            {
                return LyricsLookupResult.NotFound;
            }

            if (match.Instrumental)
            {
                return new LyricsLookupResult(LyricsLookupStatus.Instrumental);
            }

            if (!string.IsNullOrWhiteSpace(match.SyncedLyrics))
            {
                return new LyricsLookupResult(LyricsLookupStatus.Found, null, match.SyncedLyrics, "lrc");
            }

            return string.IsNullOrWhiteSpace(match.PlainLyrics)
                ? LyricsLookupResult.NotFound
                : new LyricsLookupResult(LyricsLookupStatus.Found, null, match.PlainLyrics, "txt");
        }

        public void Dispose()
        {
            _httpClient.Dispose();
            _throttle.Dispose();
        }

        // LRCLIB answers bursts with 503, so requests are spaced out and retried like Genius ones.
        private async Task<List<LrcLibRecord>?> SearchAsync(string url, CancellationToken cancellationToken)
        {
            for (var attempt = 1; attempt <= MaxAttempts; attempt++)
            {
                await _throttle.WaitAsync(MinRequestSpacing, cancellationToken).ConfigureAwait(false);
                try
                {
                    using var response = await _httpClient.GetAsync(url, cancellationToken).ConfigureAwait(false);
                    if (response.IsSuccessStatusCode)
                    {
                        return await response.Content.ReadFromJsonAsync<List<LrcLibRecord>>(cancellationToken).ConfigureAwait(false) ?? new List<LrcLibRecord>();
                    }

                    if (response.StatusCode != HttpStatusCode.TooManyRequests && (int)response.StatusCode < 500)
                    {
                        _logger.LogWarning("LRCLIB returned {Status} for {Url}", (int)response.StatusCode, url);
                        return null;
                    }

                    _logger.LogDebug("LRCLIB returned {Status}; retrying (attempt {Attempt}/{MaxAttempts})", (int)response.StatusCode, attempt, MaxAttempts);
                }
                catch (Exception ex) when (ex is HttpRequestException || ex is JsonException || (ex is TaskCanceledException && !cancellationToken.IsCancellationRequested))
                {
                    _logger.LogDebug("LRCLIB request failed (attempt {Attempt}/{MaxAttempts}): {Error}", attempt, MaxAttempts, ex.Message);
                }

                _throttle.Defer(TimeSpan.FromSeconds(5 * attempt));
            }

            return null;
        }

        private sealed class LrcLibRecord
        {
            [JsonPropertyName("trackName")]
            public string? TrackName { get; set; }

            [JsonPropertyName("artistName")]
            public string? ArtistName { get; set; }

            [JsonPropertyName("duration")]
            public double? Duration { get; set; }

            [JsonPropertyName("instrumental")]
            public bool Instrumental { get; set; }

            [JsonPropertyName("plainLyrics")]
            public string? PlainLyrics { get; set; }

            [JsonPropertyName("syncedLyrics")]
            public string? SyncedLyrics { get; set; }
        }
    }
}
