using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using AudioFool.ViewModels;
using Wpf.Ui.Controls;

namespace AudioFool;

/// <summary>
/// Finds a cover online for the album tag dialog. Modal over that dialog; the
/// chosen JPEG comes back through <see cref="ArtSearchViewModel.ChosenBytes"/>
/// and is only written when the tag dialog itself is saved.
/// </summary>
public partial class ArtSearchWindow : FluentWindow
{
    private readonly ArtSearchViewModel _viewModel;

    public ArtSearchWindow(ArtSearchViewModel viewModel, Window owner)
    {
        _viewModel = viewModel;
        DataContext = viewModel;

        InitializeComponent();

        Owner = owner;

        Loaded += (_, _) => _viewModel.SearchCommand.Execute(null);
        Closed += (_, _) => _viewModel.Cancel();
    }

    private void Query_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && _viewModel.SearchCommand.CanExecute(null))
        {
            _viewModel.SearchCommand.Execute(null);
            e.Handled = true;
        }
    }

    private void ResultList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        // Only a double-click on a cover, not on the empty space around them.
        if (ItemsControl.ContainerFromElement(ResultList, (DependencyObject)e.OriginalSource) is ListBoxItem)
            _ = UseAsync();
    }

    private void Use_Click(object sender, RoutedEventArgs e) => _ = UseAsync();

    private async Task UseAsync()
    {
        if (!_viewModel.CanUse)
            return;

        if (await _viewModel.DownloadSelectedAsync())
            DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
