using AudioFool.Core.Models;

namespace AudioFool.Core.Library;

/// <summary>
/// The three ordering rules the app is built around:
///   artists alphabetically, ignoring a leading article;
///   albums oldest first;
///   tracks by disc then track number.
/// Kept free of UI and I/O concerns so it can be unit tested directly.
/// </summary>
public static class SortRules
{
    /// <summary>
    /// Articles ignored when sorting artist names. "The Beatles" files under B.
    /// Add "A" and "An" here if you ever want those ignored too.
    /// </summary>
    public static readonly string[] DefaultLeadingArticles = ["The"];

    /// <summary>Culture-independent so ordering is identical on every machine.</summary>
    public static readonly StringComparer NameComparer = StringComparer.InvariantCultureIgnoreCase;

    /// <summary>
    /// The string an artist actually sorts on. Strips a leading article, so
    /// "The Beatles" sorts as "Beatles" while "Thelonious Monk" is untouched
    /// (the trailing space in the article match is what makes that work).
    /// </summary>
    public static string ArtistSortKey(string? name, IReadOnlyList<string>? articles = null)
    {
        if (string.IsNullOrWhiteSpace(name))
            return "";

        var trimmed = name.Trim();

        foreach (var article in articles ?? DefaultLeadingArticles)
        {
            if (string.IsNullOrEmpty(article))
                continue;

            // Require whitespace after the article: "The Beatles" matches,
            // "Thelonious" and a band literally named "The" do not.
            if (trimmed.Length <= article.Length)
                continue;
            if (!trimmed.StartsWith(article, StringComparison.InvariantCultureIgnoreCase))
                continue;
            if (!char.IsWhiteSpace(trimmed[article.Length]))
                continue;

            var stripped = trimmed[article.Length..].TrimStart();

            // "The " on its own leaves nothing to sort by; keep the original.
            return stripped.Length == 0 ? trimmed : stripped;
        }

        return trimmed;
    }

    /// <summary>Artist groups A-Z on their precomputed sort key.</summary>
    public static IEnumerable<ArtistGroup> SortArtists(IEnumerable<ArtistGroup> artists) =>
        artists
            .OrderBy(a => a.SortKey, NameComparer)
            .ThenBy(a => a.Name, NameComparer);

    /// <summary>
    /// Artist groups by their most recently added file, newest first; ties fall
    /// back to A-Z.
    /// </summary>
    public static IEnumerable<ArtistGroup> SortArtistsByRecent(IEnumerable<ArtistGroup> artists) =>
        artists
            .Select(a => (Artist: a, Added: a.LastAddedUtc))
            .OrderByDescending(x => x.Added)
            .ThenBy(x => x.Artist.SortKey, NameComparer)
            .ThenBy(x => x.Artist.Name, NameComparer)
            .Select(x => x.Artist);

    /// <summary>
    /// Artist groups by the space their listed albums take, largest first; ties
    /// fall back to A-Z.
    /// </summary>
    public static IEnumerable<ArtistGroup> SortArtistsBySize(IEnumerable<ArtistGroup> artists) =>
        artists
            .Select(a => (Artist: a, Bytes: a.TotalBytes))
            .OrderByDescending(x => x.Bytes)
            .ThenBy(x => x.Artist.SortKey, NameComparer)
            .ThenBy(x => x.Artist.Name, NameComparer)
            .Select(x => x.Artist);

    /// <summary>
    /// Artists in the given order. <paramref name="artists"/> arrives A-Z, as the
    /// library builds it.
    /// </summary>
    public static IEnumerable<ArtistGroup> SortArtists(IEnumerable<ArtistGroup> artists, ArtistOrder order) =>
        order switch
        {
            ArtistOrder.Recent => SortArtistsByRecent(artists),
            ArtistOrder.Size => SortArtistsBySize(artists),
            _ => artists,
        };

    /// <summary>
    /// What a click on the ARTISTS header moves to: A-Z, then RECENT, then SIZE,
    /// then back to A-Z.
    /// </summary>
    public static ArtistOrder Next(ArtistOrder order) => order switch
    {
        ArtistOrder.Name => ArtistOrder.Recent,
        ArtistOrder.Recent => ArtistOrder.Size,
        _ => ArtistOrder.Name,
    };

    /// <summary>
    /// Albums oldest at the top, newest at the bottom, by full release date where
    /// the files have one: "2026" (year only) comes before "2026-10-02", and
    /// "2026-03" before "2026-10-02". Albums with no year land at the bottom
    /// rather than pretending to be from year zero.
    /// </summary>
    public static IEnumerable<Album> SortAlbums(IEnumerable<Album> albums) =>
        albums
            .OrderBy(a => AlbumDate(a) is null ? 1 : 0)
            .ThenBy(a => AlbumDate(a) ?? "", StringComparer.Ordinal)
            .ThenBy(a => a.Title, NameComparer);

    /// <summary>ISO dates of differing precision sort correctly as ordinal text.</summary>
    private static string? AlbumDate(Album album) =>
        album.SortDate ?? album.Year?.ToString("0000", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>
    /// Tracks in playing order: disc number, then track number. Untagged discs
    /// are treated as disc 1; untagged track numbers sink to the end of their disc.
    /// </summary>
    public static IEnumerable<Track> SortTracks(IEnumerable<Track> tracks) =>
        tracks
            .OrderBy(t => t.DiscNumber ?? 1)
            .ThenBy(t => t.TrackNumber ?? int.MaxValue)
            .ThenBy(t => t.DisplayTitle, NameComparer);
}
