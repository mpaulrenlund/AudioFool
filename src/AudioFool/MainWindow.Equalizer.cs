using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
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
/// </summary>
public partial class MainWindow
{
    private void OnEqualizerChipClick(object sender, RoutedEventArgs e) => OpenEqualizerMenu();

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

    /// <summary>Right-click does nothing: the menu is the left click's, built fresh each time.</summary>
    private void HookEqualizerChip() =>
        EqualizerChip.ContextMenuOpening += (_, e) => e.Handled = true;
}
