using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Track = AudioFool.Core.Models.Track;

namespace AudioFool;

/// <summary>
/// Drag to reorder the songs of a playlist (2026-10-07). Press a row and move
/// past the system drag distance: the selected songs, or the pressed one, move
/// as a block to where a line shows between rows. Esc or losing the mouse
/// cancels. Only in a playlist shown in its own order (the user's call: a table
/// sorted by another column can't be dragged; clicking # puts the order back).
/// <para>
/// A plain click, double-click to play, the slow click that edits and the
/// heart all behave as before: nothing happens until the pointer moves.
/// </para>
/// </summary>
public partial class MainWindow
{
    /// <summary>A press on a row that may become a drag.</summary>
    private sealed class ReorderPress(Point start, Track pressed, DataGridColumn? column, bool deferred)
    {
        public Point Start { get; } = start;
        public Track Pressed { get; } = pressed;
        public DataGridColumn? Column { get; } = column;

        /// <summary>
        /// The press was on a row of a multiple selection and kept from the grid,
        /// which would otherwise have cut the selection down to that row at once.
        /// A click that doesn't become a drag does that on release instead.
        /// </summary>
        public bool Deferred { get; } = deferred;

        public List<Track>? Songs { get; set; }
        public int DropIndex { get; set; } = -1;
    }

    private ReorderPress? _reorder;
    private DropLineAdorner? _dropLine;
    private DispatcherTimer? _edgeScrollTimer;

    private bool IsDraggingSongs => _reorder?.Songs is not null;

    private void HookReorder()
    {
        TrackGrid.PreviewMouseMove += TrackGrid_ReorderMouseMove;
        TrackGrid.PreviewMouseLeftButtonUp += TrackGrid_ReorderMouseUp;
        TrackGrid.LostMouseCapture += TrackGrid_ReorderLostCapture;
        PreviewKeyDown += (_, e) =>
        {
            if (IsDraggingSongs && e.Key == Key.Escape)
            {
                EndReorder();
                _swallowPress = Mouse.LeftButton == MouseButtonState.Pressed;
                e.Handled = true;
            }
        };
    }

    /// <summary>Another window or app took the mouse mid-press: the drag is off.</summary>
    private void TrackGrid_ReorderLostCapture(object sender, MouseEventArgs e)
    {
        if (_reorder is not null && Mouse.Captured != TrackGrid)
            EndReorder();
    }

    /// <summary>
    /// Called from <see cref="TrackGrid_PreviewMouseLeftButtonDown"/> for a plain
    /// single press. Returns true when the press was kept from the grid.
    /// </summary>
    private bool BeginReorderPress(MouseButtonEventArgs e) =>
        BeginReorderPress(e.OriginalSource as DependencyObject, e.GetPosition(TrackGrid));

    private bool BeginReorderPress(DependencyObject? source, Point at)
    {
        _reorder = null;

        if (Keyboard.Modifiers != ModifierKeys.None
            || !_viewModel.IsPlaylistMode
            || TrackGrid.Items.SortDescriptions.Count > 0
            || source is null
            || FindAncestor<System.Windows.Controls.Primitives.TextBoxBase>(source) is not null
            || FindAncestor<DataGridRow>(source) is not { Item: Track track } row
            || !_viewModel.CanReorder(track))
            return false;

        var deferred = row.IsSelected && TrackGrid.SelectedItems.Count > 1;
        _reorder = new ReorderPress(at, track, FindAncestor<DataGridCell>(source)?.Column, deferred);

        if (!deferred)
            return false;

        TrackGrid.CaptureMouse();
        return true;
    }

    private void TrackGrid_ReorderMouseMove(object sender, MouseEventArgs e)
    {
        if (_swallowPress)
        {
            _swallowPress = e.LeftButton == MouseButtonState.Pressed;
            e.Handled = _swallowPress;
            return;
        }

        if (_reorder is null)
            return;

        if (e.LeftButton != MouseButtonState.Pressed)
        {
            EndReorder();
            return;
        }

        // Handled, it keeps the grid's own drag from selecting the rows passed over.
        e.Handled = ReorderMoveTo(e.GetPosition(TrackGrid));
    }

    /// <summary>The pointer moved to <paramref name="at"/> with the button down. True once a drag is under way.</summary>
    private bool ReorderMoveTo(Point at)
    {
        if (_reorder is not { } press)
            return false;

        if (press.Songs is null)
        {
            var moved = at - press.Start;
            if (Math.Abs(moved.X) < SystemParameters.MinimumHorizontalDragDistance
                && Math.Abs(moved.Y) < SystemParameters.MinimumVerticalDragDistance)
                return false;

            StartSongDrag(press);
        }

        UpdateDropLine(at);
        return true;
    }

    private void StartSongDrag(ReorderPress press)
    {
        _slowClickTimer.Stop();
        _slowClickCell = null;

        if (press.Deferred)
        {
            var selected = TrackGrid.SelectedItems.OfType<Track>().ToHashSet(ReferenceEqualityComparer.Instance);
            press.Songs = [.. _viewModel.Tracks.Where(selected.Contains)];
        }
        else
        {
            // The grid selected the pressed row on the press; a wobble across a
            // row edge before the drag started may have drag-selected another.
            press.Songs = [press.Pressed];
            if (TrackGrid.SelectedItems.Count != 1 || !ReferenceEquals(TrackGrid.SelectedItem, press.Pressed))
            {
                TrackGrid.SelectedItems.Clear();
                TrackGrid.SelectedItem = press.Pressed;
            }
        }

        if (Mouse.Captured != TrackGrid)
            TrackGrid.CaptureMouse();

        if (_dropLine is null && AdornerLayer.GetAdornerLayer(TrackGrid) is { } layer)
        {
            var n = Theming.TokenResources.Current!.Numbers;
            _dropLine = new DropLineAdorner(TrackGrid, (Brush)FindResource("color.text.primary"), n["songTable.dropLine.thickness"]);
            layer.Add(_dropLine);
        }

        _edgeScrollTimer ??= new DispatcherTimer(TimeSpan.FromMilliseconds(50), DispatcherPriority.Input, EdgeScroll_Tick, Dispatcher);
        _edgeScrollTimer.Start();
    }

    private void TrackGrid_ReorderMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_swallowPress)
        {
            _swallowPress = false;
            e.Handled = true;
            return;
        }

        // A drop isn't a click: nothing else should treat the release as one.
        e.Handled = ReorderRelease();
    }

    /// <summary>The button came up. True when it ended a drag.</summary>
    private bool ReorderRelease()
    {
        if (_reorder is not { } press)
            return false;

        if (press.Songs is { } songs)
        {
            var dropIndex = press.DropIndex;
            EndReorder();

            // The rows are rebuilt; the grid puts the selection and the current
            // cell back by path, so make the pressed song current to keep it in view.
            TrackGrid.CurrentCell = new DataGridCellInfo(press.Pressed, press.Column ?? SongColumn);
            if (!TrackGrid.IsKeyboardFocusWithin)
                TrackGrid.Focus();

            if (dropIndex >= 0)
                _viewModel.MovePlaylistSongs(songs, dropIndex);

            return true;
        }

        EndReorder();

        if (press.Deferred)
        {
            // A plain click on a row of a multiple selection: the selection
            // becomes that row, as the grid would have done on the press.
            TrackGrid.SelectedItems.Clear();
            TrackGrid.SelectedItem = press.Pressed;
            TrackGrid.CurrentCell = new DataGridCellInfo(press.Pressed, press.Column ?? SongColumn);
            if (FocusCell(press.Pressed, press.Column ?? SongColumn) is null)
                TrackGrid.Focus();
        }

        return false;
    }

    private void EndReorder()
    {
        var press = _reorder;
        _reorder = null;
        _edgeScrollTimer?.Stop();

        if (_dropLine is not null)
        {
            AdornerLayer.GetAdornerLayer(TrackGrid)?.Remove(_dropLine);
            _dropLine = null;
        }

        // A drag or a kept press holds the mouse; a plain press leaves it to the
        // grid, which lets go on any mouse up.
        if ((press?.Songs is not null || press?.Deferred == true) && Mouse.Captured == TrackGrid)
            TrackGrid.ReleaseMouseCapture();
    }

    /// <summary>
    /// After Esc, the rest of the press does nothing: the button is still down,
    /// and the grid would otherwise drag-select the rows it passes over.
    /// </summary>
    private bool _swallowPress;

    /// <summary>The rows area, in the table's coordinates.</summary>
    private Rect? RowsArea()
    {
        if (FindDescendant<ScrollViewer>(TrackGrid) is not { } viewer
            || viewer.Template?.FindName("PART_ScrollContentPresenter", viewer) is not FrameworkElement presenter
            || !presenter.IsVisible)
            return null;

        return presenter.TransformToAncestor(TrackGrid).TransformBounds(new Rect(presenter.RenderSize));
    }

    /// <summary>
    /// Works out where the songs would land under the pointer: before the first
    /// row whose middle is below it, or after the last. Draws the line there.
    /// </summary>
    private void UpdateDropLine(Point pointer)
    {
        if (_reorder is not { Songs: not null } press || RowsArea() is not { } area)
            return;

        var y = Math.Clamp(pointer.Y, area.Top, area.Bottom);

        var rows = new List<(int Index, Rect Bounds)>();
        foreach (var row in RealisedRows())
        {
            var index = TrackGrid.ItemContainerGenerator.IndexFromContainer(row);
            if (index >= 0)
                rows.Add((index, row.TransformToAncestor(TrackGrid).TransformBounds(new Rect(row.RenderSize))));
        }

        if (rows.Count == 0)
        {
            press.DropIndex = -1;
            return;
        }

        rows.Sort((a, b) => a.Index.CompareTo(b.Index));

        var (dropIndex, lineY) = (rows[^1].Index + 1, rows[^1].Bounds.Bottom);
        foreach (var (index, bounds) in rows)
        {
            if (y < bounds.Top + (bounds.Height / 2))
            {
                (dropIndex, lineY) = (index, bounds.Top);
                break;
            }
        }

        press.DropIndex = dropIndex;
        _dropLine?.Show(rows[0].Bounds.Left, rows[0].Bounds.Right, lineY, area);
    }

    private IEnumerable<DataGridRow> RealisedRows()
    {
        if (FindDescendant<System.Windows.Controls.Primitives.DataGridRowsPresenter>(TrackGrid) is not { } presenter)
            yield break;

        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(presenter); i++)
        {
            if (VisualTreeHelper.GetChild(presenter, i) is DataGridRow { IsVisible: true } row)
                yield return row;
        }
    }

    /// <summary>Scrolls a row at a time while the pointer is within a row of the top or bottom edge.</summary>
    private void EdgeScroll_Tick(object? sender, EventArgs e)
    {
        if (!IsDraggingSongs || RowsArea() is not { } area
            || FindDescendant<ScrollViewer>(TrackGrid) is not { } viewer)
            return;

        var zone = Theming.TokenResources.Current!.Numbers["songTable.rowHeight"];
        var y = Mouse.GetPosition(TrackGrid).Y;

        if (y < area.Top + zone && viewer.VerticalOffset > 0)
            viewer.LineUp();
        else if (y > area.Bottom - zone && viewer.VerticalOffset < viewer.ScrollableHeight)
            viewer.LineDown();
        else
            return;

        viewer.UpdateLayout();
        UpdateDropLine(Mouse.GetPosition(TrackGrid));
    }
}
