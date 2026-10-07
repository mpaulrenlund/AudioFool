using AudioFool.Core.Models;

namespace AudioFool.Core.Playlists;

/// <summary>
/// One song in a playlist. The path finds it; the artist and album, with the
/// file name, find it again when the drive comes back under another letter,
/// as <see cref="Settings.LastPlayedTrack"/> does.
/// </summary>
public sealed record PlaylistEntry(string FilePath, string Artist, string Album)
{
    public static PlaylistEntry For(Track track) => new(track.FilePath, track.GroupingArtist, track.Album);
}

/// <summary>
/// A named, ordered list of songs, kept in the app's own file rather than in the
/// music files. Changed only through <see cref="PlaylistStore"/>, which stamps
/// <see cref="ModifiedUtc"/> and saves.
/// </summary>
public sealed class Playlist
{
    public Guid Id { get; init; } = Guid.NewGuid();

    public string Name { get; set; } = "";

    /// <summary>The one playlist the hearts fill. Always there; can't be renamed or deleted.</summary>
    public bool IsLiked { get; init; }

    /// <summary>When a song was added or removed, or the name or picture changed.</summary>
    public DateTime ModifiedUtc { get; set; }

    /// <summary>
    /// The file name of a picture chosen for it, inside
    /// <see cref="PlaylistStore.PicturesDirectory"/>; null for the automatic one.
    /// </summary>
    public string? Picture { get; set; }

    public List<PlaylistEntry> Entries { get; init; } = [];

    public bool Contains(string path) =>
        Entries.Any(e => string.Equals(e.FilePath, path, StringComparison.OrdinalIgnoreCase));
}
