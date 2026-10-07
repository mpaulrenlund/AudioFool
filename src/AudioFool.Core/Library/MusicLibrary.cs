using AudioFool.Core.Models;
using AudioFool.Core.Settings;

namespace AudioFool.Core.Library;

/// <summary>A scanned library: the artist tree plus a flat list of every track.</summary>
public sealed class MusicLibrary
{
    public IReadOnlyList<ArtistGroup> Artists { get; init; } = [];
    public IReadOnlyList<Track> AllTracks { get; init; } = [];

    public static MusicLibrary Empty { get; } = new();

    public int AlbumCount => Artists.Sum(a => a.Albums.Count);

    private Dictionary<string, Album>? _albumByPath;
    private Dictionary<string, ArtistGroup>? _artistByPath;

    /// <summary>
    /// Finds where a remembered track sits in the artist tree. By path first; if
    /// the path is gone (a drive that changed letter), by artist and album name,
    /// and then the file name within that album. When only the album is found the
    /// track is null; null overall when the album is not here at all.
    /// </summary>
    public (ArtistGroup Artist, Album Album, Track? Track)? Locate(LastPlayedTrack last)
    {
        foreach (var artist in Artists)
            foreach (var album in artist.Albums)
                if (album.Tracks.FirstOrDefault(t => string.Equals(t.FilePath, last.FilePath, StringComparison.OrdinalIgnoreCase)) is { } hit)
                    return (artist, album, hit);

        var name = Path.GetFileName(last.FilePath);
        foreach (var artist in Artists.Where(a => SortRules.NameComparer.Equals(a.Name, last.Artist)))
            foreach (var album in artist.Albums.Where(a => SortRules.NameComparer.Equals(a.Title, last.Album)))
                return (artist, album, album.Tracks.FirstOrDefault(t =>
                    string.Equals(Path.GetFileName(t.FilePath), name, StringComparison.OrdinalIgnoreCase)));

        return null;
    }

    /// <summary>
    /// The artist and album names <see cref="Locate"/> will find <paramref name="track"/>
    /// by later; null when the track is not in this library.
    /// </summary>
    public LastPlayedTrack? Remember(Track track)
    {
        foreach (var artist in Artists)
            foreach (var album in artist.Albums)
                if (album.Tracks.Any(t => string.Equals(t.FilePath, track.FilePath, StringComparison.OrdinalIgnoreCase)))
                    return new LastPlayedTrack(track.FilePath, artist.Name, album.Title);

        return null;
    }

    /// <summary>
    /// The album in this library that holds <paramref name="shown"/>'s tracks.
    /// A search or a Statistics filter builds its albums from the tracks it
    /// matched, so an album there can be a part of one; looked up in the whole
    /// library, this is the album as it really is. By path, since the two builds
    /// can settle on different spellings of a name. <paramref name="shown"/>
    /// itself when this library doesn't have it.
    /// </summary>
    public Album WholeAlbumOf(Album shown) =>
        shown.Tracks.Count > 0 && AlbumHolding(shown.Tracks[0].FilePath) is { } whole ? whole : shown;

    /// <summary>The album in this library that holds the file at <paramref name="path"/>; null if none.</summary>
    public Album? AlbumHolding(string path)
    {
        if (_albumByPath is null)
        {
            var byPath = new Dictionary<string, Album>(StringComparer.OrdinalIgnoreCase);
            foreach (var album in Artists.SelectMany(a => a.Albums))
                foreach (var track in album.Tracks)
                    byPath.TryAdd(track.FilePath, album);
            _albumByPath = byPath;
        }

        return _albumByPath.GetValueOrDefault(path);
    }

    /// <summary>
    /// As <see cref="WholeAlbumOf"/>, for an artist: the artist in this library
    /// that holds <paramref name="shown"/>'s first track, with all its albums.
    /// </summary>
    public ArtistGroup WholeArtistOf(ArtistGroup shown)
    {
        if (shown.Albums.FirstOrDefault(a => a.Tracks.Count > 0) is not { } album)
            return shown;

        if (_artistByPath is null)
        {
            var byPath = new Dictionary<string, ArtistGroup>(StringComparer.OrdinalIgnoreCase);
            foreach (var artist in Artists)
                foreach (var track in artist.Albums.SelectMany(a => a.Tracks))
                    byPath.TryAdd(track.FilePath, artist);
            _artistByPath = byPath;
        }

        return _artistByPath.TryGetValue(album.Tracks[0].FilePath, out var whole) ? whole : shown;
    }
}

/// <summary>Progress while scanning, for the status bar.</summary>
public readonly record struct ScanProgress(int FilesFound, int FilesRead)
{
    public double Fraction => FilesFound == 0 ? 0 : (double)FilesRead / FilesFound;
}

/// <summary>
/// What a scan actually did. Lets the UI skip a rebuild - and the selection reset
/// that comes with it - when a background refresh found nothing new.
/// </summary>
public readonly record struct ScanSummary(int Reused, int Read, int Removed)
{
    /// <summary>
    /// Reused tracks that were given their <see cref="Track.AddedUtc"/>, which a
    /// cache written before it existed lacks. Counts as a change so the cache is
    /// saved with them, and the artist order by date added is right.
    /// </summary>
    public int Backfilled { get; init; }

    public bool AnyChanges => Read > 0 || Removed > 0 || Backfilled > 0;

    /// <summary>True when every track came straight from the cache.</summary>
    public bool EntirelyCached => Read == 0 && Removed == 0 && Reused > 0;
}

public sealed class ScanResult
{
    public required MusicLibrary Library { get; init; }
    public required ScanSummary Summary { get; init; }

    /// <summary>
    /// Configured folders that could not be read - typically an unplugged portable
    /// drive. Their tracks are still in the library, carried over from the cache.
    /// </summary>
    public IReadOnlyList<string> UnavailableFolders { get; init; } = [];

    public bool AnyFolderUnavailable => UnavailableFolders.Count > 0;
}
