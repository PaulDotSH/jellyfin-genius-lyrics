namespace GeniusLyricsPlugin.Services
{
    public enum LyricsLookupStatus
    {
        Found,
        NotFound,
        Instrumental,

        // The source could not be reached or blocked us; the song should be retried, not backed off.
        Failed
    }

    public sealed record LyricsLookupResult(LyricsLookupStatus Status, string? Url = null, string? Lyrics = null, string Format = "txt")
    {
        public static readonly LyricsLookupResult NotFound = new(LyricsLookupStatus.NotFound);
        public static readonly LyricsLookupResult Failed = new(LyricsLookupStatus.Failed);
    }
}
