using AudioFool.Core.Models;

namespace AudioFool.Core.Library;

/// <summary>
/// Free-text search over the library.
/// <para>
/// One rule does all three levels: a track matches when the query is found in its
/// title, artist, album artist or album. Albums and artists are then shown when any
/// of their tracks match, which falls out of simply rebuilding the tree from the
/// filtered tracks.
/// </para>
/// <para>
/// That single rule gives the behaviour you'd expect at each level. Searching an
/// artist matches every one of their tracks, so the whole discography stays
/// browsable. Searching a song title matches only that track, so you land directly
/// on it with its album and artist for context.
/// </para>
/// </summary>
public static class LibrarySearch
{
    private static readonly char[] TermSeparators = [' ', '\t'];

    /// <summary>
    /// Splits a query into terms. Every term has to match somewhere for the track
    /// to count, so "floyd dark" finds Dark Side of the Moon even though no single
    /// field contains that whole phrase.
    /// </summary>
    public static string[] Terms(string? query) =>
        string.IsNullOrWhiteSpace(query)
            ? []
            : query.Split(TermSeparators, StringSplitOptions.RemoveEmptyEntries
                                          | StringSplitOptions.TrimEntries);

    public static bool Matches(Track track, string[] terms)
    {
        foreach (var term in terms)
        {
            if (!MatchesTerm(track, term))
                return false;
        }

        return true;
    }

    private static bool MatchesTerm(Track track, string term) =>
        Contains(track.DisplayTitle, term)
        || Contains(track.Artist, term)
        || Contains(track.AlbumArtist, term)
        || Contains(track.Album, term);

    private static bool Contains(string? value, string term) =>
        !string.IsNullOrEmpty(value)
        && value.Contains(term, StringComparison.CurrentCultureIgnoreCase);

    /// <summary>
    /// The matching subset of <paramref name="tracks"/>. An empty query returns
    /// everything, so callers don't need to special-case "not searching".
    /// </summary>
    public static IReadOnlyList<Track> Filter(IReadOnlyList<Track> tracks, string? query)
    {
        var terms = Terms(query);
        if (terms.Length == 0)
            return tracks;

        var results = new List<Track>();
        foreach (var track in tracks)
        {
            if (Matches(track, terms))
                results.Add(track);
        }

        return results;
    }
}
