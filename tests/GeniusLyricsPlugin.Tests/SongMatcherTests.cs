using GeniusLyricsPlugin.Services;
using Xunit;

namespace GeniusLyricsPlugin.Tests
{
    public class SongMatcherTests
    {
        [Theory]
        [InlineData("Song (feat. Someone)", "Song")]
        [InlineData("Song [Remastered 2011]", "Song")]
        [InlineData("Song - Remastered 2011", "Song")]
        [InlineData("Song - Live at Wembley", "Song")]
        [InlineData("Song (Radio Edit)", "Song")]
        [InlineData("Song ft. Someone", "Song")]
        [InlineData("PU$H (Original Mix)", "PU$H")]
        [InlineData("Song (Remix)", "Song (Remix)")]
        [InlineData("$P$ (Studio Session #02)", "$P$ (Studio Session #02)")]
        [InlineData("(Live)", "(Live)")]
        public void CleanTitle(string input, string expected)
        {
            Assert.Equal(expected, SongMatcher.CleanTitle(input));
        }

        [Theory]
        [InlineData("Ștefan Bănică", "stefan banica")]
        [InlineData("Guns N' Roses", "guns n roses")]
        [InlineData("...Baby One More Time", "baby one more time")]
        public void Normalize(string input, string expected)
        {
            Assert.Equal(expected, SongMatcher.Normalize(input));
        }

        [Fact]
        public void ArtistScore_MatchesAccentsAndFeaturedArtists()
        {
            Assert.Equal(1, SongMatcher.ArtistScore(new[] { "Stefan Banica" }, "Ștefan Bănică"));
            Assert.Equal(1, SongMatcher.ArtistScore(new[] { "Laura Vass" }, "Copilul de Aur & Laura Vass"));
            Assert.Equal(1, SongMatcher.ArtistScore(new[] { "Copilul de Aur & Laura Vass" }, "Laura Vass"));
        }

        [Fact]
        public void ArtistScore_IgnoresSpacingWithinAnArtistName()
        {
            Assert.Equal(1, SongMatcher.ArtistScore(new[] { "LilCagula" }, "Lil Cagulă"));
            Assert.Equal(1, SongMatcher.ArtistScore(new[] { "LilCagula" }, "El Bacio (Ft. Lil Cagulă)"));
        }

        [Fact]
        public void ArtistScore_RejectsUnrelatedArtists()
        {
            Assert.Equal(0, SongMatcher.ArtistScore(new[] { "Copilul de Aur" }, "Delikt (ROU) (Ft. Sindicatul R.A.N.S.)"));

            // The old word fallback accepted any shared word longer than two letters, like "the".
            Assert.Equal(0, SongMatcher.ArtistScore(new[] { "The Killers" }, "The Beatles"));
        }

        [Fact]
        public void SelectBest_RejectsOtherSongsByTheSameArtist()
        {
            var hits = new[] { new GeniusSongHit("https://genius.com/x", "Their Big Hit", "Artist") };

            Assert.Null(SongMatcher.SelectBest(hits, new[] { "Artist" }, "Obscure Album Track"));
        }

        [Fact]
        public void SelectBest_PrefersExactTitleOverPartialMatch()
        {
            var hits = new[]
            {
                new GeniusSongHit("https://genius.com/remix", "Song (Club Remix)", "Artist"),
                new GeniusSongHit("https://genius.com/song", "Song", "Artist"),
            };

            Assert.Equal("https://genius.com/song", SongMatcher.SelectBest(hits, new[] { "Artist" }, "Song [Remastered]")?.Url);
        }

        [Fact]
        public void SelectBest_MatchesTitleContainedInGeniusTitle()
        {
            var hits = new[] { new GeniusSongHit("https://genius.com/Tony-1-0-regrete-lyrics", "1/0 Regrete", "Tony & Super Ed") };

            Assert.NotNull(SongMatcher.SelectBest(hits, new[] { "Tony", "Super Ed" }, "0 Regrete"));
        }

        [Fact]
        public void SelectBest_WithoutArtistsRequiresExactTitle()
        {
            var hits = new[] { new GeniusSongHit("https://genius.com/a", "Song Two", "Anyone") };

            Assert.Null(SongMatcher.SelectBest(hits, Array.Empty<string>(), "Song"));
            Assert.NotNull(SongMatcher.SelectBest(hits, Array.Empty<string>(), "Song Two"));
        }

        [Theory]
        [InlineData("https://genius.com/Artist-song-lyrics", true)]
        [InlineData("https://www.genius.com/Artist-song-lyrics", true)]
        [InlineData("http://genius.com/Artist-song-lyrics", false)]
        [InlineData("https://genius.com.evil.example/x", false)]
        [InlineData("http://169.254.169.254/latest/meta-data", false)]
        [InlineData("not a url", false)]
        public void IsGeniusUrl(string url, bool expected)
        {
            Assert.Equal(expected, ScraperService.IsGeniusUrl(url));
        }
    }
}
