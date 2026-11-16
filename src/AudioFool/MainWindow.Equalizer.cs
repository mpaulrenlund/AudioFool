using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using AudioFool.Core.Equalizer;
using AudioFool.Core.Settings;
using AudioFool.ViewModels;

namespace AudioFool;

/// <summary>
/// The status bar's EQ chip (session 79, the user's design): a click opens the
/// preset menu above it, built afresh each time from the files as they are now.
/// Off, then Peace's presets, then AudioFool's own folder's (a line between each
/// group), a check on the one in use, then Open EQ Folder… and, when Peace is
/// installed, Open Peace…. An edit to the preset in use is taken up as the menu
/// opens (<see cref="MainViewModel.TakeUpEqualizerEdit"/>). The menus' own
/// style and corners apply (Chrome.xaml, <see cref="Theming.MenuCorners"/>).
/// <para>
/// A double-click toggles the equalizer (session 80, the user's request): off when
/// on, the last preset used when off (<see cref="MainViewModel.ToggleEqualizer"/>).
/// The first click opens the menu at once, as a single click does; the second
/// press then lands while the menu has the mouse, so it's caught on the menu:
/// within the double-click time of the first and on the chip, it closes the menu
/// and toggles. A double-click on the chip with no menu open (ClickCount 2) does
/// the same.
/// </para>
/// </summary>
public partial class MainWindow
{
    // When the chip was last pressed (Environment.TickCount64), to tell a double-click.
    private long _equalizerChipPressedAt = long.MinValue / 2;

    private void OnEqualizerChipClick(object sender, RoutedEventArgs e) => OpenEqualizerMenu();

    private void OnEqualizerChipPress(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2 && IsEqualizerDoubleClick(e))
        {
            e.Handled = true;
            ToggleEqualizerFromChip();
            return;
        }
        _equalizerChipPressedAt = Environment.TickCount64;
    }

    /// <summary>The menu has the mouse: a second press on the chip, soon enough, is the double-click's.</summary>
    private void OnEqualizerMenuPress(object sender, MouseButtonEventArgs e)
    {
        if (sender is not ContextMenu menu || !IsEqualizerDoubleClick(e))
            return;

        e.Handled = true;
        menu.IsOpen = false;
        ToggleEqualizerFromChip();
    }

    /// <summary>
    /// A press on the chip within Windows' double-click time of the last one, and
    /// something to toggle: on, or a last preset to turn on. With neither, the press
    /// is left alone (it closes the menu, as any click outside it does).
    /// </summary>
    private bool IsEqualizerDoubleClick(MouseButtonEventArgs e)
    {
        var onChip = new Rect(EqualizerChip.RenderSize).Contains(e.GetPosition(EqualizerChip));
        var soon = Environment.TickCount64 - _equalizerChipPressedAt <= GetDoubleClickTime();
        return onChip && soon && _viewModel.CanToggleEqualizer;
    }

    private void ToggleEqualizerFromChip()
    {
        _equalizerChipPressedAt = long.MinValue / 2; // a third click starts afresh
        _viewModel.ToggleEqualizer();
    }

    private void OpenEqualizerMenu()
    {
        if (DataContext is not MainViewModel vm)
            return;

        var presets = vm.LoadEqualizerMenu();
        vm.TakeUpEqualizerEdit(presets);
        var menu = new ContextMenu
        {
            PlacementTarget = EqualizerChip,
            Placement = PlacementMode.Top,
        };
        menu.PreviewMouseLeftButtonDown += OnEqualizerMenuPress;

        menu.Items.Add(EqualizerItem("Off", !vm.IsEqualizerOn, () => vm.ChooseEqualizer(null, null)));

        void AddGroup(IReadOnlyList<EqPreset> group, string folder)
        {
            if (group.Count == 0)
                return;
            menu.Items.Add(new Separator());
            foreach (var preset in group)
                menu.Items.Add(EqualizerItem(preset.Name, vm.IsEqualizerInUse(preset, folder),
                    () => vm.ChooseEqualizer(preset, folder)));
        }

        AddGroup(presets.Peace, AppSettings.EqualizerInPeace);
        AddGroup(presets.Own, AppSettings.EqualizerInOwnFolder);

        menu.Items.Add(new Separator());
        // A folder icon in the box's place, as the Libraries menu's Add Folder…
        // has, so its text lines up with the presets'.
        var openFolder = EqualizerItem("Open EQ Folder…", false, vm.OpenEqualizerFolder, checkable: false);
        openFolder.Icon = new Wpf.Ui.Controls.SymbolIcon { Symbol = Wpf.Ui.Controls.SymbolRegular.FolderOpen24 };
        menu.Items.Add(openFolder);

        // Peace, to edit or add presets (the user's request, session 79); only
        // when it's installed.
        if (MainViewModel.PeaceExecutable is not null)
        {
            var openPeace = EqualizerItem("Open Peace…", false, vm.OpenPeace, checkable: false);
            openPeace.Icon = new Wpf.Ui.Controls.SymbolIcon { Symbol = Wpf.Ui.Controls.SymbolRegular.Open24 };
            menu.Items.Add(openPeace);
        }

        // On the chip, so the menu finds its resources through it, as the
        // other context menus do through their owners.
        EqualizerChip.ContextMenu = menu;
        menu.IsOpen = true;
    }

    private static MenuItem EqualizerItem(string header, bool isChecked, Action choose, bool checkable = true)
    {
        // A preset's name is its file's: an underscore in it is a character, not
        // an access key. WPF-UI draws the tick only on a checkable item (measured:
        // IsChecked alone draws nothing), in a box on every checkable row, as the
        // Libraries menu shows its folders. A click flipping it is harmless, as
        // the menu is built afresh each time it opens.
        var item = new MenuItem
        {
            Header = header.Replace("_", "__"),
            IsCheckable = checkable,
            IsChecked = isChecked,
        };
        item.Click += (_, _) => choose();
        return item;
    }

    /// <summary>Right-click does nothing: the menu is the left click's, built fresh each time. A press is timed for the double-click.</summary>
    private void HookEqualizerChip()
    {
        EqualizerChip.ContextMenuOpening += (_, e) => e.Handled = true;
        EqualizerChip.PreviewMouseLeftButtonDown += OnEqualizerChipPress;
    }

    /// <summary>
    /// The album header's EQ panel (session 80): laid out again when the header's
    /// width changes or what it shows does; the preset in use read again from its
    /// file each time AudioFool is switched back to, so an edit saved in Peace shows
    /// (and is heard) at once.
    /// </summary>
    private void HookEqualizerPanel()
    {
        HeaderGrid.SizeChanged += (_, e) =>
        {
            if (e.WidthChanged)
                UpdateEqualizerPanel();
        };
        _viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(MainViewModel.EqualizerGraph)
                or nameof(MainViewModel.CanOpenEqualizerInPeace)
                or nameof(MainViewModel.ShowsAlbumHeader)
                or nameof(MainViewModel.ShowsPlaylistHeader))
                UpdateEqualizerPanel();
        };
        Activated += (_, _) => _viewModel.TakeUpEqualizerEditFromFile();
        UpdateEqualizerPanel();
    }

    /// <summary>
    /// Shows the panel while an EQ is on and a header shows, beside it (spec 6.5,
    /// <i>EQ panel</i>). It takes <c>eqPanel.width</c>, or what the header has left
    /// once the art and <c>eqPanel.titleRoom</c> for the title are given theirs.
    /// The band numbers show when each gets <c>eqPanel.minColumnWidth</c>; otherwise
    /// it is compact, three frequencies and a taller graph; under
    /// <c>eqPanel.compactMinWidth</c> it is hidden.
    /// </summary>
    private void UpdateEqualizerPanel()
    {
        var graph = _viewModel.EqualizerGraph;
        var header = _viewModel.ShowsAlbumHeader || _viewModel.ShowsPlaylistHeader;
        if (graph is null || !header)
        {
            EqualizerPanel.Visibility = Visibility.Collapsed;
            return;
        }

        double Token(string key) => TryFindResource(key) is double value
            ? value
            : throw new InvalidOperationException($"{key} is missing from the token set.");

        // The header's left padding, the art and its gap, the title's room, the
        // album grid's right padding (the gap before the panel), the panel's own.
        var padding = Token("albumHeader.paddingX");
        var available = HeaderGrid.ActualWidth - 3 * padding
            - Token("albumHeader.artSize") - Token("albumHeader.artGap") - Token("eqPanel.titleRoom");
        var width = Math.Min(Token("eqPanel.width"), available);
        if (width < Token("eqPanel.compactMinWidth"))
        {
            EqualizerPanel.Visibility = Visibility.Collapsed;
            return;
        }

        var plot = width - Token("eqPanel.axisWidth") - Token("eqPanel.axisGap");
        var compact = graph.Bands.Count == 0 || plot / graph.Bands.Count < Token("eqPanel.minColumnWidth");

        EqualizerPanel.Width = width;
        EqualizerGraphView.Compact = compact;
        EqualizerGraphView.Height = Token(compact ? "eqPanel.compactGraphHeight" : "eqPanel.graphHeight");
        EqualizerScale.Height = EqualizerGraphView.Height;
        EqualizerBands.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        EqualizerFrequencies.Visibility = compact ? Visibility.Visible : Visibility.Collapsed;
        EqualizerFrequencies.InsetX = EqualizerGraphView.InsetX;
        EqualizerPanel.Cursor = _viewModel.CanOpenEqualizerInPeace ? Cursors.Hand : null;
        EqualizerPanel.Visibility = Visibility.Visible;
    }

    /// <summary>A click on the EQ panel opens Peace, to edit the preset (the user's call, session 80).</summary>
    private void EqualizerPanel_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel.CanOpenEqualizerInPeace)
            _viewModel.OpenPeace();
    }
}
