using System.Windows;
using AudioFool.ViewModels;
using Wpf.Ui.Controls;

namespace AudioFool;

/// <summary>
/// Edits a single track's tags, or a whole album's shared tags and art, depending
/// on which <see cref="TagEditViewModel"/> constructor built the view model.
/// <para>
/// The several-tracks edit is shown modally (<c>ShowDialog</c>): it writes to
/// disk, and modal display keeps a second edit or a rescan from racing the same
/// files while it is open.
/// </para>
/// <para>
/// The album and single-song edits are not (<see cref="FollowsSelection"/>): each
/// is shown beside the main window and follows the Albums list or the Songs
/// table, so the user can walk through albums or songs with it open. Its owner
/// (<see cref="MainViewModel"/>) gives it a new view model for each one, asks
/// before leaving unsaved changes behind, and looks the tracks up again when
/// Save is pressed, since the library may have changed underneath it.
/// </para>
/// </summary>
public partial class TagEditWindow : FluentWindow
{
    private TagEditViewModel _viewModel;

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

    /// <summary>
    /// The album edit, shown with <c>Show</c>: Save raises <see cref="SaveRequested"/>
    /// and keeps the window open, and the other button closes it.
    /// </summary>
    public bool FollowsSelection
    {
        get;
        init
        {
            field = value;
            CancelButton.Content = value ? "Close" : "Cancel";
        }
    }

    /// <summary>Save was pressed on a <see cref="FollowsSelection"/> window with nothing invalid.</summary>
    public event EventHandler? SaveRequested;

    public TagEditViewModel ViewModel => _viewModel;

    /// <summary>Shows another album (or the same one, read again) in place.</summary>
    public void SetViewModel(TagEditViewModel viewModel)
    {
        _viewModel = viewModel;
        DataContext = viewModel;
        Title = viewModel.WindowTitle;
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

        if (FollowsSelection)
            SaveRequested?.Invoke(this, EventArgs.Empty);
        else
            DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        if (FollowsSelection)
            Close();
        else
            DialogResult = false;
    }

    private void SearchInternet_Click(object sender, RoutedEventArgs e)
    {
        var search = new ArtSearchViewModel(_viewModel.ArtSearch, _viewModel.SearchArtist, _viewModel.AlbumTitle.Trim());
        var window = new ArtSearchWindow(search, this);

        if (window.ShowDialog() == true && search.ChosenBytes is { } bytes)
            _viewModel.UseDownloadedArt(bytes);
    }
}
