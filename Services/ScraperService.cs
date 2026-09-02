using System;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using HtmlAgilityPack;
using System.Web;
using Microsoft.Extensions.Logging;

namespace GeniusLyricsPlugin.Services
{
    public class ScraperService
    {
        private readonly HttpClient _httpClient;
        private readonly ILogger<ScraperService> _logger;

        public ScraperService(ILogger<ScraperService> logger)
        {
            _logger = logger;
            var handler = new HttpClientHandler
            {
                AutomaticDecompression = System.Net.DecompressionMethods.GZip | System.Net.DecompressionMethods.Deflate
            };
            _httpClient = new HttpClient(handler);
            _httpClient.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
            _httpClient.DefaultRequestHeaders.Add("Accept", "application/json, text/plain, */*");
            _httpClient.DefaultRequestHeaders.Add("Accept-Language", "en-US,en;q=0.9");
        }

        public async Task<string> SearchSongUrlAsync(string artist, string title, string apiKey, CancellationToken cancellationToken)
        {
            var cleanArtist = artist.Replace(" x ", " ").Replace(" & ", " ").Replace(" feat. ", " ").Replace(" ft. ", " ").Replace(" + ", " ");
            var query = HttpUtility.UrlEncode($"{cleanArtist} {title}");
            var url = $"https://genius.com/api/search/multi?q={query}";
            
            var request = new HttpRequestMessage(HttpMethod.Get, url);
            if (!string.IsNullOrWhiteSpace(apiKey))
            {
                request.Headers.Add("Authorization", $"Bearer {apiKey}");
            }

            int attempts = 0;
            HttpResponseMessage response = null;
            
            while (attempts < 3)
            {
                attempts++;
                response = await _httpClient.SendAsync(request, cancellationToken);
                
                if (response.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
                {
                    _logger.LogWarning($"Rate limited searching for '{title}' by '{artist}', backing off for {attempts * 10}s...");
                    await Task.Delay(TimeSpan.FromSeconds(attempts * 10), cancellationToken);
                    
                    // Re-create request because it cannot be sent twice
                    request = new HttpRequestMessage(HttpMethod.Get, url);
                    if (!string.IsNullOrWhiteSpace(apiKey))
                    {
                        request.Headers.Add("Authorization", $"Bearer {apiKey}");
                    }
                    continue;
                }
                break;
            }

            if (response == null || !response.IsSuccessStatusCode)
            {
                return null;
            }

            var content = await response.Content.ReadAsStringAsync();
            JsonDocument doc = null;
            try 
            {
                doc = JsonDocument.Parse(content);
            }
            catch (System.Text.Json.JsonException)
            {
                _logger.LogWarning($"Genius returned invalid JSON (Cloudflare block?). Content starts with: {content.Substring(0, Math.Min(content.Length, 100))}");
                return null;
            }

            using (doc)
            {
                var root = doc.RootElement;
                if (root.TryGetProperty("response", out var responseObj) && responseObj.TryGetProperty("sections", out var sections))
                {
                    foreach (var section in sections.EnumerateArray())
                    {
                        if (section.TryGetProperty("hits", out var hits))
                        {
                            foreach (var hit in hits.EnumerateArray())
                            {
                                if (hit.TryGetProperty("result", out var result))
                                {
                                    if (result.TryGetProperty("_type", out var typeNode) && typeNode.GetString() == "song")
                                    {
                                        if (result.TryGetProperty("url", out var songUrl))
                                        {
                                            var hitArtist = result.TryGetProperty("primary_artist", out var primaryArtist) && primaryArtist.TryGetProperty("name", out var primaryArtistName) 
                                                ? primaryArtistName.GetString() 
                                                : string.Empty;
                                            
                                            var hitArtistNames = result.TryGetProperty("artist_names", out var an) ? an.GetString() : hitArtist;
                                            
                                            bool isArtistMatch = false;
                                            if (!string.IsNullOrWhiteSpace(hitArtistNames) && !string.IsNullOrWhiteSpace(artist))
                                            {
                                                var requestedArtistLower = artist.ToLowerInvariant();
                                                var hitArtistLower = hitArtistNames.ToLowerInvariant();
                                                
                                                if (hitArtistLower.Contains(requestedArtistLower) || requestedArtistLower.Contains(hitArtistLower))
                                                {
                                                    isArtistMatch = true;
                                                }
                                                else
                                                {
                                                    var artistWords = requestedArtistLower.Split(new[] { ' ', '-', ',', '&' }, StringSplitOptions.RemoveEmptyEntries);
                                                    foreach (var word in artistWords)
                                                    {
                                                        if (word.Length > 2 && hitArtistLower.Contains(word))
                                                        {
                                                            isArtistMatch = true;
                                                            break;
                                                        }
                                                    }
                                                }
                                            }

                                            if (isArtistMatch)
                                            {
                                                return songUrl.GetString();
                                            }
                                            else
                                            {
                                                _logger.LogInformation($"Skipped '{songUrl.GetString()}' because artist '{hitArtistNames}' did not match '{artist}'");
                                            }
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
            }

            return null;
        }

        public async Task<string> ScrapeLyricsAsync(string url, CancellationToken cancellationToken)
        {
            int attempts = 0;
            HttpResponseMessage response = null;

            while (attempts < 3)
            {
                attempts++;
                var request = new HttpRequestMessage(HttpMethod.Get, url);
                response = await _httpClient.SendAsync(request, cancellationToken);

                if (response.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
                {
                    _logger.LogWarning($"Rate limited scraping lyrics from {url}, backing off for {attempts * 10}s...");
                    await Task.Delay(TimeSpan.FromSeconds(attempts * 10), cancellationToken);
                    continue;
                }
                break;
            }

            if (response == null || !response.IsSuccessStatusCode)
                return null;

            var html = await response.Content.ReadAsStringAsync();
            var htmlDoc = new HtmlDocument();
            htmlDoc.LoadHtml(html);

            // Genius lyrics are typically inside divs with data-lyrics-container="true"
            var lyricsNodes = htmlDoc.DocumentNode.SelectNodes("//div[@data-lyrics-container='true']");
            if (lyricsNodes == null || lyricsNodes.Count == 0)
            {
                // Fallback for older layout
                var oldLyricsNode = htmlDoc.DocumentNode.SelectSingleNode("//div[@class='lyrics']");
                if (oldLyricsNode != null)
                {
                    return CleanHtmlToText(oldLyricsNode.InnerHtml);
                }
                return null;
            }

            var fullLyrics = string.Empty;
            foreach (var node in lyricsNodes)
            {
                fullLyrics += CleanHtmlToText(node.InnerHtml) + "\n";
            }

            return fullLyrics.Trim();
        }

        private string CleanHtmlToText(string html)
        {
            if (string.IsNullOrWhiteSpace(html)) return string.Empty;
            
            // Replace <br> with newlines
            var text = html.Replace("<br>", "\n").Replace("<br/>", "\n").Replace("<br />", "\n");
            
            // Strip other HTML tags
            var htmlDoc = new HtmlDocument();
            htmlDoc.LoadHtml(text);
            text = HttpUtility.HtmlDecode(htmlDoc.DocumentNode.InnerText);
            
            // Remove Genius header metadata like "3 ContributorsSong Title Lyrics"
            text = System.Text.RegularExpressions.Regex.Replace(text, @"^.*?Lyrics\s*", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            
            return text.Trim();
        }
    }
}
