using GeniusLyricsPlugin.Services;
using Xunit;

namespace GeniusLyricsPlugin.Tests
{
    public class GeniusParserTests
    {
        // Mirrors the structure of a current Genius song page, with placeholder text.
        private const string SongPage = @"
<html><body>
<div id=""lyrics-root"">
  <div data-lyrics-container=""true"" class=""Lyrics__Container"">
    <div data-exclude-from-selection=""true"" class=""LyricsHeader__Container"">
      <button><span>12 Contributors</span></button>
      <div class=""Dropdown""><a href=""#"">Italiano</a></div>
      <h2>Some Song Lyrics</h2>
      <div class=""SongBioPreview""><p>Some Song is the debut single, which topped the charts in many countries.</p></div>
    </div>
    [Verse 1]<br/>First line &amp; more<br/><a href=""/annotation""><span>Annotated line</span></a><br/><br/>[Chorus]<br/>Chorus line
  </div>
  <div data-exclude-from-selection=""true"" class=""InreadAd"">Advertisement</div>
  <div data-lyrics-container=""true"" class=""Lyrics__Container"">Second container line with the word lyrics<br/>Last line</div>
</div>
</body></html>";

        [Fact]
        public void ExtractLyrics_DropsHeaderAboutBlurbAndAds()
        {
            var lyrics = GeniusParser.ExtractLyrics(SongPage);

            Assert.NotNull(lyrics);
            Assert.DoesNotContain("Contributors", lyrics);
            Assert.DoesNotContain("debut single", lyrics);
            Assert.DoesNotContain("Italiano", lyrics);
            Assert.DoesNotContain("Advertisement", lyrics);
            Assert.StartsWith("[Verse 1]\nFirst line & more\nAnnotated line\n\n[Chorus]\nChorus line", lyrics);
        }

        [Fact]
        public void ExtractLyrics_KeepsLaterContainersIntact()
        {
            var lyrics = GeniusParser.ExtractLyrics(SongPage);

            // The old regex stripped everything up to the word "lyrics" in every container.
            Assert.Contains("Second container line with the word lyrics\nLast line", lyrics);
        }

        [Fact]
        public void ExtractLyrics_ReturnsNullWithoutContainers()
        {
            Assert.Null(GeniusParser.ExtractLyrics("<html><body><div id=\"lyrics-root\"></div></body></html>"));
        }

        [Fact]
        public void RemoveSectionHeaders_DropsBracketLinesOnly()
        {
            var cleaned = GeniusParser.RemoveSectionHeaders("[Verse 1]\nA line [with brackets]\n\n[Chorus: Someone]\nChorus");

            Assert.Equal("A line [with brackets]\n\nChorus", cleaned);
        }

        [Theory]
        [InlineData(@"{""value"":true,""key"":""is_instrumental""}", true)]
        [InlineData(@"{\""value\"":true,\""key\"":\""is_instrumental\""}", true)]
        [InlineData(@"{\""value\"":false,\""key\"":\""is_instrumental\""}", false)]
        [InlineData("<div class=\"LyricsPlaceholder\">This song is an instrumental</div>", true)]
        [InlineData("<a>Queen Of Pop (Instrumental)</a>", false)]
        public void IsMarkedInstrumental(string html, bool expected)
        {
            Assert.Equal(expected, GeniusParser.IsMarkedInstrumental(html));
        }

        [Fact]
        public void ParseSearchHits_ReadsWebApiSections()
        {
            const string json = @"{""response"":{""sections"":[
                {""type"":""top_hit"",""hits"":[{""type"":""song"",""result"":{""_type"":""song"",""url"":""https://genius.com/a"",""title"":""A"",""artist_names"":""Artist""}}]},
                {""type"":""song"",""hits"":[
                    {""type"":""song"",""result"":{""_type"":""song"",""url"":""https://genius.com/a"",""title"":""A"",""artist_names"":""Artist""}},
                    {""type"":""song"",""result"":{""_type"":""song"",""url"":""https://genius.com/b"",""title"":""B"",""primary_artist"":{""name"":""Other""}}}]},
                {""type"":""artist"",""hits"":[{""type"":""artist"",""result"":{""_type"":""artist"",""url"":""https://genius.com/artists/x""}}]}]}}";

            var hits = GeniusParser.ParseSearchHits(json);

            Assert.NotNull(hits);
            Assert.Collection(
                hits,
                h => Assert.Equal(("https://genius.com/a", "A", "Artist"), (h.Url, h.Title, h.ArtistNames)),
                h => Assert.Equal(("https://genius.com/b", "B", "Other"), (h.Url, h.Title, h.ArtistNames)));
        }

        [Fact]
        public void ParseSearchHits_ReadsPublicApiHits()
        {
            const string json = @"{""meta"":{""status"":200},""response"":{""hits"":[{""type"":""song"",""result"":{""url"":""https://genius.com/c"",""title"":""C"",""artist_names"":""Band""}}]}}";

            var hits = GeniusParser.ParseSearchHits(json);

            Assert.NotNull(hits);
            Assert.Single(hits);
            Assert.Equal("https://genius.com/c", hits[0].Url);
        }

        [Fact]
        public void ParseSearchHits_ReturnsNullForCloudflarePage()
        {
            Assert.Null(GeniusParser.ParseSearchHits("<!DOCTYPE html><html><head><title>Just a moment...</title>"));
        }
    }
}
