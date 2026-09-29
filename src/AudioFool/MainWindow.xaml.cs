using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AudioFool.Core.Library;
using AudioFool.Services;
using AudioFool.ViewModels;
using Wpf.Ui.Controls;

// System.Windows.Controls.Primitives (needed for the slider's drag events) also
// defines a Track, which is the Slider's rail rather than a song.
using AudioFool.Core.Models;
using Track = AudioFool.Core.Models.Track;

namespace AudioFool;

public partial class MainWindow : FluentWindow
{
    private readonly MainViewModel _viewModel;
    private readonly GlobalHotkeys _hotkeys = new();
    private readonly TaskbarControls _taskbarControls;
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

        _taskbarControls = new TaskbarControls(this, viewModel);

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

        _slowClickTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(GetDoubleClickTime()) };
        _slowClickTimer.Tick += SlowClickTimer_Tick;
        TrackGrid.PreviewMouseWheel += (_, _) => _slowClickTimer.Stop();
        TrackGrid.LostKeyboardFocus += TrackGrid_LostKeyboardFocus;
        _viewModel.TracksChanging += OnTracksChanging;
    }

    /// <summary>
    /// The ten track columns want a wide window, but the declared size can be
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
        HookTrackGridScrollBar();
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
        _taskbarControls.Dispose();
        _slowClickTimer.Stop();
        _viewModel.TracksChanging -= OnTracksChanging;

        if (_trackGridScroller is not null)
            _trackGridScroller.ScrollChanged -= TrackGridScroller_ScrollChanged;
    }

    // ------------------------------------------------------- horizontal scrollbar

    /// <summary>
    /// Star-column layout leaves a few pixels of rounding overflow even when every
    /// column already fits the viewport - WPF reports the DataGrid as fractionally
    /// scrollable regardless of how wide the window is. That keeps the Auto
    /// horizontal scrollbar visible with nowhere real to scroll.
    /// <para>
    /// Anything within this tolerance counts as "fully displayed" and the bar is
    /// hidden. Genuine overflow - the floors in <see cref="AutoFitColumns"/> are
    /// allowed to exceed the viewport by tens of pixels on purpose - stays well
    /// above it and still shows the bar.
    /// </para>
    /// </summary>
    private const double HorizontalOverflowTolerance = 4;

    private ScrollViewer? _trackGridScroller;

    private void HookTrackGridScrollBar()
    {
        _trackGridScroller = FindDescendant<ScrollViewer>(TrackGrid);
        if (_trackGridScroller is not null)
            _trackGridScroller.ScrollChanged += TrackGridScroller_ScrollChanged;
    }

    private void TrackGridScroller_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        var scroller = (ScrollViewer)sender;
        scroller.HorizontalScrollBarVisibility = scroller.ScrollableWidth > HorizontalOverflowTolerance
            ? ScrollBarVisibility.Auto
            : ScrollBarVisibility.Hidden;
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

                // The album's tracks are already bound in by now (Tracks is filled
                // synchronously when SelectedAlbum changes), so refitting here means
                // every album's column widths reflect its own content - not whatever
                // the previously-viewed album happened to leave behind. Also covers
                // switching artists, since that reassigns SelectedAlbum too.
                if (ReferenceEquals(list, AlbumList))
                    AutoFitColumns();
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
        e.Handled = true;
        _ = ShowArtAsync(_viewModel.GetSelectedAlbumFullArtAsync(), _viewModel.SelectedAlbumCaption);
    }

    private void NowPlayingArt_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        _ = ShowArtAsync(_viewModel.GetNowPlayingFullArtAsync(), _viewModel.NowPlayingCaption);
    }

    private void NowPlayingInfo_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        _viewModel.NavigateToNowPlaying();
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

    /// <summary>
    /// Selects the row under the cursor before its context menu opens, since a
    /// DataGridRow (unlike a ListBoxItem) doesn't select itself on right-click.
    /// A row already in a multiple selection keeps the selection, so the menu
    /// edits all of it; any other row replaces the selection, as in Explorer.
    /// </summary>
    private void TrackGridRow_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not DataGridRow row || row.IsSelected)
            return;

        TrackGrid.SelectedItems.Clear();
        row.IsSelected = true;
    }

    private void TrackGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _slowClickTimer.Stop();
        _viewModel.SelectedTracks = TrackGrid.SelectedItems.OfType<Track>().ToList();
    }

    private void AlbumList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject source)
            return;

        if (ItemsControl.ContainerFromElement(AlbumList, source) is ListBoxItem { DataContext: AlbumItemViewModel album })
            _viewModel.PlayAlbumCommand.Execute(album);
    }

    private void ArtistList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject source)
            return;

        if (ItemsControl.ContainerFromElement(ArtistList, source) is ListBoxItem { DataContext: ArtistGroup artist })
            _viewModel.PlayArtistCommand.Execute(artist);
    }

    // ------------------------------------------------------- column auto-fit

    /// <summary>How close to a header's edge counts as grabbing its divider.</summary>
    private const double GripperReach = 6;

    /// <summary>
    /// A squeezed flexible column keeps at least this much, so it stays a column
    /// rather than disappearing to a sliver.
    /// </summary>
    private const double FlexFloor = 70;

    /// <summary>
    /// Double-clicking the divider between # and Song fits *every* column at once.
    /// <para>
    /// Hooked on the tunnelling PreviewMouseLeftButtonDown rather than a
    /// double-click event: DataGrid's own gripper handler sizes the single column
    /// it belongs to, and tunnelling is what lets this run - and mark the event
    /// handled - before that gets a look in. Every other divider keeps the stock
    /// one-column behaviour.
    /// </para>
    /// </summary>
    private void TrackGrid_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _slowClickTimer.Stop();

        if (e.OriginalSource is not DependencyObject source)
            return;

        if (e.ClickCount == 1)
        {
            ArmSlowClick(source);
            return;
        }

        if (e.ClickCount != 2)
            return;

        if (FindAncestor<DataGridColumnHeader>(source) is not { Column: { } column } header)
            return;

        var x = e.GetPosition(header).X;
        var onLeftEdge = x <= GripperReach;
        var onRightEdge = x >= header.ActualWidth - GripperReach;

        // The same divider is reachable from either side of it.
        var isFirstDivider = (column == TrackNumberColumn && onRightEdge)
                             || (column == SongColumn && onLeftEdge);

        if (!isFirstDivider)
            return;

        AutoFitColumns();
        e.Handled = true;
    }

    /// <summary>
    /// Fits every column to its content in one pass, resolving a shortage by
    /// priority rather than by truncating whatever happens to be on the right.
    /// <para>
    /// The narrow facts - Time, Disc, Kind, Bitrate, Bit Depth, Sample Rate, and
    /// the track number - are sized first and always get what they ask for; they
    /// are the columns whose headers are wider than their values, so clipping them
    /// costs a word rather than a character. What is left goes to Song, then
    /// Artist, then Album, each taking its full width only if the ones before it
    /// have been satisfied.
    /// </para>
    /// <para>
    /// Row virtualisation means "content" is the rows currently realised. Fitting
    /// to what is on screen is the useful answer, and the alternative - measuring
    /// several thousand rows - would stall the UI.
    /// </para>
    /// </summary>
    private void AutoFitColumns()
    {
        // The indicator column is a fixed 22 px by design: no header text to fit,
        // and one glyph's width is not what should decide it.
        var sizeable = TrackGrid.Columns.Where(c => c != IndicatorColumn).ToArray();
        if (sizeable.Length == 0)
            return;

        // Auto measures header and realised cells together, so this asks WPF what
        // each column would like, then takes the answer back.
        foreach (var column in sizeable)
            column.Width = new DataGridLength(1, DataGridLengthUnitType.Auto);

        TrackGrid.UpdateLayout();

        var desired = sizeable.ToDictionary(c => c, c => Math.Ceiling(c.ActualWidth) + 2);

        var viewport = FindDescendant<ScrollViewer>(TrackGrid)?.ViewportWidth ?? TrackGrid.ActualWidth;
        var available = viewport - IndicatorColumn.ActualWidth - 2;

        DataGridColumn[] mustFit =
        [
            TrackNumberColumn, TimeColumn, DiscColumn, KindColumn,
            BitrateColumn, BitDepthColumn, SampleRateColumn,
        ];

        // In priority order. Song is applied as a star column below, so it also
        // collects anything left over once the other two are satisfied.
        DataGridColumn[] flexible = [SongColumn, ArtistColumn, AlbumColumn];

        var left = available - mustFit.Sum(c => desired[c]);

        var given = new double[flexible.Length];
        for (var i = 0; i < flexible.Length; i++)
        {
            // Hold back a floor for each column still waiting behind this one.
            var heldBack = FlexFloor * (flexible.Length - 1 - i);
            var allowance = Math.Max(FlexFloor, left - heldBack);

            // The floor wins even when that overruns the width available. Three
            // legible columns behind a horizontal scrollbar beat three slivers.
            given[i] = Math.Min(desired[flexible[i]], allowance);
            left -= given[i];
        }

        foreach (var column in mustFit)
            column.Width = new DataGridLength(desired[column]);

        ArtistColumn.Width = new DataGridLength(given[1]);
        AlbumColumn.Width = new DataGridLength(given[2]);

        // Star, not the measured pixel width: Song takes every spare pixel and goes
        // on flexing when the window is resized, which is what it did before.
        SongColumn.Width = new DataGridLength(1, DataGridLengthUnitType.Star);
    }

    private static T? FindAncestor<T>(DependencyObject? node) where T : DependencyObject
    {
        while (node is not null and not T)
            node = VisualTreeHelper.GetParent(node);

        return node as T;
    }

    private static T? FindDescendant<T>(DependencyObject node) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
        {
            var child = VisualTreeHelper.GetChild(node, i);
            if (child is T match)
                return match;

            if (FindDescendant<T>(child) is { } deeper)
                return deeper;
        }

        return null;
    }

    private void TrackGrid_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is not (Key.Enter or Key.Return))
            return;

        if (e.OriginalSource is TextBoxBase)
        {
            SaveAndEditNextRow();
            e.Handled = true;
            return;
        }

        if (TrackGrid.SelectedItem is Track track)
        {
            _viewModel.PlayTrackCommand.Execute(track);
            e.Handled = true;
        }
    }

    // ------------------------------------------------------ in-place tag edits

    /// <summary>
    /// # / Song / Artist / Album can be edited in their cells, as a file is
    /// renamed in Explorer: F2, or a second single click on a row that was
    /// already the one selected. Nothing else opens an edit - not the grid's
    /// own click on a selected cell, which would open one on the first half of
    /// every double-click that plays a track, and not typing, which on a grid
    /// that looks like a list would quietly start rewriting a tag.
    /// </summary>
    private readonly DispatcherTimer _slowClickTimer;
    private DataGridCell? _slowClickCell;
    private bool _openingEdit;

    [DllImport("user32.dll")]
    private static extern uint GetDoubleClickTime();

    private InlineField? FieldFor(DataGridColumn? column) =>
        column == TrackNumberColumn ? InlineField.TrackNumber
        : column == SongColumn ? InlineField.Title
        : column == ArtistColumn ? InlineField.Artist
        : column == AlbumColumn ? InlineField.Album
        : null;

    /// <summary>
    /// A plain click on an editable cell of the one row already selected, with
    /// the grid already focused, opens an edit once the double-click time has
    /// passed without a second click - which would play the track instead.
    /// </summary>
    private void ArmSlowClick(DependencyObject source)
    {
        if (Keyboard.Modifiers != ModifierKeys.None
            || !TrackGrid.IsKeyboardFocusWithin
            || TrackGrid.SelectedItems.Count != 1
            || FindAncestor<DataGridCell>(source) is not { IsEditing: false } cell
            || FieldFor(cell.Column) is null
            || FindAncestor<DataGridRow>(cell) is not { IsSelected: true, Item: Track })
            return;

        _slowClickCell = cell;
        _slowClickTimer.Start();
    }

    private void SlowClickTimer_Tick(object? sender, EventArgs e)
    {
        _slowClickTimer.Stop();

        var cell = _slowClickCell;
        _slowClickCell = null;

        // Still the same row, still the only one selected, and the button up:
        // a held button is the start of a drag, not a click.
        if (cell is null || !cell.IsLoaded || Mouse.LeftButton == MouseButtonState.Pressed
            || TrackGrid.SelectedItems.Count != 1
            || !ReferenceEquals(cell.DataContext, TrackGrid.SelectedItem))
            return;

        OpenEdit(new DataGridCellInfo(cell));
    }

    private void OpenEdit(DataGridCellInfo cell)
    {
        TrackGrid.CurrentCell = cell;
        _openingEdit = true;
        try
        {
            TrackGrid.BeginEdit();
        }
        finally
        {
            _openingEdit = false;
        }
    }

    private void TrackGrid_BeginningEdit(object? sender, DataGridBeginningEditEventArgs e)
    {
        // F2 arrives through DataGrid.BeginEditCommand, with no input event;
        // the grid's own click and typing both carry theirs.
        var allowed = _openingEdit
            || (e.EditingEventArgs is null or KeyEventArgs { Key: Key.F2 } && TrackGrid.SelectedItems.Count <= 1);

        if (!allowed || FieldFor(e.Column) is null)
        {
            // Not stopping the slow click here: the grid's own attempt comes from
            // the very click that armed it, whenever that lands on the focused cell.
            e.Cancel = true;
            return;
        }

        _slowClickTimer.Stop();
    }

    private void TrackGrid_CellEditEnding(object? sender, DataGridCellEditEndingEventArgs e)
    {
        if (e.EditAction != DataGridEditAction.Commit
            || e.Row.Item is not Track track
            || e.EditingElement is not System.Windows.Controls.TextBox box
            || FieldFor(e.Column) is not { } field)
            return;

        // The column is bound one way - Track is immutable - so the typed text
        // goes no further than this box unless the save below replaces the row.
        _lastInlineSave = _viewModel.ApplyInlineEditAsync(track, field, box.Text);
    }

    private Task _lastInlineSave = Task.CompletedTask;

    /// <summary>The cell Enter moved to, opened once the save's rebuild has settled.</summary>
    private PendingEdit? _editNext;

    /// <summary>A cell to open for editing, with the text to put back if a rebuild interrupted it.</summary>
    private sealed record PendingEdit(string Path, DataGridColumn Column, TypedText? Typed = null);

    private sealed record TypedText(string Text, int SelectionStart, int SelectionLength);

    /// <summary>
    /// Enter saves the cell and opens the same field on the row below, so a
    /// column of track numbers can be typed straight down. Esc stops. The last
    /// row just saves.
    /// </summary>
    private void SaveAndEditNextRow()
    {
        if (TrackGrid.CurrentCell is not { Item: Track track, Column: { } column })
            return;

        var rows = TrackGrid.Items.OfType<Track>().ToList();
        var index = rows.IndexOf(track);

        _lastInlineSave = Task.CompletedTask;
        TrackGrid.CommitEdit(DataGridEditingUnit.Row, exitEditingMode: true);

        if (index < 0 || index + 1 >= rows.Count)
            return;

        // Taken before the save: a new track number can re-sort the edited row
        // elsewhere, and "next" is the track that was below it.
        var next = rows[index + 1];
        TrackGrid.SelectedItems.Clear();
        TrackGrid.SelectedItem = next;
        TrackGrid.CurrentCell = new DataGridCellInfo(next, column);
        FocusCell(next, column);

        // A save rebuilds the rows, which would cancel an edit opened now, so it
        // opens after the rebuild (RestoreGridPosition). A save that writes
        // nothing, or fails, never rebuilds; its finishing opens it instead.
        // Each Enter has its own target, so a slow earlier save finishing late
        // cannot open a later one before that one's rebuild.
        var pending = new PendingEdit(next.FilePath, column);
        _editNext = pending;
        var save = _lastInlineSave;
        if (save.IsCompleted)
            Dispatcher.BeginInvoke(() => OpenPendingEdit(pending), DispatcherPriority.Loaded);
        else
            save.ContinueWith(_ => Dispatcher.BeginInvoke(() => OpenPendingEdit(pending), DispatcherPriority.Loaded),
                TaskScheduler.Default);
    }

    /// <param name="only">Open only if this is still the pending edit.</param>
    /// <param name="hadFocus">
    /// Whether the grid had the keyboard; a rebuild passes what it saw before
    /// clearing the rows, since clearing them takes the focus away.
    /// </param>
    private void OpenPendingEdit(PendingEdit? only = null, bool? hadFocus = null)
    {
        if (_editNext is not { } target || _gridRestore is not null
            || (only is not null && !ReferenceEquals(only, target)))
            return;

        _editNext = null;

        var item = TrackGrid.Items.OfType<Track>().FirstOrDefault(t =>
            string.Equals(t.FilePath, target.Path, StringComparison.OrdinalIgnoreCase));

        // Gone (another album was chosen meanwhile), or the user has clicked
        // away from the grid.
        if (item is null || !(hadFocus ?? TrackGrid.IsKeyboardFocusWithin))
            return;

        // Selected again rather than trusted: after a commit the grid can hand
        // focus, and with it the selection, back to the row just edited.
        if (TrackGrid.SelectedItems.Count != 1 || !ReferenceEquals(TrackGrid.SelectedItem, item))
        {
            TrackGrid.SelectedItems.Clear();
            TrackGrid.SelectedItem = item;
        }

        TrackGrid.CurrentCell = new DataGridCellInfo(item, target.Column);
        FocusCell(item, target.Column);
        OpenEdit(new DataGridCellInfo(item, target.Column));

        // The grid settles its own focus on the rebuilt rows after this, which
        // leaves the box open but unfocused; hand it the keyboard once that's done.
        // The cell is looked up again then: straight after a rebuild its row may
        // not have been realised yet, which is also why this can take a retry.
        var typedApplied = false;
        void FocusBox(bool retry)
        {
            if (CellFor(item, target.Column) is not { IsEditing: true } cell
                || FindDescendant<System.Windows.Controls.TextBox>(cell) is not { } box)
            {
                if (retry)
                    Dispatcher.BeginInvoke(() => FocusBox(retry: false), DispatcherPriority.ApplicationIdle);
                return;
            }

            if (!box.IsKeyboardFocused)
                box.Focus();

            if (!typedApplied)
            {
                typedApplied = true;
                if (target.Typed is { } typed)
                {
                    box.Text = typed.Text;
                    box.Select(typed.SelectionStart, typed.SelectionLength);
                }
                else
                {
                    box.SelectAll();
                }
            }

            if (retry && !box.IsKeyboardFocused)
                Dispatcher.BeginInvoke(() => FocusBox(retry: false), DispatcherPriority.ApplicationIdle);
        }

        Dispatcher.BeginInvoke(() => FocusBox(retry: true), DispatcherPriority.ContextIdle);
    }

    private DataGridCell? CellFor(Track item, DataGridColumn column) =>
        TrackGrid.ItemContainerGenerator.ContainerFromItem(item) is DataGridRow row
        && column.GetCellContent(row)?.Parent is DataGridCell cell
            ? cell
            : null;

    private DataGridCell? FocusCell(Track item, DataGridColumn column)
    {
        TrackGrid.ScrollIntoView(item, column);
        TrackGrid.UpdateLayout();

        var cell = CellFor(item, column);
        cell?.Focus();
        return cell;
    }

    /// <summary>
    /// Clicking away from the grid saves an open edit, as leaving a rename box
    /// does; the grid itself would leave it open.
    /// </summary>
    private void TrackGrid_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        // No new focus is the window being switched away from; the edit waits.
        if (e.NewFocus is not DependencyObject next || IsInside(next, TrackGrid) || next is ContextMenu || next is System.Windows.Controls.MenuItem)
            return;

        TrackGrid.CommitEdit(DataGridEditingUnit.Row, exitEditingMode: true);
    }

    private static bool IsInside(DependencyObject node, DependencyObject ancestor)
    {
        for (var current = node; current is not null; current = VisualTreeHelper.GetParent(current))
        {
            if (ReferenceEquals(current, ancestor))
                return true;
        }

        return false;
    }

    /// <summary>
    /// The rows are about to be cleared, which the grid cannot do under an open
    /// edit. So an open edit is cancelled and reopened, half-typed text and all,
    /// once the rows are back: a scan or an earlier save landing mid-edit costs
    /// nothing. Choosing another album saves it first, because the click takes
    /// focus off the grid.
    /// </summary>
    private void OnTracksChanging(object? sender, EventArgs e)
    {
        _slowClickTimer.Stop();

        if (TrackGrid.CurrentCell is { Item: Track editing, Column: { } editColumn }
            && CellFor(editing, editColumn) is { IsEditing: true } editCell
            && FindDescendant<System.Windows.Controls.TextBox>(editCell) is { } editBox)
        {
            _editNext = new PendingEdit(editing.FilePath, editColumn,
                new TypedText(editBox.Text, editBox.SelectionStart, editBox.SelectionLength));
        }

        TrackGrid.CancelEdit(DataGridEditingUnit.Row);

        // A save rebuilds the rows too, which would drop the selection and the
        // current column: Enter's move to the next row, and the row a slow click
        // needs selected. Put them back once the rows are refilled. A rebuild can
        // clear the list twice, so the first snapshot is the one kept.
        if (_gridRestore is not null)
            return;

        _gridRestore = new GridPosition(
            TrackGrid.SelectedItems.OfType<Track>().Select(t => t.FilePath).ToHashSet(StringComparer.OrdinalIgnoreCase),
            (TrackGrid.CurrentCell.Item as Track)?.FilePath,
            TrackGrid.CurrentCell.Column,
            TrackGrid.IsKeyboardFocusWithin);

        Dispatcher.BeginInvoke(RestoreGridPosition, DispatcherPriority.Loaded);
    }

    private sealed record GridPosition(
        HashSet<string> Selected, string? CurrentPath, DataGridColumn? CurrentColumn, bool HadFocus);

    private GridPosition? _gridRestore;

    /// <summary>
    /// Reselects the rows and current cell recorded by <see cref="OnTracksChanging"/>,
    /// matched by file path since a save replaces the Track objects. Another
    /// album's rows match nothing, so choosing one is unaffected.
    /// </summary>
    private void RestoreGridPosition()
    {
        var saved = _gridRestore;
        _gridRestore = null;

        if (saved is null)
            return;

        RestoreSelection(saved);

        // An edit Enter moved to, or one the rebuild interrupted, reopens now.
        OpenPendingEdit(hadFocus: saved.HadFocus);
    }

    private void RestoreSelection(GridPosition saved)
    {
        var rows = TrackGrid.Items.OfType<Track>().ToList();
        var selected = rows.Where(t => saved.Selected.Contains(t.FilePath)).ToList();
        if (selected.Count == 0)
            return;

        TrackGrid.SelectedItems.Clear();
        foreach (var track in selected)
            TrackGrid.SelectedItems.Add(track);

        var current = rows.FirstOrDefault(t =>
                          string.Equals(t.FilePath, saved.CurrentPath, StringComparison.OrdinalIgnoreCase))
                      ?? selected[0];
        var column = saved.CurrentColumn ?? SongColumn;

        TrackGrid.CurrentCell = new DataGridCellInfo(current, column);
        TrackGrid.ScrollIntoView(current, column);

        if (!saved.HadFocus)
            return;

        // Focusing the cell is what lets F2 and the slow click carry on.
        if (FocusCell(current, column) is null)
            TrackGrid.Focus();
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
