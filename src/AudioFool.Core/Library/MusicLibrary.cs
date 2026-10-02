using AudioFool.Core.Models;

namespace AudioFool.Core.Library;

/// <summary>A scanned library: the artist tree plus a flat list of every track.</summary>
public sealed class MusicLibrary
{
    public IReadOnlyList<ArtistGroup> Artists { get; init; } = [];
    public IReadOnlyList<Track> AllTracks { get; init; } = [];

    public static MusicLibrary Empty { get; } = new();

    public int AlbumCount => Artists.Sum(a => a.Albums.Count);

    private Dictionary<string, Album>? _albumByPath;

    /// <summary>
    /// The album in this library that holds <paramref name="shown"/>'s tracks.
    /// A search or a Statistics filter builds its albums from the tracks it
    /// matched, so an album there can be a part of one; looked up in the whole
    /// library, this is the album as it really is. By path, since the two builds
    /// can settle on different spellings of a name. <paramref name="shown"/>
    /// itself when this library doesn't have it.
    /// </summary>
    public Album WholeAlbumOf(Album shown)
    {
        if (shown.Tracks.Count == 0)
            return shown;

        if (_albumByPath is null)
        {
            var byPath = new Dictionary<string, Album>(StringComparer.OrdinalIgnoreCase);
            foreach (var album in Artists.SelectMany(a => a.Albums))
                foreach (var track in album.Tracks)
                    byPath.TryAdd(track.FilePath, album);
            _albumByPath = byPath;
        }

        return _albumByPath.TryGetValue(shown.Tracks[0].FilePath, out var whole) ? whole : shown;
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
