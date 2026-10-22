using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;

using Track = AudioFool.Core.Models.Track;

namespace AudioFool;

/// <summary>
/// Keyboard shortcuts for when the main window has focus (the user's list,
/// session 46). Space plays or pauses; the rest are Ctrl+letter or Ctrl+arrow.
/// </summary>
public partial class MainWindow
{
    internal enum Shortcut
    {
        None, PlayPause, Statistics, Analyze, Art, Shuffle, Scan, Repeat, BitPerfect,
        Search, Like, Mute, GoToPlaying, Previous, Next, VolumeUp, VolumeDown, Theme,
        FindReplace,
    }

    private const double VolumeStep = 0.05;

    private void HookShortcuts() =>
        PreviewKeyDown += (_, e) =>
        {
            if (HandleShortcut(e.Key, Keyboard.Modifiers, e.IsRepeat, Keyboard.FocusedElement as DependencyObject))
                e.Handled = true;
        };

    /// <summary>
    /// Runs the shortcut for a key, if it is one; true when it was. The modifiers
    /// and focus are parameters so ThemeLab can call it without a real keyboard.
    /// A held key acts once, like F9–F11, except the volume.
    /// </summary>
    internal bool HandleShortcut(Key key, ModifierKeys modifiers, bool isRepeat, DependencyObject? focused)
    {
        var shortcut = ResolveShortcut(key, modifiers, focused);
        if (shortcut == Shortcut.None)
            return false;

        if (isRepeat && shortcut is not (Shortcut.VolumeUp or Shortcut.VolumeDown))
            return true;

        // The ones that open a window wait for the key event to finish:
        // Statistics and Find and Replace are modal, and nothing should open
        // mid-keystroke.
        if (shortcut is Shortcut.Statistics or Shortcut.Analyze or Shortcut.Art or Shortcut.Search
            or Shortcut.FindReplace)
            Dispatcher.BeginInvoke(() => RunShortcut(shortcut), DispatcherPriority.Input);
        else
            RunShortcut(shortcut);

        return true;
    }

    /// <summary>
    /// Which shortcut a key is, where focus is.
    /// <list type="bullet">
    /// <item>Space is left alone in a text box, in an open menu, and while a
    /// type-ahead in Artists or Albums is under way ("pink floyd"). Otherwise it
    /// is play/pause, even on a focused button, which Space would click.</item>
    /// <item>Ctrl shortcuts are off while a song-table cell is being edited, so
    /// Ctrl+S there doesn't toggle Shuffle when a save was meant; Ctrl+arrows are
    /// off in any text box, where they move by word.</item>
    /// </list>
    /// </summary>
    internal Shortcut ResolveShortcut(Key key, ModifierKeys modifiers, DependencyObject? focused)
    {
        var inText = focused is TextBoxBase;
        var inCellEdit = inText && FindAncestor<DataGrid>(focused) == TrackGrid;
        var inMenu = focused is MenuItem || FindAncestor<ContextMenu>(focused) is not null;

        if (inMenu || IsDraggingSongs)
            return Shortcut.None;

        if (key == Key.Space && modifiers == ModifierKeys.None)
            return inText || _typeAheadBuffer.Length > 0 ? Shortcut.None : Shortcut.PlayPause;

        if (modifiers != ModifierKeys.Control || inCellEdit)
            return Shortcut.None;

        if (inText && key is Key.Left or Key.Right or Key.Up or Key.Down)
            return Shortcut.None;

        return key switch
        {
            Key.W => Shortcut.Statistics,
            Key.H => Shortcut.FindReplace,
            Key.Q => Shortcut.Analyze,
            Key.D => Shortcut.Art,
            Key.S => Shortcut.Shuffle,
            Key.R => Shortcut.Scan,
            Key.E => Shortcut.Repeat,
            Key.B => Shortcut.BitPerfect,
            Key.T => Shortcut.Theme,
            Key.F => Shortcut.Search,
            Key.L => Shortcut.Like,
            Key.M => Shortcut.Mute,
            Key.J => Shortcut.GoToPlaying,
            Key.Left => Shortcut.Previous,
            Key.Right => Shortcut.Next,
            Key.Up => Shortcut.VolumeUp,
            Key.Down => Shortcut.VolumeDown,
            _ => Shortcut.None,
        };
    }

    private void RunShortcut(Shortcut shortcut)
    {
        switch (shortcut)
        {
            case Shortcut.PlayPause:
                _viewModel.TogglePlayCommand.Execute(null);
                break;
            case Shortcut.Statistics:
                _viewModel.ShowStatisticsCommand.Execute(null);
                break;
            case Shortcut.FindReplace:
                _viewModel.ShowFindReplaceCommand.Execute(null);
                break;
            case Shortcut.Analyze:
                _viewModel.AnalyzeTrackCommand.Execute(AnalyzeTarget());
                break;
            // Ctrl+D again closes it, from here or from the viewer (ArtWindow.ClosesOn).
            case Shortcut.Art when _artWindow is { } open:
                _artWindow = null;
                open.Close();
                break;
            case Shortcut.Art:
                _ = ArtShowsPlaying
                    ? ShowArtAsync(_viewModel.GetNowPlayingFullArtAsync(), _viewModel.NowPlayingCaption)
                    : ShowArtAsync(_viewModel.GetSelectedAlbumFullArtAsync(), _viewModel.SelectedAlbumCaption);
                break;
            case Shortcut.Shuffle:
                _viewModel.ToggleShuffleCommand.Execute(null);
                break;
            case Shortcut.Scan when _viewModel.RefreshCommand.CanExecute(null):
                _viewModel.RefreshCommand.Execute(null);
                break;
            case Shortcut.Repeat:
                _viewModel.CycleRepeatCommand.Execute(null);
                break;
            case Shortcut.BitPerfect when _viewModel.CanUseExclusiveOutput:
                _viewModel.IsExclusiveOutput = !_viewModel.IsExclusiveOutput;
                break;
            case Shortcut.Theme:
                _viewModel.ToggleThemeCommand.Execute(null);
                break;
            case Shortcut.Search:
                SearchBox.Focus();
                SearchBox.SelectAll();
                break;
            case Shortcut.Like:
                _viewModel.ToggleLikeCommand.Execute(_viewModel.NowPlaying);
                break;
            case Shortcut.Mute:
                _viewModel.ToggleMuteCommand.Execute(null);
                break;
            case Shortcut.GoToPlaying:
                _viewModel.NavigateToNowPlaying();
                break;
            case Shortcut.Previous:
                _viewModel.PreviousCommand.Execute(null);
                break;
            case Shortcut.Next:
                _viewModel.NextCommand.Execute(null);
                break;
            case Shortcut.VolumeUp:
                _viewModel.StepVolume(VolumeStep);
                break;
            case Shortcut.VolumeDown:
                _viewModel.StepVolume(-VolumeStep);
                break;
        }
    }

    /// <summary>
    /// Ctrl+Q: the selected song (the focused one of several), or the playing
    /// one when no song is selected.
    /// </summary>
    internal Track? AnalyzeTarget()
    {
        var selected = TrackGrid.SelectedItems.Count == 0 ? null
            : TrackGrid.CurrentItem is Track current && TrackGrid.SelectedItems.Contains(current) ? current
            : TrackGrid.SelectedItem as Track;

        return selected ?? _viewModel.NowPlaying;
    }

    /// <summary>
    /// Ctrl+D: the playing album's art, as a click on the playback bar's art
    /// opens it; with nothing playing, the selected album's (the user's call).
    /// </summary>
    internal bool ArtShowsPlaying => _viewModel.NowPlaying is not null;
}
