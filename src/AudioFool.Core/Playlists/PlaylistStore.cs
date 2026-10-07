using System.Text.Json;
using AudioFool.Core.Library;
using AudioFool.Core.Models;

namespace AudioFool.Core.Playlists;

/// <summary>
/// Every playlist, Liked included, in <c>playlists.json</c> beside the library
/// cache. Each change is saved straight away, to a temporary file moved over the
/// old one, as <see cref="Scrobbling.ScrobbleQueue"/> does. Nothing is written to
/// the music files.
/// <para>
/// A song whose file can't be found is never dropped from here: a drive that is
/// unplugged, or back under another letter, isn't a deleted song. Only removing
/// it, or unliking it, takes it out.
/// </para>
/// </summary>
public sealed class PlaylistStore
{
    public const string LikedName = "Liked";

    public static string DefaultPath { get; } = Path.Combine(LibraryCache.CacheDirectory, "playlists.json");

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly string _path;
    private readonly List<Playlist> _playlists;

    private PlaylistStore(string path, List<Playlist> playlists)
    {
        _path = path;
        _playlists = playlists;
    }

    /// <summary>Where chosen pictures are copied, so moving the original doesn't lose one.</summary>
    public string PicturesDirectory => Path.Combine(Path.GetDirectoryName(_path)!, "playlist-pictures");

    public IReadOnlyList<Playlist> Playlists => _playlists;

    public Playlist Liked => _playlists.First(p => p.IsLiked);

    /// <summary>Most recently modified first (the user's call), then by name.</summary>
    public IReadOnlyList<Playlist> ByRecent() =>
        [.. _playlists
            .OrderByDescending(p => p.ModifiedUtc)
            .ThenBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase)];

    /// <summary>
    /// Never throws. A missing file is no playlists but Liked. An unreadable one is
    /// moved aside rather than overwritten by the next save, so it can be recovered.
    /// </summary>
    public static PlaylistStore Load(string path, DateTime nowUtc)
    {
        List<Playlist> playlists = [];
        try
        {
            if (File.Exists(path))
                playlists = JsonSerializer.Deserialize<List<Playlist>>(File.ReadAllText(path), JsonOptions) ?? [];
        }
        catch (JsonException)
        {
            MoveAside(path, nowUtc);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            // Unreadable for now (locked, say): start empty but don't save over it.
            return new PlaylistStore(path, [NewLiked(nowUtc)]) { _readOnly = true };
        }

        var store = new PlaylistStore(path, [.. playlists.Where(p => p is not null)]);
        if (!store._playlists.Any(p => p.IsLiked))
        {
            store._playlists.Add(NewLiked(nowUtc));
            store.Save();
        }

        return store;
    }

    private bool _readOnly;

    private static Playlist NewLiked(DateTime nowUtc) =>
        new() { Name = LikedName, IsLiked = true, ModifiedUtc = nowUtc };

    private static void MoveAside(string path, DateTime nowUtc)
    {
        try
        {
            File.Move(path, Path.ChangeExtension(path, $".unreadable-{nowUtc:yyyyMMddHHmmss}.json"), overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>
    /// Why <paramref name="name"/> can't be used, or null when it can: it needs a
    /// name, and one no other playlist has (Liked included).
    /// </summary>
    public string? NameProblem(string? name, Playlist? renaming = null)
    {
        var trimmed = name?.Trim() ?? "";
        if (trimmed.Length == 0)
            return "Give the playlist a name.";

        return _playlists.Any(p => !ReferenceEquals(p, renaming)
                                   && string.Equals(p.Name.Trim(), trimmed, StringComparison.CurrentCultureIgnoreCase))
            ? $"There's already a playlist called \"{trimmed}\"."
            : null;
    }

    public Playlist Create(string name, DateTime nowUtc)
    {
        if (NameProblem(name) is { } problem)
            throw new ArgumentException(problem, nameof(name));

        var playlist = new Playlist { Name = name.Trim(), ModifiedUtc = nowUtc };
        _playlists.Add(playlist);
        Save();
        return playlist;
    }

    public void Rename(Playlist playlist, string name, DateTime nowUtc)
    {
        if (playlist.IsLiked)
            throw new InvalidOperationException("Liked can't be renamed.");
        if (NameProblem(name, playlist) is { } problem)
            throw new ArgumentException(problem, nameof(name));

        if (playlist.Name == name.Trim())
            return;

        playlist.Name = name.Trim();
        Touch(playlist, nowUtc);
    }

    /// <summary>Deletes the playlist and its copied picture. Liked can't be deleted.</summary>
    public bool Delete(Playlist playlist)
    {
        if (playlist.IsLiked || !_playlists.Remove(playlist))
            return false;

        DeletePicture(playlist.Picture);
        Save();
        return true;
    }

    /// <summary>
    /// Adds the songs to the end, in order, skipping any already in it. Returns how
    /// many were added; nothing changes, not even the date, when that is none.
    /// </summary>
    public int Add(Playlist playlist, IEnumerable<Track> tracks, DateTime nowUtc)
    {
        var added = 0;
        foreach (var track in tracks)
        {
            if (playlist.Contains(track.FilePath))
                continue;

            playlist.Entries.Add(PlaylistEntry.For(track));
            added++;
        }

        if (added > 0)
            Touch(playlist, nowUtc);

        return added;
    }

    /// <summary>Removes the songs at these paths. Returns how many went.</summary>
    public int Remove(Playlist playlist, IEnumerable<string> paths, DateTime nowUtc)
    {
        var set = paths.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var removed = playlist.Entries.RemoveAll(e => set.Contains(e.FilePath));
        if (removed > 0)
            Touch(playlist, nowUtc);

        return removed;
    }

    /// <summary>
    /// Moves the songs at these paths, as one block in their playlist order, to
    /// just before the song at <paramref name="beforePath"/>, or to the end when
    /// that is null. Every other entry keeps its place among the rest, songs whose
    /// file isn't found included. Returns whether the order changed; nothing is
    /// saved, not even the date, when it didn't.
    /// </summary>
    public bool Move(Playlist playlist, IEnumerable<string> paths, string? beforePath, DateTime nowUtc)
    {
        var set = paths.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var moving = playlist.Entries.Where(e => set.Contains(e.FilePath)).ToList();
        if (moving.Count == 0)
            return false;

        var rest = playlist.Entries.Where(e => !set.Contains(e.FilePath)).ToList();

        // Dropped on one of the moving songs: before the first one left after it.
        var at = rest.Count;
        if (beforePath is not null)
        {
            var target = playlist.Entries.FindIndex(e => string.Equals(e.FilePath, beforePath, StringComparison.OrdinalIgnoreCase));
            if (target >= 0)
            {
                var anchor = playlist.Entries.Skip(target).FirstOrDefault(e => !set.Contains(e.FilePath));
                at = anchor is null ? rest.Count : rest.IndexOf(anchor);
            }
        }

        rest.InsertRange(at, moving);
        if (rest.SequenceEqual(playlist.Entries, ReferenceEqualityComparer.Instance))
            return false;

        playlist.Entries.Clear();
        playlist.Entries.AddRange(rest);
        Touch(playlist, nowUtc);
        return true;
    }

    /// <summary>
    /// Copies <paramref name="sourceFile"/> in as the playlist's picture, replacing
    /// any earlier one. The copy gets a new name each time, so nothing holding the
    /// old picture is shown it under the new one's name. Throws on a file that
    /// can't be read.
    /// </summary>
    public void SetPicture(Playlist playlist, string sourceFile, DateTime nowUtc)
    {
        Directory.CreateDirectory(PicturesDirectory);
        var name = $"{playlist.Id:N}-{nowUtc.Ticks}{Path.GetExtension(sourceFile).ToLowerInvariant()}";
        File.Copy(sourceFile, Path.Combine(PicturesDirectory, name), overwrite: true);

        var old = playlist.Picture;
        playlist.Picture = name;
        Touch(playlist, nowUtc);
        DeletePicture(old);
    }

    /// <summary>Back to the automatic picture: the first song's cover.</summary>
    public void ClearPicture(Playlist playlist, DateTime nowUtc)
    {
        if (playlist.Picture is not { } old)
            return;

        playlist.Picture = null;
        Touch(playlist, nowUtc);
        DeletePicture(old);
    }

    /// <summary>The full path of the playlist's chosen picture, or null.</summary>
    public string? PicturePath(Playlist playlist) =>
        playlist.Picture is { } name ? Path.Combine(PicturesDirectory, name) : null;

    private void DeletePicture(string? name)
    {
        if (name is null)
            return;

        try
        {
            File.Delete(Path.Combine(PicturesDirectory, name));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // An orphaned picture costs a little disk; failing the change would cost more.
        }
    }

    private void Touch(Playlist playlist, DateTime nowUtc)
    {
        playlist.ModifiedUtc = nowUtc;
        Save();
    }

    /// <summary>Writes the file. Also called after re-pointing songs, which isn't a modification.</summary>
    public void Save()
    {
        if (_readOnly)
            return;

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var temp = _path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(_playlists, JsonOptions));
            File.Move(temp, _path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            // Still held in memory, and written with the next change.
        }
    }
}
