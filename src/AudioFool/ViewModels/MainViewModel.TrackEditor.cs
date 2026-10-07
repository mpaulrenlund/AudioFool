using System.Windows;
using System.Windows.Threading;
using AudioFool.Core.Models;

namespace AudioFool.ViewModels;

/// <summary>
/// The Edit Tags window for one song, which stays open beside the main window
/// and follows the Songs table, as the album window follows the Albums list
/// (MainViewModel.AlbumEditor.cs, whose rules it shares: unsaved changes are
/// asked about, Save keeps it open). It moves when exactly one row is selected
/// and that row is another song; a Ctrl-click to a second row, or a new album
/// with nothing selected yet, leaves it where it is.
/// </summary>
public partial class MainViewModel
{
    private TagEditWindow? _trackEditor;

    /// <summary>The song the window shows, as it was when loaded.</summary>
    private Track? _trackEditorTrack;

    private bool _trackEditorBusy;
    private bool _trackEditorSyncQueued;

    /// <summary>A song the user declined to move to and the table couldn't be put back from (see the album window's).</summary>
    private string? _trackEditorDeclined;

    private void ShowTrackEditor(Track track, Window owner)
    {
        if (_trackEditor is { } open)
        {
            open.Activate();
            _ = MoveTrackEditorAsync(track);
            return;
        }

        var window = new TagEditWindow(new TagEditViewModel(track), owner) { FollowsSelection = true };
        window.SaveRequested += (_, _) => _ = SaveTrackEditorAsync();
        window.Closed += (_, _) =>
        {
            _trackEditor = null;
            _trackEditorTrack = null;
        };

        _trackEditor = window;
        _trackEditorTrack = track;
        ShowAlbumEditorWindow(window);
    }

    private void LoadTrackEditor(Track track)
    {
        if (_trackEditor is null)
            return;

        _trackEditorTrack = track;
        _trackEditor.SetViewModel(new TagEditViewModel(track));
    }

    /// <summary>Checks the window against the table once a selection or rebuild settles.</summary>
    private void QueueTrackEditorSync()
    {
        if (_trackEditor is null || _trackEditorSyncQueued)
            return;

        _trackEditorSyncQueued = true;
        _dispatcher.BeginInvoke(() =>
        {
            _trackEditorSyncQueued = false;
            _ = SyncTrackEditorAsync();
        }, DispatcherPriority.Background);
    }

    private async Task SyncTrackEditorAsync()
    {
        if (_trackEditor is null || _trackEditorBusy || _trackEditorTrack is not { } shown)
            return;

        if (SelectedTracks is [var picked])
        {
            var declined = _trackEditorDeclined is { } d && SamePath(d, picked.FilePath);
            if (!declined)
                _trackEditorDeclined = null;

            if (!SamePath(shown.FilePath, picked.FilePath))
            {
                if (!declined)
                    await MoveTrackEditorAsync(picked);
                return;
            }
        }

        // Still the same song, but a grid edit, another dialog or a rescan may
        // have retagged it. Shown again unless something has been typed.
        var current = LibraryCopyOf(shown);
        if (!ReferenceEquals(current, shown) && !_trackEditor.ViewModel.HasChanges)
            LoadTrackEditor(current);
    }

    private async Task MoveTrackEditorAsync(Track target)
    {
        if (_trackEditor is not { } window || _trackEditorBusy || _trackEditorTrack is not { } shown
            || SamePath(shown.FilePath, target.FilePath))
            return;

        var vm = window.ViewModel;
        _trackEditorBusy = true;
        try
        {
            var choice = vm.HasChanges
                ? AskSaveChanges(window, "Unsaved Changes",
                    $"Save your changes to {shown.DisplayTitle} before editing {target.DisplayTitle}?")
                : UnsavedChoice.Discard;

            if (choice == UnsavedChoice.Save && !vm.CanSave)
                choice = UnsavedChoice.Cancel; // the window's footer says what's wrong

            if (choice == UnsavedChoice.Cancel)
            {
                _trackEditorDeclined = target.FilePath;
                RevealTrackRequested?.Invoke(this, LibraryCopyOf(shown));
                return;
            }

            if (choice == UnsavedChoice.Save)
                await ApplyTrackEditAsync(shown, vm.BuildTrackEdit());

            if (_trackEditor is not null)
                LoadTrackEditor(LibraryCopyOf(target));
        }
        finally
        {
            _trackEditorBusy = false;
            QueueTrackEditorSync();
        }
    }

    /// <summary>The window's Save: writes, keeps the window open, and shows the song again as saved.</summary>
    private async Task SaveTrackEditorAsync()
    {
        if (_trackEditor is not { } window || _trackEditorBusy || _trackEditorTrack is not { } shown)
            return;

        _trackEditorBusy = true;
        window.IsEnabled = false;
        try
        {
            await ApplyTrackEditAsync(shown, window.ViewModel.BuildTrackEdit());
            if (_trackEditor is not null)
                LoadTrackEditor(LibraryCopyOf(shown));
        }
        finally
        {
            window.IsEnabled = true;
            _trackEditorBusy = false;
            QueueTrackEditorSync();
        }
    }

    private static bool SamePath(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}
