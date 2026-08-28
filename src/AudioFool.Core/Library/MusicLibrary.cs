using AudioFool.Core.Models;

namespace AudioFool.Core.Library;

/// <summary>A scanned library: the artist tree plus a flat list of every track.</summary>
public sealed class MusicLibrary
{
    public IReadOnlyList<ArtistGroup> Artists { get; init; } = [];
    public IReadOnlyList<Track> AllTracks { get; init; } = [];

    public static MusicLibrary Empty { get; } = new();

    public int AlbumCount => Artists.Sum(a => a.Albums.Count);
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
    public bool AnyChanges => Read > 0 || Removed > 0;

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
