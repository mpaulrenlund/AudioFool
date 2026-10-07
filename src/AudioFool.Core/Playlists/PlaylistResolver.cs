using AudioFool.Core.Library;
using AudioFool.Core.Models;

namespace AudioFool.Core.Playlists;

/// <summary>A playlist's songs as the library has them now.</summary>
/// <param name="Tracks">The songs found, in playlist order.</param>
/// <param name="Missing">Songs the library doesn't have. They stay in the playlist.</param>
/// <param name="Changed">
/// Whether an entry was brought up to date: re-pointed to a file found under
/// another drive letter, or given the artist or album its tags now carry. Worth
/// saving, but not a modification of the playlist.
/// </param>
public sealed record ResolvedPlaylist(IReadOnlyList<Track> Tracks, int Missing, bool Changed)
{
    public TimeSpan Duration => Tracks.Aggregate(TimeSpan.Zero, (sum, t) => sum + t.Duration);
}

/// <summary>
/// Finds playlist songs in a library. By path first; when the path is gone (a
/// drive back under another letter), by file name within the same artist and
/// album, as <see cref="MusicLibrary.Locate"/> finds the last-played song. One
/// resolver per library, since building the lookups is the costly part.
/// </summary>
public sealed class PlaylistResolver
{
    private readonly Dictionary<string, Track> _byPath = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<Track>> _byFileName = new(StringComparer.OrdinalIgnoreCase);

    public PlaylistResolver(IReadOnlyList<Track> tracks)
    {
        foreach (var track in tracks)
        {
            _byPath.TryAdd(track.FilePath, track);

            var name = Path.GetFileName(track.FilePath);
            if (!_byFileName.TryGetValue(name, out var list))
                _byFileName[name] = list = [];
            list.Add(track);
        }
    }

    public Track? Find(PlaylistEntry entry)
    {
        if (_byPath.TryGetValue(entry.FilePath, out var hit))
            return hit;

        return _byFileName.TryGetValue(Path.GetFileName(entry.FilePath), out var candidates)
            ? candidates.FirstOrDefault(t => SortRules.NameComparer.Equals(t.GroupingArtist, entry.Artist)
                                             && SortRules.NameComparer.Equals(t.Album, entry.Album))
            : null;
    }

    /// <summary>
    /// The playlist's songs in order. Entries found are updated in place to the
    /// track's current path, artist and album (see <see cref="ResolvedPlaylist.Changed"/>);
    /// entries not found are left exactly as they were.
    /// </summary>
    public ResolvedPlaylist Resolve(Playlist playlist)
    {
        var tracks = new List<Track>(playlist.Entries.Count);
        var shown = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var missing = 0;
        var changed = false;

        for (var i = 0; i < playlist.Entries.Count; i++)
        {
            var entry = playlist.Entries[i];
            if (Find(entry) is not { } track)
            {
                missing++;
                continue;
            }

            var current = PlaylistEntry.For(track);
            if (current != entry)
            {
                playlist.Entries[i] = current;
                changed = true;
            }

            // Two entries can land on one file once re-pointed; it shows once.
            if (shown.Add(track.FilePath))
                tracks.Add(track);
        }

        return new ResolvedPlaylist(tracks, missing, changed);
    }
}
