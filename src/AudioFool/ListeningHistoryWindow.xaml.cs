using System.Windows;
using AudioFool.ViewModels;
using Wpf.Ui.Controls;

namespace AudioFool;

/// <summary>
/// Listening History (Ctrl+Y): the user's Last.fm totals, top lists for a
/// chosen period and recent plays, in Statistics' layout. Read-only.
/// <para>
/// Modal like <see cref="StatisticsWindow"/>, and as tall as the screen allows
/// from the start: the answer arrives after the window opens, so it can't be
/// sized to its content. Escape or Close dismisses it; a click on a row the
/// library holds closes it with that row as the answer.
/// </para>
/// </summary>
public partial class ListeningHistoryWindow : FluentWindow
{
    private readonly ListeningHistoryViewModel _viewModel;

    public ListeningHistoryWindow(ListeningHistoryViewModel viewModel, Window owner)
    {
        _viewModel = viewModel;
        DataContext = viewModel;

        InitializeComponent();

        Owner = owner;
        Height = Services.DialogPlacement.WorkAreaOf(owner).Height;

        Loaded += async (_, _) =>
        {
            Services.DialogPlacement.CentreOverOwner(this, owner, ActualWidth, ActualHeight);
            await _viewModel.LoadAsync();
        };
    }

    private void Row_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: ListeningRow { IsClickable: true } row })
            return;

        _viewModel.Chosen = row;
        DialogResult = true;
    }

    private void OpenLastFm_Click(object sender, RoutedEventArgs e)
    {
        _viewModel.WantsLastFm = true;
        DialogResult = true;
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
