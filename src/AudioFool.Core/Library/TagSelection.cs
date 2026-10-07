using AudioFool.Core.Models;

namespace AudioFool.Core.Library;

/// <summary>
/// What an edit of several rows in the Artists or Albums list covers: every
/// track of those artists or albums, and the words that say so in the dialog's
/// title ("3 artists, 214 tracks").
/// <para>
/// Always whole artists and whole albums, looked up in <c>whole</c>, never the
/// part a search or Statistics filter shows: writing to part of an album splits
/// it (session 32). Rows that turn out to be the same artist or album count once.
/// </para>
/// </summary>
public sealed record TagSelection(IReadOnlyList<Track> Tracks, string Description)
{
    public static TagSelection ForArtists(MusicLibrary whole, IEnumerable<ArtistGroup> shown)
    {
        var artists = shown.Select(whole.WholeArtistOf).Distinct(ReferenceEqualityComparer.Instance).Cast<ArtistGroup>().ToList();
        var tracks = Distinct(artists.SelectMany(a => a.Albums).SelectMany(a => a.Tracks));
        return new(tracks, $"{Count(artists.Count, "artist")}, {Count(tracks.Count, "track")}");
    }

    public static TagSelection ForAlbums(MusicLibrary whole, IEnumerable<Album> shown)
    {
        var albums = shown.Select(whole.WholeAlbumOf).Distinct(ReferenceEqualityComparer.Instance).Cast<Album>().ToList();
        var tracks = Distinct(albums.SelectMany(a => a.Tracks));
        return new(tracks, $"{Count(albums.Count, "album")}, {Count(tracks.Count, "track")}");
    }

    private static List<Track> Distinct(IEnumerable<Track> tracks)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        return tracks.Where(t => seen.Add(t.FilePath)).ToList();
    }

    private static string Count(int n, string noun) => n == 1 ? $"1 {noun}" : $"{n:N0} {noun}s";
}
