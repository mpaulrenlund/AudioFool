using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using System.Windows.Media.Imaging;
using AudioFool.Services;
using AudioFool.ViewModels;
using Wpf.Ui.Controls;

// System.Windows.Controls.Primitives (needed for the slider's drag events) also
// defines a Track, which is the Slider's rail rather than a song.
using Track = AudioFool.Core.Models.Track;

namespace AudioFool;

public partial class MainWindow : FluentWindow
{
    private readonly MainViewModel _viewModel;
    private readonly GlobalHotkeys _hotkeys = new();
    private ArtWindow? _artWindow;
    private string _typeAheadBuffer = "";
    private ListBox? _typeAheadTarget;
    private readonly DispatcherTimer _typeAheadTimer;

    public MainWindow(MainViewModel viewModel)
    {
        _viewModel = viewModel;
        DataContext = viewModel;

        InitializeComponent();

        FitToWorkArea();

        Loaded += OnLoaded;
        SourceInitialized += OnSourceInitialized;
        Closed += OnClosed;

        _typeAheadTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(800) };
        _typeAheadTimer.Tick += (_, _) =>
        {
            _typeAheadTimer.Stop();
            _typeAheadBuffer = "";
            _typeAheadTarget = null;
        };
        PreviewTextInput += OnTypeAheadInput;
        PreviewKeyDown += OnTypeAheadKeyDown;
    }

    /// <summary>
    /// The eleven track columns want a wide window, but the declared size can be
    /// larger than the display - especially over remote desktop, where the
    /// resolution can change while the app is running. Shrink to fit and centre.
    /// </summary>
    private void FitToWorkArea()
    {
        const double margin = 40;
        var work = SystemParameters.WorkArea;

        Width = Math.Min(Width, work.Width - margin);
        Height = Math.Min(Height, work.Height - margin);

        Left = work.Left + ((work.Width - Width) / 2);
        Top = work.Top + ((work.Height - Height) / 2);
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;
        await _viewModel.InitialiseAsync();
    }

    /// <summary>
    /// Claims the global playback keys. Done at SourceInitialized because the window
    /// needs a real HWND before Windows will register a hotkey against it.
    /// </summary>
    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        SourceInitialized -= OnSourceInitialized;

        if (!_viewModel.GlobalHotkeysEnabled)
            return;

        _hotkeys.Pressed += OnHotkeyPressed;
        _hotkeys.Register(this);
        _viewModel.ReportHotkeys(_hotkeys.Unavailable);
    }

    private void OnHotkeyPressed(object? sender, GlobalHotkeys.Action action)
    {
        switch (action)
        {
            case GlobalHotkeys.Action.PlayPause:
                _viewModel.TogglePlayCommand.Execute(null);
                break;

            // Previous already means "restart if past three seconds in, otherwise
            // step back a track" - see AudioEngine.Previous.
            case GlobalHotkeys.Action.Previous:
                _viewModel.PreviousCommand.Execute(null);
                break;

            case GlobalHotkeys.Action.Next:
                _viewModel.NextCommand.Execute(null);
                break;
        }
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        Closed -= OnClosed;
        _hotkeys.Pressed -= OnHotkeyPressed;
        _hotkeys.Dispose();
    }

    /// <summary>
    /// Scrolls a programmatic selection into view.
    /// <para>
    /// Setting <c>SelectedItem</c> highlights a row but does not move the scroll
    /// position, so re-selecting an artist after the search box is cleared would
    /// leave it selected somewhere off-screen among hundreds of others - which looks
    /// exactly like the selection having been lost.
    /// </para>
    /// </summary>
    private void BrowserList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is not ListBox list || list.SelectedItem is null)
            return;

        // Deferred: during a rebuild the item's container may not exist yet, and
        // ScrollIntoView silently does nothing without one.
        var target = list.SelectedItem;
        list.Dispatcher.BeginInvoke(
            () =>
            {
                if (!ReferenceEquals(list.SelectedItem, target))
                    return;

                // Scrolling to the end first, then back to the target, lands it near
                // the top of the viewport with the rest of the list below it. Going
                // straight there scrolls the minimum distance instead, leaving the
                // row flush against whichever edge it entered from.
                if (list.Items.Count > 0)
                    list.ScrollIntoView(list.Items[^1]);

                list.ScrollIntoView(target);
            },
            DispatcherPriority.Background);
    }

    private ListBox? GetTypeAheadTarget()
    {
        if (ArtistList.IsMouseOver) return ArtistList;
        if (AlbumList.IsMouseOver) return AlbumList;
        return null;
    }

    private void SelectTypeAheadMatch()
    {
        if (_typeAheadTarget == ArtistList)
        {
            var match = _viewModel.Artists.FirstOrDefault(a =>
                a.SortKey.StartsWith(_typeAheadBuffer, StringComparison.OrdinalIgnoreCase));
            if (match is not null)
                ArtistList.SelectedItem = match;
        }
        else if (_typeAheadTarget == AlbumList)
        {
            var match = _viewModel.Albums.FirstOrDefault(a =>
                a.Title.StartsWith(_typeAheadBuffer, StringComparison.OrdinalIgnoreCase));
            if (match is not null)
                AlbumList.SelectedItem = match;
        }
    }

    private void OnTypeAheadInput(object sender, TextCompositionEventArgs e)
    {
        var target = GetTypeAheadTarget();
        if (target is null || Keyboard.FocusedElement is TextBoxBase)
            return;

        if (_typeAheadTarget != target)
        {
            _typeAheadBuffer = "";
            _typeAheadTarget = target;
        }

        _typeAheadBuffer += e.Text;
        _typeAheadTimer.Stop();
        _typeAheadTimer.Start();
        SelectTypeAheadMatch();
        e.Handled = true;
    }

    private void OnTypeAheadKeyDown(object sender, KeyEventArgs e)
    {
        var target = GetTypeAheadTarget();
        if (target is null || Keyboard.FocusedElement is TextBoxBase)
            return;

        if (e.Key == Key.Back && _typeAheadBuffer.Length > 0 && _typeAheadTarget == target)
        {
            _typeAheadBuffer = _typeAheadBuffer[..^1];
            _typeAheadTimer.Stop();

            if (_typeAheadBuffer.Length > 0)
            {
                _typeAheadTimer.Start();
                SelectTypeAheadMatch();
            }
            else
            {
                _typeAheadTarget = null;
            }

            e.Handled = true;
        }
        else if (e.Key == Key.Escape && _typeAheadBuffer.Length > 0)
        {
            _typeAheadBuffer = "";
            _typeAheadTarget = null;
            _typeAheadTimer.Stop();
            e.Handled = true;
        }
    }

    // Border has no MouseDoubleClick of its own - that's a Control member - so the
    // click count gets checked by hand.

    private void AlbumArt_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount != 2)
            return;

        e.Handled = true;
        _ = ShowArtAsync(_viewModel.GetSelectedAlbumFullArtAsync(), _viewModel.SelectedAlbumCaption);
    }

    private void NowPlayingArt_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount != 2)
            return;

        e.Handled = true;
        _ = ShowArtAsync(_viewModel.GetNowPlayingFullArtAsync(), _viewModel.NowPlayingCaption);
    }

    /// <summary>
    /// Opens the enlarged view once the full-size decode finishes. Does nothing when
    /// there's no cover - an empty window is worse than no window.
    /// </summary>
    private async Task ShowArtAsync(Task<BitmapSource?> load, string caption)
    {
        var art = await load;
        if (art is null)
            return;

        // Reuse one viewer rather than stacking windows on repeated double-clicks.
        _artWindow?.Close();

        _artWindow = new ArtWindow(art, caption, this);
        _artWindow.Closed += (s, _) =>
        {
            if (ReferenceEquals(_artWindow, s))
                _artWindow = null;
        };

        _artWindow.Show();
    }

    private void TrackGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        // Only a double-click on an actual row should start playback - not one on
        // a column header or the empty space below the rows.
        if (e.OriginalSource is not DependencyObject source)
            return;

        if (ItemsControl.ContainerFromElement(TrackGrid, source) is DataGridRow { Item: Track track })
            _viewModel.PlayTrackCommand.Execute(track);
    }

    private void TrackGrid_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is not (Key.Enter or Key.Return))
            return;

        if (TrackGrid.SelectedItem is Track track)
        {
            _viewModel.PlayTrackCommand.Execute(track);
            e.Handled = true;
        }
    }

    // The seek bar is bound two-way, so the position timer and the user's drag
    // would otherwise fight over it. IsSeeking parks the timer mid-drag, and the
    // seek is only sent to the engine once the user lets go.

    private void SeekBar_DragStarted(object sender, DragStartedEventArgs e) =>
        _viewModel.IsSeeking = true;

    private void SeekBar_DragCompleted(object sender, DragCompletedEventArgs e)
    {
        _viewModel.IsSeeking = false;
        _viewModel.CommitSeek();
    }

    /// <summary>
    /// Catches a click on the slider track (IsMoveToPointEnabled), which jumps the
    /// value without ever raising a drag.
    /// </summary>
    private void SeekBar_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        _viewModel.IsSeeking = false;
        _viewModel.CommitSeek();
    }
}
