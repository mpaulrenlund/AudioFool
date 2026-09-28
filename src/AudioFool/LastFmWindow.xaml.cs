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

        // A Mica dialog in front of an opaque theme reads as a different app.
        WindowBackdropType = ThemeService.Backdrop;

        Owner = owner;

        // Height depends on SizeToContent, so position once layout has run.
        Loaded += (_, _) => CenterOverOwner(owner);
        Closed += (_, _) => _viewModel.Dispose();
    }

    private void CenterOverOwner(Window owner)
    {
        var work = SystemParameters.WorkArea;

        var centreX = owner.Left + (owner.ActualWidth / 2);
        var centreY = owner.Top + (owner.ActualHeight / 2);

        Left = Math.Clamp(centreX - (Width / 2), work.Left, Math.Max(work.Left, work.Right - Width));
        Top = Math.Clamp(centreY - (ActualHeight / 2), work.Top, Math.Max(work.Top, work.Bottom - ActualHeight));
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
