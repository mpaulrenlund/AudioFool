using System.Windows;
using AudioFool.ViewModels;
using Wpf.Ui.Controls;

namespace AudioFool;

/// <summary>
/// Edits a single track's tags, or a whole album's shared tags and art, depending
/// on which <see cref="TagEditViewModel"/> constructor built the view model.
/// <para>
/// Shown modally (<c>ShowDialog</c>), unlike the inert <see cref="ArtWindow"/> -
/// this window triggers a real write to disk, and modal display makes it
/// structurally impossible for a second edit dialog or a background rescan to
/// race the same file while this one is still open.
/// </para>
/// </summary>
public partial class TagEditWindow : FluentWindow
{
    private readonly TagEditViewModel _viewModel;

    public TagEditWindow(TagEditViewModel viewModel, Window owner)
    {
        _viewModel = viewModel;
        DataContext = viewModel;

        InitializeComponent();

        Owner = owner;
        Title = viewModel.WindowTitle;

        // Height depends on SizeToContent, so position once layout has run.
        Loaded += (_, _) => CenterOverOwner(owner);
    }

    private void CenterOverOwner(Window owner)
    {
        var work = SystemParameters.WorkArea;

        var centreX = owner.Left + (owner.ActualWidth / 2);
        var centreY = owner.Top + (owner.ActualHeight / 2);

        Left = Math.Clamp(centreX - (Width / 2), work.Left, Math.Max(work.Left, work.Right - Width));
        Top = Math.Clamp(centreY - (ActualHeight / 2), work.Top, Math.Max(work.Top, work.Bottom - ActualHeight));
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (!_viewModel.CanSave)
            return;

        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private void SearchInternet_Click(object sender, RoutedEventArgs e)
    {
        var search = new ArtSearchViewModel(_viewModel.ArtSearch, _viewModel.SearchArtist, _viewModel.AlbumTitle.Trim());
        var window = new ArtSearchWindow(search, this);

        if (window.ShowDialog() == true && search.ChosenBytes is { } bytes)
            _viewModel.UseDownloadedArt(bytes);
    }
}
