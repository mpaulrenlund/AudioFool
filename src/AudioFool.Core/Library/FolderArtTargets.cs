using AudioFool.Core.Models;

namespace AudioFool.Core.Library;

/// <summary>Which of an album's folders a new folder cover file may be written to.</summary>
public static class FolderArtTargets
{
    /// <summary>
    /// True when <paramref name="directory"/> holds a song of another album (a
    /// "Singles" folder, a compilation dropped in with other albums). Every album
    /// there shows the folder's cover file, so writing this album's would change
    /// theirs too: such a folder gets the cover in this album's songs only.
    /// <para>
    /// A song of the same album name counts as this album, so an album split across
    /// two artists by one track's tags still gets its folder file; a song with no
    /// album name never does, since untagged songs of every artist share that.
    /// </para>
    /// </summary>
    public static bool HoldsOtherAlbums(Album album, string directory, IEnumerable<Track> library) =>
        HoldsOtherAlbums(album.Tracks, directory, library);

    /// <summary>
    /// As above, for the songs a cover is being saved from (<paramref name="own"/>),
    /// which may not be one album: Keep Best Cover works folder by folder over a
    /// filter's songs. Songs of more than one album name among them count as a
    /// shared folder too.
    /// </summary>
    public static bool HoldsOtherAlbums(IEnumerable<Track> own, string directory, IEnumerable<Track> library)
    {
        var ownPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var names = new HashSet<string>(SortRules.NameComparer);
        foreach (var track in own)
        {
            ownPaths.Add(track.FilePath);
            if (track.Album.Trim() is { Length: > 0 } name)
                names.Add(name);
        }

        if (names.Count > 1)
            return true;

        return library.Any(t =>
            !ownPaths.Contains(t.FilePath)
            && string.Equals(Path.GetDirectoryName(t.FilePath), directory, StringComparison.OrdinalIgnoreCase)
            && !names.Contains(t.Album.Trim()));
    }
}
