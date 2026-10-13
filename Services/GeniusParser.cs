using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using HtmlAgilityPack;

namespace GeniusLyricsPlugin.Services
{
    public sealed record GeniusSongHit(string Url, string Title, string ArtistNames);

    // Pure parsing of Genius responses, kept free of I/O so it can be unit tested.
    public static class GeniusParser
    {
        // Genius embeds tracking data like {"value":true,"key":"is_instrumental"}, sometimes with escaped quotes.
        private static readonly Regex InstrumentalFlag = new(@"\{\\?""value\\?"":true,\\?""key\\?"":\\?""is_instrumental\\?""\}", RegexOptions.Compiled);
        private static readonly Regex SectionHeaderLine = new(@"^\s*\[[^\]]*\]\s*$", RegexOptions.Compiled);
        private static readonly Regex ExtraBlankLines = new(@"\n{3,}", RegexOptions.Compiled);

        // Accepts both the internal web API (response.sections[].hits) and the public API (response.hits).
        // Returns null when the body isn't JSON, which usually means a Cloudflare challenge page.
        public static IReadOnlyList<GeniusSongHit>? ParseSearchHits(string json)
        {
            JsonDocument doc;
            try
            {
                doc = JsonDocument.Parse(json);
            }
            catch (JsonException)
            {
                return null;
            }

            using (doc)
            {
                var hits = new List<GeniusSongHit>();
                if (doc.RootElement.ValueKind != JsonValueKind.Object || !doc.RootElement.TryGetProperty("response", out var response))
                {
                    return hits;
                }

                if (response.TryGetProperty("hits", out var topHits))
                {
                    AddSongHits(topHits, hits);
                }

                if (response.TryGetProperty("sections", out var sections) && sections.ValueKind == JsonValueKind.Array)
                {
                    foreach (var section in sections.EnumerateArray())
                    {
                        if (section.ValueKind == JsonValueKind.Object && section.TryGetProperty("hits", out var sectionHits))
                        {
                            AddSongHits(sectionHits, hits);
                        }
                    }
                }

                return hits;
            }
        }

        public static string? ExtractLyrics(string html)
        {
            var doc = new HtmlDocument();
            doc.LoadHtml(html);

            var containers = doc.DocumentNode.SelectNodes("//div[@data-lyrics-container='true']")
                ?? doc.DocumentNode.SelectNodes("//div[@class='lyrics']");
            if (containers == null)
            {
                return null;
            }

            var sb = new StringBuilder();
            foreach (var container in containers)
            {
                // The header (contributors, translations, the About blurb) and inline ads are marked as excluded from selection.
                var excluded = container.SelectNodes(".//*[@data-exclude-from-selection='true']");
                if (excluded != null)
                {
                    foreach (var node in excluded.ToList())
                    {
                        node.Remove();
                    }
                }

                AppendText(container, sb);
                sb.Append('\n');
            }

            var lines = sb.ToString().Replace("\r", string.Empty).Split('\n').Select(l => l.TrimEnd());
            var text = ExtraBlankLines.Replace(string.Join('\n', lines), "\n\n").Trim();
            return text.Length == 0 ? null : text;
        }

        public static bool IsMarkedInstrumental(string html)
        {
            return InstrumentalFlag.IsMatch(html) || html.Contains("This song is an instrumental", StringComparison.OrdinalIgnoreCase);
        }

        // A lyrics root without lyric containers is a real "no lyrics yet" page; without it, the page is a block or a layout change.
        public static bool LooksLikeSongPage(string html)
        {
            return html.Contains("lyrics-root", StringComparison.Ordinal) || html.Contains("LyricsPlaceholder", StringComparison.Ordinal);
        }

        public static string RemoveSectionHeaders(string lyrics)
        {
            var lines = lyrics.Split('\n').Where(l => !SectionHeaderLine.IsMatch(l));
            return ExtraBlankLines.Replace(string.Join('\n', lines), "\n\n").Trim();
        }

        private static void AddSongHits(JsonElement hits, List<GeniusSongHit> into)
        {
            if (hits.ValueKind != JsonValueKind.Array)
            {
                return;
            }

            foreach (var hit in hits.EnumerateArray())
            {
                if (hit.ValueKind != JsonValueKind.Object || !hit.TryGetProperty("result", out var result) || result.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var isSong = GetString(hit, "type") == "song" || GetString(result, "_type") == "song";
                var url = GetString(result, "url");
                if (!isSong || string.IsNullOrEmpty(url) || into.Any(h => h.Url == url))
                {
                    continue;
                }

                var artistNames = GetString(result, "artist_names");
                if (string.IsNullOrEmpty(artistNames) && result.TryGetProperty("primary_artist", out var primaryArtist))
                {
                    artistNames = GetString(primaryArtist, "name");
                }

                into.Add(new GeniusSongHit(url, GetString(result, "title") ?? string.Empty, artistNames ?? string.Empty));
            }
        }

        private static void AppendText(HtmlNode node, StringBuilder sb)
        {
            foreach (var child in node.ChildNodes)
            {
                if (child.NodeType == HtmlNodeType.Text)
                {
                    sb.Append(HtmlEntity.DeEntitize(child.InnerText));
                }
                else if (child.Name == "br")
                {
                    sb.Append('\n');
                }
                else if (child.NodeType == HtmlNodeType.Element && child.Name != "script" && child.Name != "style")
                {
                    AppendText(child, sb);
                }
            }
        }

        private static string? GetString(JsonElement element, string name)
        {
            return element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;
        }
    }
}
