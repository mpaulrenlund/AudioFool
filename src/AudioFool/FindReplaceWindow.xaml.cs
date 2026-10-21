using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using AudioFool.ViewModels;
using Wpf.Ui.Controls;

namespace AudioFool;

/// <summary>
/// Find and Replace over the library (session 63). Modal, like Statistics: the
/// rows are a snapshot of the library as Find saw it, and nothing else edits the
/// songs while the window is open. Closing while a replace or an undo saves
/// waits for it, then closes.
/// </summary>
public partial class FindReplaceWindow : FluentWindow
{
    private readonly FindReplaceViewModel _viewModel;
    private bool _closeWhenDone;

    public FindReplaceWindow(FindReplaceViewModel viewModel, Window owner)
    {
        _viewModel = viewModel;
        DataContext = viewModel;

        InitializeComponent();
        Services.OwnerActivation.Attach(this);

        Owner = owner;
        ApplyTableLayout();

        var work = WorkAreaOf(owner);
        Width = OpeningWidth(OwnerFrame(owner, work), work, MinWidth,
            Theming.TokenResources.Current!.Numbers["findReplace.ownerMarginX"]);

        viewModel.PropertyChanged += OnViewModelChanged;
        Loaded += (_, _) =>
        {
            var area = WorkAreaOf(owner);
            var corner = CentreOver(OwnerFrame(owner, area), area, ActualWidth, ActualHeight);
            Left = corner.X;
            Top = corner.Y;
            FindBox.Focus();
        };
    }

    /// <summary>
    /// The width it opens at (the user's call, session 63): the main window's,
    /// less findReplace.ownerMarginX either side, so the long FILE paths have room
    /// and the main window shows on both sides. No narrower than
    /// <paramref name="minWidth"/>, no wider than the work area.
    /// </summary>
    internal static double OpeningWidth(Rect frame, Rect work, double minWidth, double margin) =>
        Math.Min(Math.Max(minWidth, frame.Width - (2 * margin)), work.Width);

    /// <summary>The top-left corner that centres a window of this size over <paramref name="frame"/>, kept on the work area.</summary>
    internal static Point CentreOver(Rect frame, Rect work, double width, double height) =>
        new(Math.Clamp(frame.Left + ((frame.Width - width) / 2), work.Left, Math.Max(work.Left, work.Right - width)),
            Math.Clamp(frame.Top + ((frame.Height - height) / 2), work.Top, Math.Max(work.Top, work.Bottom - height)));

    /// <summary>
    /// Where the main window is on screen. Maximised, that is its monitor's work
    /// area: its Left and Top then say where it would restore to, and a window this
    /// wide centred on them could sit against one edge. (The other dialogs still
    /// centre on them; it is on session 60's list.)
    /// </summary>
    private static Rect OwnerFrame(Window owner, Rect work) =>
        owner.WindowState == WindowState.Maximized
            ? work
            : new Rect(owner.Left, owner.Top, owner.ActualWidth, owner.ActualHeight);

    /// <summary>The work area of the main window's monitor, in WPF's units; the primary monitor's if it can't be had.</summary>
    private static Rect WorkAreaOf(Window owner)
    {
        if (Services.WindowPlacement.WorkAreaOf(owner) is not { } area)
            return SystemParameters.WorkArea;

        var dpi = VisualTreeHelper.GetDpi(owner);
        return new Rect(area.Left / dpi.DpiScaleX, area.Top / dpi.DpiScaleY,
                        (area.Right - area.Left) / dpi.DpiScaleX, (area.Bottom - area.Top) / dpi.DpiScaleY);
    }

    /// <summary>
    /// The Songs table's paddings and cell styles, and this table's column widths:
    /// a fixed column is its token width plus a gap, as there, and BEFORE, AFTER
    /// and FILE share the rest by their flex values.
    /// </summary>
    private void ApplyTableLayout()
    {
        var n = Theming.TokenResources.Current!.Numbers;
        var gap = n["songTable.columnGap"];
        Theming.SongTableLayout.ApplyPaddings(PreviewGrid);

        TickColumn.Width = new DataGridLength(n["dialog.checkBoxSize"] + gap);
        FieldColumn.Width = new DataGridLength(n["findReplace.fieldWidth"] + gap);
        foreach (var (column, flex) in new[] { (BeforeColumn, "beforeFlex"), (AfterColumn, "afterFlex"), (FileColumn, "fileFlex") })
        {
            column.Width = new DataGridLength(n[$"findReplace.{flex}"], DataGridLengthUnitType.Star);
            column.MinWidth = Theming.SongTableLayout.FlexFloor + gap;
        }

        var header = (Style)FindResource("theme.songHeader");
        var cell = (Style)FindResource("theme.songCell");
        var text = (Style)FindResource("theme.songText");
        foreach (var column in PreviewGrid.Columns)
        {
            column.CellStyle = cell;
            column.HeaderStyle = header;
            if (column is DataGridTextColumn textColumn)
                textColumn.ElementStyle = text;
        }

        // Its header is the tick box itself, not a label to be drawn as text.
        TickColumn.HeaderStyle = new Style(typeof(DataGridColumnHeader), header)
        {
            Setters = { new Setter(ContentControl.ContentTemplateProperty, null) },
        };
    }

    /// <summary>Space ticks or unticks the selected rows together, the way the focused one goes.</summary>
    private void PreviewGrid_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Space || Keyboard.Modifiers != ModifierKeys.None)
            return;

        var selected = PreviewGrid.SelectedItems.OfType<ReplaceRow>().ToList();
        if (selected.Count == 0)
            return;

        var lead = PreviewGrid.CurrentItem is ReplaceRow current && selected.Contains(current) ? current : selected[0];
        _viewModel.SetTicked(selected, !lead.IsTicked);
        e.Handled = true;
    }

    /// <summary>
    /// A press on a row only half in view doesn't scroll it in, so the release
    /// lands on the tick box that was pressed (as in the main window's lists,
    /// session 59). The keys still scroll.
    /// </summary>
    private void Row_RequestBringIntoView(object sender, RequestBringIntoViewEventArgs e)
    {
        if (Mouse.LeftButton == MouseButtonState.Pressed)
            e.Handled = true;
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    protected override void OnClosing(CancelEventArgs e)
    {
        if (_viewModel.IsBusy)
        {
            e.Cancel = true;
            _closeWhenDone = true;
        }

        base.OnClosing(e);
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(FindReplaceViewModel.IsBusy) && !_viewModel.IsBusy && _closeWhenDone)
            Dispatcher.InvokeAsync(Close);
    }

    protected override void OnClosed(EventArgs e)
    {
        _viewModel.PropertyChanged -= OnViewModelChanged;
        base.OnClosed(e);
    }
}
