using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using AudioFool.Core.Library;
using AudioFool.Core.Models;
using AudioFool.Core.Playlists;
using AudioFool.Formatting;

namespace AudioFool.ViewModels;

/// <summary>
/// Playlists and Liked (the user's request, 2026-10-07). The playlists button in
/// the ARTISTS header turns the Artists panel into the Playlists panel; Albums
/// goes blank, and the Songs panel shows the chosen playlist under a header of
/// its own. The hearts in the Like column fill Liked.
/// <para>
/// Songs are found in the whole library, ticked folders or not: a playlist is
/// an explicit choice. One the library doesn't have stays in the playlist and
/// isn't shown (see <see cref="PlaylistStore"/>).
/// </para>
/// </summary>
public sealed partial class MainViewModel
{
    private PlaylistStore _playlistStore = null!;
    private PlaylistResolver? _resolver;
    private MusicLibrary? _resolverLibrary;

    /// <summary>The shown playlist as last resolved: what the header counts.</summary>
    private ResolvedPlaylist? _shownResolved;

    /// <summary>
    /// Recently Added (the user's request, 2026-10-07): the songs that arrived in
    /// the last 30 days, worked out from the ticked folders rather than saved.
    /// One row for the whole session, so it stays selected across rebuilds.
    /// </summary>
    private readonly PlaylistItemViewModel _recentlyAdded =
        new(new Playlist { Name = RecentlyAdded.Name }, isRecentlyAdded: true);

    private IReadOnlyList<Album> _recentAlbumList = [];
    private MusicLibrary? _recentLibrary;
    private bool _fillingRecentAlbums;

    /// <summary>Liked, then Recently Added (both pinned, the user's call), then the rest most recently modified first.</summary>
    public ObservableCollection<PlaylistItemViewModel> Playlists { get; } = [];

    /// <summary>The Albums panel while Recently Added shows: its albums, newest addition first.</summary>
    public ObservableCollection<AlbumItemViewModel> RecentAlbums { get; } = [];

    /// <summary>
    /// An album picked in Recently Added narrows the Songs panel to it, under the
    /// album header; none picked shows every recent song under the playlist header.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsAlbumHeader))]
    [NotifyPropertyChangedFor(nameof(ShowsPlaylistHeader))]
    private AlbumItemViewModel? _selectedRecentAlbum;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsAlbumHeader))]
    [NotifyPropertyChangedFor(nameof(ShowsPlaylistHeader))]
    [NotifyPropertyChangedFor(nameof(ShowsEmptyState))]
    [NotifyPropertyChangedFor(nameof(ShowsRecentAlbums))]
    [NotifyPropertyChangedFor(nameof(CanRemoveFromShownPlaylist))]
    private bool _isPlaylistMode;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsPlaylistHeader))]
    [NotifyPropertyChangedFor(nameof(ShowsRecentAlbums))]
    [NotifyPropertyChangedFor(nameof(CanRemoveFromShownPlaylist))]
    private PlaylistItemViewModel? _selectedPlaylist;

    /// <summary>Recently Added is showing, so the Albums panel lists its albums.</summary>
    public bool ShowsRecentAlbums => IsPlaylistMode && SelectedPlaylist is { IsRecentlyAdded: true };

    /// <summary>For the song menu's Remove from Playlist: not in Recently Added, which only the library changes.</summary>
    public bool CanRemoveFromShownPlaylist => IsPlaylistMode && SelectedPlaylist is { IsEditable: true };

    /// <summary>
    /// The album the album header describes: the Albums list's, or in Recently
    /// Added the one picked there (null while every recent song shows).
    /// </summary>
    public AlbumItemViewModel? HeaderAlbum => IsPlaylistMode ? SelectedRecentAlbum : SelectedAlbum;

    [ObservableProperty]
    private BitmapSource? _selectedPlaylistArt;

    /// <summary>The paths in Liked, for the hearts. A new set on each change, so every heart re-reads it.</summary>
    [ObservableProperty]
    private IReadOnlySet<string> _likedPaths = new HashSet<string>();

    /// <summary>Path to place in the shown playlist, for the # column; null while browsing albums.</summary>
    [ObservableProperty]
    private IReadOnlyDictionary<string, int>? _playlistPositions;

    public bool ShowsAlbumHeader => HeaderAlbum is not null;

    public bool ShowsPlaylistHeader => IsPlaylistMode && SelectedPlaylist is not null && SelectedRecentAlbum is null;

    /// <summary>No album chosen, or a playlist with nothing to show.</summary>
    public bool ShowsEmptyState => IsPlaylistMode ? Tracks.Count == 0 : SelectedAlbum is null;

    public string PlaylistHeaderTitle => SelectedPlaylist?.Name ?? "";

    public string PlaylistHeaderModified =>
        SelectedPlaylist is { IsRecentlyAdded: true } ? RecentlyAdded.Window
        : SelectedPlaylist is { } item ? PlaylistText.Modified(item.Playlist.ModifiedUtc)
        : "";

    public string PlaylistHeaderTrackCount =>
        _shownResolved is { } shown ? PlaylistText.HeaderTrackCount(shown.Tracks.Count, shown.Missing) : "";

    /// <summary>"42:36", the album header's clock format.</summary>
    public string PlaylistHeaderDuration => _shownResolved is { } shown ? Display.Time(shown.Duration) : "";

    private static DateTime Now => DateTime.UtcNow;

    /// <summary>Where playlists are kept. ThemeLab points it at a scratch file before building a view model.</summary>
    public static string PlaylistsPath { get; set; } = PlaylistStore.DefaultPath;

    private void InitialisePlaylists()
    {
        _playlistStore = PlaylistStore.Load(PlaylistsPath, Now);
        RefreshPlaylists();
    }

    private PlaylistResolver Resolver
    {
        get
        {
            if (_resolver is null || !ReferenceEquals(_resolverLibrary, _library))
            {
                _resolver = new PlaylistResolver(_library.AllTracks);
                _resolverLibrary = _library;
            }

            return _resolver;
        }
    }

    /// <summary>
    /// Finds every playlist's songs again, then brings the Playlists rows, their
    /// order and the hearts up to date. Saves when a song was re-pointed (a drive
    /// back under another letter), which doesn't count as a change to the playlist.
    /// An empty library (before the first scan lands) finds nothing, so nothing is
    /// re-pointed then.
    /// </summary>
    private void RefreshPlaylists()
    {
        var counts = new Dictionary<Playlist, int>();
        var changed = false;
        foreach (var playlist in _playlistStore.Playlists)
        {
            var resolved = Resolver.Resolve(playlist);
            counts[playlist] = resolved.Tracks.Count;
            changed |= resolved.Changed;
        }

        if (changed)
            _playlistStore.Save();

        RefreshRecentAlbums();
        counts[_recentlyAdded.Playlist] = _recentAlbumList.Sum(a => a.Tracks.Count);

        // Liked first and Recently Added second, pinned (the user's call); the
        // rest most recently modified first.
        var liked = _playlistStore.Liked;
        List<Playlist> order = [liked, _recentlyAdded.Playlist, .. _playlistStore.ByRecent().Where(p => p != liked)];
        for (var i = Playlists.Count - 1; i >= 0; i--)
        {
            if (!order.Contains(Playlists[i].Playlist))
                Playlists.RemoveAt(i);
        }

        for (var i = 0; i < order.Count; i++)
        {
            var at = -1;
            for (var j = 0; j < Playlists.Count; j++)
            {
                if (ReferenceEquals(Playlists[j].Playlist, order[i]))
                {
                    at = j;
                    break;
                }
            }

            if (at < 0)
                Playlists.Insert(i, ReferenceEquals(order[i], _recentlyAdded.Playlist) ? _recentlyAdded : new PlaylistItemViewModel(order[i]));
            else if (at != i)
                Playlists.Move(at, i);

            Playlists[i].Refresh(counts[order[i]]);
        }

        LikedPaths = _playlistStore.Liked.Entries
            .Select(e => e.FilePath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Works Recently Added out again when the ticked library is a new one (a
    /// scan, a save, a folder ticked) or <paramref name="force"/> asks, as opening
    /// the Playlists panel does so the 30 days are counted from now. Otherwise,
    /// as after a like, the albums and the one picked are left alone. The pick
    /// is kept by artist and title, or dropped back to every recent song.
    /// </summary>
    private void RefreshRecentAlbums(bool force = false)
    {
        if (!force && ReferenceEquals(_recentLibrary, _folderFilteredLibrary))
            return;

        _recentLibrary = _folderFilteredLibrary;
        _recentAlbumList = RecentlyAdded.Albums(_folderFilteredLibrary, Now);

        var picked = SelectedRecentAlbum?.Album;
        _fillingRecentAlbums = true;
        try
        {
            SelectedRecentAlbum = null;
            RecentAlbums.Clear();
            foreach (var album in _recentAlbumList)
                RecentAlbums.Add(new AlbumItemViewModel(album, _artService, namesArtist: true));

            if (picked is not null)
            {
                SelectedRecentAlbum = RecentAlbums.FirstOrDefault(a =>
                    SortRules.NameComparer.Equals(a.Album.ArtistName, picked.ArtistName)
                    && SortRules.NameComparer.Equals(a.Album.Title, picked.Title));
            }
        }
        finally
        {
            _fillingRecentAlbums = false;
        }
    }

    partial void OnSelectedRecentAlbumChanged(AlbumItemViewModel? value)
    {
        if (!_fillingRecentAlbums && ShowsRecentAlbums)
            ShowPlaylist();

        RaiseAlbumHeaderChanged();
        _ = LoadAlbumHeaderArtAsync(value);
    }

    /// <summary>
    /// Recently Added's row clicked while it already shows: back to every recent
    /// song, from an album picked in the Albums panel.
    /// </summary>
    public void ShowAllRecentlyAdded()
    {
        if (ShowsRecentAlbums)
            SelectedRecentAlbum = null;
    }

    partial void OnIsPlaylistModeChanged(bool value)
    {
        RaiseQualityMenuChanged();

        // Recently Added opens on every recent song, whichever album was picked before.
        _fillingRecentAlbums = true;
        SelectedRecentAlbum = null;
        _fillingRecentAlbums = false;

        if (value)
        {
            RefreshRecentAlbums(force: true);
            RefreshPlaylists();

            // Liked the first time (the top of the list); after that, whichever
            // was chosen last, while it still exists.
            if (SelectedPlaylist is null || !Playlists.Contains(SelectedPlaylist))
                SelectedPlaylist = Playlists.FirstOrDefault();

            ShowPlaylist();
        }
        else
        {
            _shownResolved = null;
            PlaylistPositions = null;
            SelectedPlaylistArt = null;
            FillAlbumTracks(SelectedAlbum);
        }

        RaiseAlbumHeaderChanged();
        _ = LoadAlbumHeaderArtAsync(HeaderAlbum);
        RefreshEmptyState();
    }

    partial void OnSelectedPlaylistChanged(PlaylistItemViewModel? value)
    {
        _fillingRecentAlbums = true;
        SelectedRecentAlbum = null;
        _fillingRecentAlbums = false;

        if (IsPlaylistMode)
            ShowPlaylist();
    }

    /// <summary>Fills the Songs panel with the chosen playlist, in its order.</summary>
    private void ShowPlaylist()
    {
        if (!IsPlaylistMode)
            return;

        TracksChanging?.Invoke(this, EventArgs.Empty);
        Tracks.Clear();

        if (SelectedPlaylist is { IsRecentlyAdded: true })
        {
            // The header counts every recent song; the rows are the album picked, or all of them.
            _shownResolved = new ResolvedPlaylist(RecentlyAdded.Tracks(_recentAlbumList), 0, false);
            foreach (var track in SelectedRecentAlbum?.Album.Tracks ?? _shownResolved.Tracks)
                Tracks.Add(track);
        }
        else if (SelectedPlaylist is { } item)
        {
            _shownResolved = Resolver.Resolve(item.Playlist);
            foreach (var track in _shownResolved.Tracks)
                Tracks.Add(track);
        }
        else
        {
            _shownResolved = null;
        }

        RefreshShownPlaylist();
        _ = LoadPlaylistArtAsync(SelectedPlaylist);
    }

    /// <summary>
    /// The header, the # places and the empty state for the shown playlist,
    /// without touching its rows: a song unliked while Liked is shown keeps its
    /// row, with an empty heart and no place, until the playlist is shown again,
    /// so rows don't jump out from under the pointer.
    /// </summary>
    private void RefreshShownPlaylist()
    {
        if (IsPlaylistMode && SelectedPlaylist is { IsRecentlyAdded: true })
        {
            // No places: # shows the track number, since the songs run album by
            // album in track order, and with no places nothing can be dragged.
            _shownResolved = new ResolvedPlaylist(RecentlyAdded.Tracks(_recentAlbumList), 0, false);
            PlaylistPositions = null;
        }
        else if (IsPlaylistMode && SelectedPlaylist is { } item)
        {
            _shownResolved = Resolver.Resolve(item.Playlist);
            var positions = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < _shownResolved.Tracks.Count; i++)
                positions[_shownResolved.Tracks[i].FilePath] = i + 1;
            PlaylistPositions = positions;
        }

        OnPropertyChanged(nameof(PlaylistHeaderTitle));
        OnPropertyChanged(nameof(PlaylistHeaderModified));
        OnPropertyChanged(nameof(PlaylistHeaderTrackCount));
        OnPropertyChanged(nameof(PlaylistHeaderDuration));
        OnPropertyChanged(nameof(ShowsEmptyState));
        RefreshEmptyState();
    }

    /// <summary>
    /// After the library view is rebuilt (a scan, a tag save, a search): the
    /// songs are new objects, so find them again.
    /// </summary>
    private void OnLibraryViewRebuilt()
    {
        RefreshPlaylists();
        ShowPlaylist();
        QueueAlbumEditorSync();
        QueueTrackEditorSync();
    }

    /// <summary>After a playlist changed: its row, the order, the hearts, and the Songs panel if it's showing.</summary>
    private void AfterPlaylistChange(Playlist changed, bool rebuildRows)
    {
        RefreshPlaylists();

        if (!IsPlaylistMode || !ReferenceEquals(SelectedPlaylist?.Playlist, changed))
            return;

        if (rebuildRows)
            ShowPlaylist();
        else
            RefreshShownPlaylist();

        _ = LoadPlaylistArtAsync(SelectedPlaylist);
    }

    // ------------------------------------------------------------ commands

    /// <summary>The PLAYLISTS header: back to Artists, as the playlists button does.</summary>
    [RelayCommand]
    private void ShowArtists() => IsPlaylistMode = false;

    /// <summary>A heart: likes the song (to the end of Liked) or unlikes it.</summary>
    [RelayCommand]
    private void ToggleLike(Track? track)
    {
        if (track is null)
            return;

        var liked = _playlistStore.Liked;
        if (liked.Contains(track.FilePath))
            _playlistStore.Remove(liked, [track.FilePath], Now);
        else
            _playlistStore.Add(liked, [track], Now);

        AfterPlaylistChange(liked, rebuildRows: false);
    }

    /// <summary>The song table's selected songs, in table order, for the menu commands.</summary>
    private List<Track> MenuTracks()
    {
        var selected = SelectedTracks.ToHashSet(ReferenceEqualityComparer.Instance);
        return [.. Tracks.Where(selected.Contains)];
    }

    /// <summary>Adds the selected songs to the end of a playlist, skipping any already there.</summary>
    [RelayCommand]
    private void AddToPlaylist(PlaylistItemViewModel? target)
    {
        if (target is not { IsEditable: true } || MenuTracks() is not { Count: > 0 } tracks)
            return;

        var added = _playlistStore.Add(target.Playlist, tracks, Now);
        AfterPlaylistChange(target.Playlist, rebuildRows: true);

        StatusText = added == 0
            ? (tracks.Count == 1 ? $"Already in {target.Name}." : $"All {tracks.Count:N0} are already in {target.Name}.")
            : $"Added {Songs(added)} to {target.Name}.";
    }

    /// <summary>
    /// Asks for a name and makes a playlist. From the song table's menu it starts
    /// with the selected songs; from the Playlists panel it starts empty and is
    /// shown.
    /// </summary>
    [RelayCommand]
    private void NewPlaylist(string? withSelection)
    {
        if (Application.Current.MainWindow is not { } owner)
            return;

        var name = PromptWindow.AskName(owner, "New Playlist", "Create", "",
            text => _playlistStore.NameProblem(text));
        if (name is null)
            return;

        var playlist = _playlistStore.Create(name, Now);
        var tracks = withSelection is not null ? MenuTracks() : [];
        if (tracks.Count > 0)
            _playlistStore.Add(playlist, tracks, Now);

        RefreshPlaylists();
        if (IsPlaylistMode && withSelection is null)
            SelectedPlaylist = Playlists.FirstOrDefault(p => ReferenceEquals(p.Playlist, playlist));

        StatusText = tracks.Count > 0 ? $"Added {Songs(tracks.Count)} to {playlist.Name}." : $"Made {playlist.Name}.";
    }

    /// <summary>Takes the selected songs out of the shown playlist. The files are untouched.</summary>
    [RelayCommand]
    private void RemoveFromPlaylist()
    {
        if (!CanRemoveFromShownPlaylist || SelectedPlaylist is not { } item || MenuTracks() is not { Count: > 0 } tracks)
            return;

        var removed = _playlistStore.Remove(item.Playlist, tracks.Select(t => t.FilePath), Now);
        AfterPlaylistChange(item.Playlist, rebuildRows: true);
        StatusText = $"Removed {Songs(removed)} from {item.Name}.";
    }

    /// <summary>
    /// Whether a song table row can be dragged to a new place: in a playlist,
    /// and still in it (a song just unliked from Liked has no place).
    /// </summary>
    public bool CanReorder(Track track) =>
        IsPlaylistMode && PlaylistPositions is { } positions && positions.ContainsKey(track.FilePath);

    /// <summary>
    /// Drag to reorder: moves these songs, as a block in playlist order, to just
    /// before the row now at <paramref name="dropIndex"/> in the Songs panel, or to
    /// the end past the last row. The rows are rebuilt; the play queue isn't
    /// touched (the user's call: it keeps the order it started with). Returns
    /// whether anything moved.
    /// </summary>
    public bool MovePlaylistSongs(IEnumerable<Track> songs, int dropIndex)
    {
        if (!IsPlaylistMode || SelectedPlaylist is not { } item)
            return false;

        var moving = new HashSet<Track>(songs.Where(CanReorder), ReferenceEqualityComparer.Instance);
        if (moving.Count == 0)
            return false;

        // The first row from the drop point on that has a place. One of the moving
        // songs is fine: the store then places them before the next one left, so a
        // song not found that sat right after them stays there.
        var before = Tracks.Skip(Math.Max(dropIndex, 0)).FirstOrDefault(CanReorder);

        // A drop that leaves the table as it was changes nothing, not even the
        // date, although the store might move a song not found past the block.
        var shown = Tracks.Where(CanReorder).ToList();
        var rest = shown.Where(t => !moving.Contains(t)).ToList();
        var anchor = before is null ? null : shown.Skip(shown.IndexOf(before)).FirstOrDefault(t => !moving.Contains(t));
        rest.InsertRange(anchor is null ? rest.Count : rest.IndexOf(anchor), shown.Where(moving.Contains));
        if (rest.SequenceEqual(shown))
            return false;

        if (!_playlistStore.Move(item.Playlist, moving.Select(t => t.FilePath), before?.FilePath, Now))
            return false;

        AfterPlaylistChange(item.Playlist, rebuildRows: true);
        return true;
    }

    [RelayCommand]
    private void RenamePlaylist(PlaylistItemViewModel? item)
    {
        if (item is not { CanRenameOrDelete: true } || Application.Current.MainWindow is not { } owner)
            return;

        var name = PromptWindow.AskName(owner, "Rename Playlist", "Rename", item.Name,
            text => _playlistStore.NameProblem(text, item.Playlist));
        if (name is null)
            return;

        _playlistStore.Rename(item.Playlist, name, Now);
        AfterPlaylistChange(item.Playlist, rebuildRows: false);
    }

    [RelayCommand]
    private void DeletePlaylist(PlaylistItemViewModel? item)
    {
        if (item is not { CanRenameOrDelete: true } || Application.Current.MainWindow is not { } owner)
            return;

        if (!PromptWindow.Confirm(owner, "Delete Playlist",
                $"Delete \"{item.Name}\"? Its songs stay in the library.", "Delete"))
            return;

        var wasShown = ReferenceEquals(SelectedPlaylist, item);
        _playlistStore.Delete(item.Playlist);
        RefreshPlaylists();

        if (wasShown)
            SelectedPlaylist = Playlists.FirstOrDefault();

        StatusText = $"Deleted {item.Name}.";
    }

    /// <summary>Picks an image for the playlist; a copy is kept with the playlists.</summary>
    [RelayCommand]
    private void SetPlaylistPicture(PlaylistItemViewModel? item)
    {
        item ??= SelectedPlaylist;
        if (item is not { IsEditable: true })
            return;

        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = $"Picture for {item.Name}",
            Filter = "Images|*.jpg;*.jpeg;*.png;*.bmp;*.gif;*.webp;*.tif;*.tiff|All files|*.*",
        };

        if (dialog.ShowDialog() != true)
            return;

        // Decoded once first, so a file WPF can't show is refused here rather
        // than leaving the header blank.
        if (LoadPicture(dialog.FileName, 16) is null)
        {
            StatusText = $"Couldn't read {Path.GetFileName(dialog.FileName)} as a picture.";
            return;
        }

        try
        {
            _playlistStore.SetPicture(item.Playlist, dialog.FileName, Now);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            StatusText = $"Couldn't copy the picture: {ex.Message}";
            return;
        }

        AfterPlaylistChange(item.Playlist, rebuildRows: false);
    }

    /// <summary>Back to the first song's cover.</summary>
    [RelayCommand]
    private void ClearPlaylistPicture(PlaylistItemViewModel? item)
    {
        item ??= SelectedPlaylist;
        if (item is not { IsEditable: true })
            return;

        _playlistStore.ClearPicture(item.Playlist, Now);
        AfterPlaylistChange(item.Playlist, rebuildRows: false);
    }

    /// <summary>Double-click on a playlist: plays it from the top.</summary>
    [RelayCommand]
    private void PlayPlaylist(PlaylistItemViewModel? item)
    {
        if (item is null)
            return;

        SelectedPlaylist = item;
        ShowAllRecentlyAdded();
        PlayTrack(Tracks.FirstOrDefault());
    }

    private static string Songs(int count) => count == 1 ? "1 song" : $"{count:N0} songs";

    // ------------------------------------------------------------ picture

    /// <summary>
    /// The chosen picture, or the first song's cover when none is chosen (the
    /// user's call): stable, where the newest song's would change with every like.
    /// </summary>
    private async Task LoadPlaylistArtAsync(PlaylistItemViewModel? item)
    {
        BitmapSource? art = null;
        if (item is not null)
        {
            if (_playlistStore.PicturePath(item.Playlist) is { } path)
                art = await Task.Run(() => LoadPicture(path, AlbumHeaderArtWidth));
            else if (Tracks.FirstOrDefault(t => LikedOrShown(t)) is { } first)
                art = await _artService.GetTrackArtAsync(first, AlbumHeaderArtWidth);
        }

        if (ReferenceEquals(SelectedPlaylist, item) && IsPlaylistMode)
            SelectedPlaylistArt = art;
    }

    /// <summary>The first song still in the playlist, passing over one just unliked.</summary>
    private bool LikedOrShown(Track track) =>
        PlaylistPositions is not { } positions || positions.ContainsKey(track.FilePath);

    public Task<BitmapSource?> GetSelectedPlaylistFullArtAsync()
    {
        if (SelectedPlaylist is not { } item)
            return Task.FromResult<BitmapSource?>(null);

        if (_playlistStore.PicturePath(item.Playlist) is { } path)
            return Task.Run(() => LoadPicture(path, null));

        return Tracks.FirstOrDefault(LikedOrShown) is { } first
            ? _artService.GetFullTrackArtAsync(first)
            : Task.FromResult<BitmapSource?>(null);
    }

    public string SelectedPlaylistCaption => SelectedPlaylist?.Name ?? "Playlist";

    /// <summary>Reads a picture file whole, so it isn't held open. Null when it can't be read.</summary>
    private static BitmapSource? LoadPicture(string path, int? decodeWidth)
    {
        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            if (decodeWidth is { } width)
                image.DecodePixelWidth = width;
            image.UriSource = new Uri(path);
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException or UnauthorizedAccessException
                                       or ArgumentException or InvalidOperationException
                                       or System.Runtime.InteropServices.COMException)
        {
            return null;
        }
    }
}
