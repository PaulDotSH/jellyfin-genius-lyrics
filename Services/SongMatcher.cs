using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace GeniusLyricsPlugin.Services
{
    // Decides whether a search hit is the song we asked for. Pure, so it can be unit tested.
    public static class SongMatcher
    {
        private const double MinArtistScore = 0.6;
        private const double MinTitleScore = 0.5;

        // "(feat. X)", "[Remastered 2011]", "(Radio Edit)", "(Live at ...)" and similar don't appear in Genius titles.
        private static readonly Regex BracketedNoise = new(
            @"\s*[\(\[][^\)\]]*\b(feat|ft|featuring|prod|remaster(ed)?|live|version|edit|mix|mono|stereo|bonus|deluxe|explicit|clean|single|radio)\b[^\)\]]*[\)\]]",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        // "Song - Remastered 2011", "Song - Live", "Song - Radio Edit"
        private static readonly Regex DashSuffixNoise = new(
            @"\s+-\s+[^-]*\b(remaster(ed)?|live|version|edit|mono|stereo|mix|single|radio)\b.*$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex TrailingFeat = new(@"\s+(feat\.?|ft\.?|featuring)\s+.*$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex Brackets = new(@"[\(\)\[\]]", RegexOptions.Compiled);

        private static readonly Regex ArtistSeparators =new(@"\s*(?:,|;|&|\+|\s+x\s+|\s+(?:feat\.?|ft\.?|featuring|vs\.?)\s+)\s*", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly HashSet<string> ArtistStopWords = new(StringComparer.Ordinal)
        {
            "the", "and", "feat", "ft", "featuring", "with", "band", "orchestra", "de", "la", "le", "el", "los", "las", "und"
        };

        public static string CleanTitle(string title)
        {
            var cleaned = BracketedNoise.Replace(title, string.Empty);
            cleaned = DashSuffixNoise.Replace(cleaned, string.Empty);
            cleaned = TrailingFeat.Replace(cleaned, string.Empty);
            cleaned = cleaned.Trim();
            return cleaned.Length == 0 ? title.Trim() : cleaned;
        }

        public static string CleanArtist(string artist)
        {
            return TrailingFeat.Replace(artist, string.Empty).Trim();
        }

        // Lowercase, strip diacritics, and turn punctuation into single spaces.
        public static string Normalize(string value)
        {
            var decomposed = value.ToLowerInvariant().Normalize(NormalizationForm.FormD);
            var sb = new StringBuilder(decomposed.Length);
            foreach (var c in decomposed)
            {
                if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark)
                {
                    continue;
                }

                sb.Append(char.IsLetterOrDigit(c) ? c : ' ');
            }

            return string.Join(' ', sb.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
        }

        public static double TitleScore(string requested, string candidate)
        {
            var a = Normalize(CleanTitle(requested));
            var b = Normalize(CleanTitle(candidate));
            if (a.Length == 0 || b.Length == 0)
            {
                return 0;
            }

            if (a == b)
            {
                return 1;
            }

            if (ContainsPhrase(b, a) || ContainsPhrase(a, b))
            {
                return 0.8;
            }

            var ta = a.Split(' ').ToHashSet();
            var tb = b.Split(' ').ToHashSet();
            return (double)ta.Intersect(tb).Count() / Math.Max(ta.Count, tb.Count);
        }

        public static double ArtistScore(IEnumerable<string> requestedArtists, string candidateArtists)
        {
            var hit = Normalize(candidateArtists);
            if (hit.Length == 0)
            {
                return 0;
            }

            var hitTokens = hit.Split(' ').ToHashSet();

            // Each credited artist with spaces removed, so "LilCagula" matches "Lil Cagulă" in "El Bacio (Ft. Lil Cagulă)".
            var hitArtistsCompact = ArtistSeparators.Split(Brackets.Replace(candidateArtists, " "))
                .Select(a => Normalize(a).Replace(" ", string.Empty, StringComparison.Ordinal))
                .Where(a => a.Length > 0)
                .ToHashSet();
            double best = 0;
            foreach (var artist in ExpandArtists(requestedArtists))
            {
                var requested = Normalize(artist);
                if (requested.Length == 0)
                {
                    continue;
                }

                if (requested == hit
                    || ContainsPhrase(hit, requested)
                    || (hit.Length >= 3 && ContainsPhrase(requested, hit))
                    || hitArtistsCompact.Contains(requested.Replace(" ", string.Empty, StringComparison.Ordinal)))
                {
                    return 1;
                }

                var significant = requested.Split(' ').Where(t => t.Length > 2 && !ArtistStopWords.Contains(t)).ToList();
                if (significant.Count > 0 && (double)significant.Count(hitTokens.Contains) / significant.Count >= 0.5)
                {
                    best = Math.Max(best, MinArtistScore);
                }
            }

            return best;
        }

        // Returns the best acceptable hit, or null. Without any artist to compare, only an exact title match is accepted.
        public static GeniusSongHit? SelectBest(IEnumerable<GeniusSongHit> hits, IReadOnlyList<string> artists, string title)
        {
            var hasArtists = artists.Any(a => !string.IsNullOrWhiteSpace(a));
            GeniusSongHit? best = null;
            double bestScore = 0;
            foreach (var hit in hits)
            {
                var titleScore = TitleScore(title, hit.Title);
                var artistScore = hasArtists ? ArtistScore(artists, hit.ArtistNames) : 0;
                var acceptable = hasArtists
                    ? artistScore >= MinArtistScore && titleScore >= MinTitleScore
                    : titleScore >= 1;
                if (!acceptable)
                {
                    continue;
                }

                var score = artistScore + titleScore;
                if (score > bestScore)
                {
                    best = hit;
                    bestScore = score;
                }
            }

            return best;
        }

        public static IEnumerable<string> ExpandArtists(IEnumerable<string> artists)
        {
            foreach (var artist in artists.Where(a => !string.IsNullOrWhiteSpace(a)))
            {
                yield return artist;
                foreach (var part in ArtistSeparators.Split(artist).Where(p => !string.IsNullOrWhiteSpace(p) && p != artist))
                {
                    yield return part;
                }
            }
        }

        private static bool ContainsPhrase(string haystack, string needle)
        {
            return $" {haystack} ".Contains($" {needle} ", StringComparison.Ordinal);
        }
    }
}
