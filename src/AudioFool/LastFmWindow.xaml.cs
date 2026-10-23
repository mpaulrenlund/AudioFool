using System.Windows;
using AudioFool.ViewModels;
using Wpf.Ui.Controls;

namespace AudioFool;

/// <summary>
/// Connects AudioFool to Last.fm and shows what the scrobbler is doing. Modal
/// like the other dialogs. Closing it while waiting for the browser approval
/// abandons the attempt; the scrobbler itself lives on in the main view model.
/// </summary>
public partial class LastFmWindow : FluentWindow
{
    private readonly LastFmViewModel _viewModel;

    public LastFmWindow(LastFmViewModel viewModel, Window owner)
    {
        _viewModel = viewModel;
        DataContext = viewModel;

        InitializeComponent();

        Owner = owner;

        // Height depends on SizeToContent, so position once layout has run.
        Loaded += (_, _) => CenterOverOwner(owner);
        Closed += (_, _) => _viewModel.Dispose();
    }

    private void CenterOverOwner(Window owner) =>
        Services.DialogPlacement.CentreOverOwner(this, owner, Width, ActualHeight);

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
