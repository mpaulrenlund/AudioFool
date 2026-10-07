using System.Windows;
using System.Windows.Threading;
using AudioFool.Core.Art;
using AudioFool.Core.Models;

namespace AudioFool.ViewModels;

/// <summary>
/// The Edit Album Tags window, which stays open beside the main window and
/// follows the Albums list (the user's request). Choosing another album, by
/// clicking it, by keyboard or by choosing another artist, loads that album into
/// the window; unsaved changes are asked about first (Save, Don't Save, Cancel).
/// Save writes and keeps the window open on the album, read again from its files.
/// </summary>
public partial class MainViewModel
{
    /// <summary>
    /// How the window is shown and how unsaved changes are asked about. Only
    /// ThemeLab replaces them, so that nothing appears on the desktop.
    /// </summary>
    public static Action<TagEditWindow> ShowAlbumEditorWindow { get; set; } = window => window.Show();
    public static Func<Window, string, string, UnsavedChoice> AskSaveChanges { get; set; } = PromptWindow.AskSaveChanges;

    private TagEditWindow? _albumEditor;

    /// <summary>The album the window shows, as it was when loaded.</summary>
    private Album? _albumEditorAlbum;

    /// <summary>Saving or asking about unsaved changes: the window doesn't follow the list meanwhile.</summary>
    private bool _albumEditorBusy;

    private bool _albumEditorSyncQueued;

    /// <summary>
    /// An album the user declined to move to (Cancel) while the list couldn't be
    /// put back, say because a search hides the shown one: not asked about again
    /// until the list moves elsewhere.
    /// </summary>
    private Album? _albumEditorDeclined;

    /// <summary>Opens the window on <paramref name="album"/>, or moves the open one to it.</summary>
    private void ShowAlbumEditor(Album album, Window owner)
    {
        if (_albumEditor is { } open)
        {
            open.Activate();
            _ = MoveAlbumEditorAsync(album);
            return;
        }

        var window = new TagEditWindow(NewAlbumEditorViewModel(album), owner) { FollowsSelection = true };
        window.SaveRequested += (_, _) => _ = SaveAlbumEditorAsync();
        window.Closed += (_, _) =>
        {
            var (vm, shown) = (window.ViewModel, _albumEditorAlbum);
            _albumEditor = null;
            _albumEditorAlbum = null;
            // Closing is Cancel, but covers "Save Embedded Art" wrote are real files.
            if (shown is not null)
                _ = FinishAlbumDialogAsync(shown, vm, saved: false);
        };

        _albumEditor = window;
        _albumEditorAlbum = album;
        ShowAlbumEditorWindow(window);
    }

    private TagEditViewModel NewAlbumEditorViewModel(Album album) =>
        new(album, _artService, new OnlineArtSearch(_settings.FanartTvApiKey));

    private void LoadAlbumEditor(Album album)
    {
        if (_albumEditor is null)
            return;

        _albumEditorAlbum = album;
        _albumEditor.SetViewModel(NewAlbumEditorViewModel(album));
    }

    /// <summary>
    /// Checks the window against the Albums list once things settle: called on
    /// every album selection and library rebuild, which come in bursts while a
    /// list is rebuilt (the artist's first album, then the kept one).
    /// </summary>
    private void QueueAlbumEditorSync()
    {
        if (_albumEditor is null || _albumEditorSyncQueued)
            return;

        _albumEditorSyncQueued = true;
        _dispatcher.BeginInvoke(() =>
        {
            _albumEditorSyncQueued = false;
            _ = SyncAlbumEditorAsync();
        }, DispatcherPriority.Background);
    }

    private async Task SyncAlbumEditorAsync()
    {
        if (_albumEditor is null || _albumEditorBusy || _albumEditorAlbum is not { } shown)
            return;

        // A playlist leaves the album selection underneath alone, and so does the window.
        if (!IsPlaylistMode && SelectedAlbum is { } item)
        {
            var target = _folderFilteredLibrary.WholeAlbumOf(item.Album);
            var declined = _albumEditorDeclined is { } d && SameAlbum(d, target);
            if (!declined)
                _albumEditorDeclined = null;

            if (!SameAlbum(shown, target))
            {
                if (!declined)
                    await MoveAlbumEditorAsync(target);
                return;
            }
        }

        // Still the same album, but a save elsewhere (the grid, another dialog)
        // or a rescan may have changed its tracks. Shown again, unless something
        // has been typed, which Save will write over the current tracks anyway.
        if (shown.Tracks.FirstOrDefault() is { } first
            && _folderFilteredLibrary.AlbumHolding(first.FilePath) is { } current
            && !current.Tracks.SequenceEqual(shown.Tracks)
            && !_albumEditor.ViewModel.HasChanges)
            LoadAlbumEditor(current);
    }

    /// <summary>
    /// Moves the window to <paramref name="target"/>, asking first when the shown
    /// album has unsaved changes. Cancel puts the list back on the shown album.
    /// </summary>
    private async Task MoveAlbumEditorAsync(Album target)
    {
        if (_albumEditor is not { } window || _albumEditorBusy || _albumEditorAlbum is not { } shown
            || SameAlbum(shown, target))
            return;

        var vm = window.ViewModel;
        _albumEditorBusy = true;
        try
        {
            var choice = vm.HasChanges
                ? AskSaveChanges(window, "Unsaved Changes",
                    $"Save your changes to {shown.Title} before editing {target.Title}?")
                : UnsavedChoice.Discard;

            if (choice == UnsavedChoice.Save && !vm.CanSave)
                choice = UnsavedChoice.Cancel; // the window's footer says what's wrong

            if (choice == UnsavedChoice.Cancel)
            {
                _albumEditorDeclined = target;
                SelectWhereTrackFiles(shown.Tracks[0]);
                return;
            }

            var targetPath = target.Tracks.FirstOrDefault()?.FilePath;
            await FinishAlbumDialogAsync(shown, vm, saved: choice == UnsavedChoice.Save);

            // A save rebuilt the library; take the target as it is now.
            if (targetPath is not null && _folderFilteredLibrary.AlbumHolding(targetPath) is { } now)
                target = now;
            if (_albumEditor is not null)
                LoadAlbumEditor(target);
        }
        finally
        {
            _albumEditorBusy = false;
            QueueAlbumEditorSync();
        }
    }

    /// <summary>
    /// The window's Save: writes, keeps the window open, and shows the album again
    /// as saved. The lists follow it if a rename moved it (FollowEditedTracks).
    /// </summary>
    private async Task SaveAlbumEditorAsync()
    {
        if (_albumEditor is not { } window || _albumEditorBusy || _albumEditorAlbum is not { } shown
            || shown.Tracks.Count == 0)
            return;

        _albumEditorBusy = true;
        window.IsEnabled = false;
        try
        {
            await FinishAlbumDialogAsync(shown, window.ViewModel, saved: true, followTracks: true);
            if (_albumEditor is not null && _folderFilteredLibrary.AlbumHolding(shown.Tracks[0].FilePath) is { } saved)
                LoadAlbumEditor(saved);
        }
        finally
        {
            window.IsEnabled = true;
            _albumEditorBusy = false;
            QueueAlbumEditorSync();
        }
    }

    /// <summary>The same album when <paramref name="other"/> holds the first file of <paramref name="shown"/>.</summary>
    private static bool SameAlbum(Album shown, Album other) =>
        ReferenceEquals(shown, other)
        || (shown.Tracks.FirstOrDefault() is { } first
            && other.Tracks.Any(t => string.Equals(t.FilePath, first.FilePath, StringComparison.OrdinalIgnoreCase)));
}
