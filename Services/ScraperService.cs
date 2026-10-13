using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace GeniusLyricsPlugin.Services
{
    // Registered as a singleton: every Genius request from the provider and the backfill task goes through one
    // HTTP client, one cache and one rate limiter, so Jellyfin's lyric tasks can't hammer Genius in parallel.
    public sealed class ScraperService : IDisposable
    {
        private const int MaxAttempts = 3;
        private static readonly TimeSpan CacheDuration = TimeSpan.FromHours(6);
        private static readonly TimeSpan MaxRetryWait = TimeSpan.FromMinutes(2);
        private const string NotFoundMarker = "";

        private readonly HttpClient _httpClient;
        private readonly ILogger<ScraperService> _logger;
        private readonly MemoryCache _cache = new(new MemoryCacheOptions { SizeLimit = 5000 });
        private readonly RequestThrottle _throttle = new();

        public ScraperService(ILogger<ScraperService> logger)
        {
            _logger = logger;
            var handler = new SocketsHttpHandler
            {
                AutomaticDecompression = DecompressionMethods.All,
                PooledConnectionLifetime = TimeSpan.FromMinutes(10)
            };
            _httpClient = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
            _httpClient.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
            _httpClient.DefaultRequestHeaders.Add("Accept-Language", "en-US,en;q=0.9");
        }

        public static bool IsGeniusUrl(string? url)
        {
            return Uri.TryCreate(url, UriKind.Absolute, out var uri)
                && uri.Scheme == Uri.UriSchemeHttps
                && (uri.Host == "genius.com" || uri.Host.EndsWith(".genius.com", StringComparison.OrdinalIgnoreCase));
        }

        public async Task<LyricsLookupResult> FindLyricsAsync(IReadOnlyList<string> artists, string title, CancellationToken cancellationToken)
        {
            var config = Plugin.CurrentConfiguration;
            var queryArtist = artists.Count > 0 ? SongMatcher.CleanArtist(artists[0]) : string.Empty;
            var query = $"{queryArtist} {SongMatcher.CleanTitle(title)}".Trim();
            if (query.Length == 0)
            {
                return LyricsLookupResult.NotFound;
            }

            var cacheKey = "search:" + SongMatcher.Normalize(string.Join('|', artists) + "|" + title);
            if (!_cache.TryGetValue(cacheKey, out string? url))
            {
                var hasKey = !string.IsNullOrWhiteSpace(config.GeniusApiKey);
                var searchUrl = hasKey
                    ? $"https://api.genius.com/search?q={Uri.EscapeDataString(query)}"
                    : $"https://genius.com/api/search/multi?q={Uri.EscapeDataString(query)}";

                var response = await GetAsync(searchUrl, "application/json", hasKey ? config.GeniusApiKey : null, cancellationToken).ConfigureAwait(false);
                if (response.Body == null)
                {
                    return LyricsLookupResult.Failed;
                }

                var hits = GeniusParser.ParseSearchHits(response.Body);
                if (hits == null)
                {
                    _logger.LogWarning("Genius search returned something other than JSON (Cloudflare block?). Starts with: {Start}", Truncate(response.Body));
                    return LyricsLookupResult.Failed;
                }

                var best = SongMatcher.SelectBest(hits, artists, title);
                if (best == null)
                {
                    _logger.LogDebug(
                        "No Genius match for {Artist} - {Title}; candidates: {Candidates}",
                        string.Join(", ", artists),
                        title,
                        string.Join("; ", hits.Take(5).Select(h => $"{h.ArtistNames} - {h.Title}")));
                }

                url = best?.Url ?? NotFoundMarker;
                _cache.Set(cacheKey, url, new MemoryCacheEntryOptions { Size = 1, AbsoluteExpirationRelativeToNow = CacheDuration });
            }

            return string.IsNullOrEmpty(url)
                ? LyricsLookupResult.NotFound
                : await GetLyricsByUrlAsync(url, cancellationToken).ConfigureAwait(false);
        }

        public async Task<LyricsLookupResult> GetLyricsByUrlAsync(string url, CancellationToken cancellationToken)
        {
            if (!IsGeniusUrl(url))
            {
                return LyricsLookupResult.NotFound;
            }

            var cacheKey = "lyrics:" + url;
            if (!_cache.TryGetValue(cacheKey, out LyricsLookupResult? result) || result == null)
            {
                var response = await GetAsync(url, "text/html", null, cancellationToken).ConfigureAwait(false);
                if (response.Body == null)
                {
                    return response.Status == HttpStatusCode.NotFound ? LyricsLookupResult.NotFound : LyricsLookupResult.Failed;
                }

                result = ParseSongPage(url, response.Body);
                if (result.Status == LyricsLookupStatus.Failed)
                {
                    _logger.LogWarning("{Url} doesn't look like a Genius song page (Cloudflare block or layout change?)", url);
                    return result;
                }

                _cache.Set(cacheKey, result, new MemoryCacheEntryOptions { Size = 1, AbsoluteExpirationRelativeToNow = CacheDuration });
            }

            // Applied on the way out so changing the setting takes effect without waiting for the cache to expire.
            return result.Lyrics != null && Plugin.CurrentConfiguration.RemoveSectionHeaders
                ? result with { Lyrics = GeniusParser.RemoveSectionHeaders(result.Lyrics) }
                : result;
        }

        public void Dispose()
        {
            _httpClient.Dispose();
            _cache.Dispose();
            _throttle.Dispose();
        }

        private static LyricsLookupResult ParseSongPage(string url, string html)
        {
            var lyrics = GeniusParser.ExtractLyrics(html);
            if (lyrics != null)
            {
                return new LyricsLookupResult(LyricsLookupStatus.Found, url, lyrics);
            }

            if (GeniusParser.IsMarkedInstrumental(html))
            {
                return new LyricsLookupResult(LyricsLookupStatus.Instrumental, url);
            }

            return GeniusParser.LooksLikeSongPage(html)
                ? new LyricsLookupResult(LyricsLookupStatus.NotFound, url)
                : LyricsLookupResult.Failed;
        }

        private async Task<(HttpStatusCode? Status, string? Body)> GetAsync(string url, string accept, string? bearerToken, CancellationToken cancellationToken)
        {
            HttpStatusCode? lastStatus = null;
            for (var attempt = 1; attempt <= MaxAttempts; attempt++)
            {
                await _throttle.WaitAsync(TimeSpan.FromMilliseconds(Plugin.CurrentConfiguration.RequestDelayMs), cancellationToken).ConfigureAwait(false);

                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.Accept.ParseAdd(accept);
                if (bearerToken != null)
                {
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);
                }

                HttpResponseMessage response;
                try
                {
                    response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is HttpRequestException || (ex is TaskCanceledException && !cancellationToken.IsCancellationRequested))
                {
                    _logger.LogWarning("Request to {Url} failed (attempt {Attempt}/{MaxAttempts}): {Error}", url, attempt, MaxAttempts, ex.Message);
                    _throttle.Defer(TimeSpan.FromSeconds(5 * attempt));
                    continue;
                }

                using (response)
                {
                    lastStatus = response.StatusCode;
                    if (response.IsSuccessStatusCode)
                    {
                        return (response.StatusCode, await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
                    }

                    if (response.StatusCode == HttpStatusCode.TooManyRequests || (int)response.StatusCode >= 500)
                    {
                        var wait = GetRetryDelay(response, attempt);
                        _logger.LogWarning(
                            "Genius returned {Status} for {Url}; pausing all Genius requests for {Seconds}s (attempt {Attempt}/{MaxAttempts})",
                            (int)response.StatusCode,
                            url,
                            (int)wait.TotalSeconds,
                            attempt,
                            MaxAttempts);
                        _throttle.Defer(wait);
                        continue;
                    }

                    if (response.StatusCode != HttpStatusCode.NotFound)
                    {
                        _logger.LogWarning("Genius returned {Status} for {Url}", (int)response.StatusCode, url);
                    }

                    return (response.StatusCode, null);
                }
            }

            return (lastStatus, null);
        }

        private static TimeSpan GetRetryDelay(HttpResponseMessage response, int attempt)
        {
            var retryAfter = response.Headers.RetryAfter;
            var wait = retryAfter?.Delta
                ?? (retryAfter?.Date is { } date ? date - DateTimeOffset.UtcNow : (TimeSpan?)null)
                ?? TimeSpan.FromSeconds(10 * attempt);
            if (wait < TimeSpan.FromSeconds(1))
            {
                return TimeSpan.FromSeconds(1);
            }

            return wait > MaxRetryWait ? MaxRetryWait : wait;
        }

        private static string Truncate(string value)
        {
            return value.Length <= 100 ? value : value[..100];
        }
    }
}
