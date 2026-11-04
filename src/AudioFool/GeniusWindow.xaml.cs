using System.Windows;
using AudioFool.Formatting;
using AudioFool.Services;
using AudioFool.ViewModels;
using Wpf.Ui.Controls;

using Track = AudioFool.Core.Models.Track;

namespace AudioFool;

/// <summary>
/// What Genius has on one song (right-click a song → Genius…, or Ctrl+G).
/// Not modal: it stays open while the player is used, and only one is open at
/// a time; asking for another song switches this one to it
/// (<see cref="ShowSong(Track)"/>). It stays on its song when the next one plays.
/// <para>
/// As tall as the screen allows from the start, as Listening History is: the
/// page arrives after the window opens, so it can't be sized to it. Escape or
/// Close closes it.
/// </para>
/// </summary>
public partial class GeniusWindow : FluentWindow
{
    private readonly GeniusViewModel _viewModel;

    public GeniusWindow(GeniusViewModel viewModel, Window owner)
    {
        _viewModel = viewModel;
        DataContext = viewModel;

        InitializeComponent();
        OwnerActivation.Attach(this);

        Owner = owner;
        Height = DialogPlacement.WorkAreaOf(owner).Height;

        Loaded += (_, _) => DialogPlacement.CentreOverOwner(this, owner, ActualWidth, ActualHeight);
    }

    /// <summary>Switches the window to a song, from the top.</summary>
    public Task ShowSong(Track track)
    {
        Scroller.ScrollToTop();
        return _viewModel.ShowAsync(track);
    }

    protected override void OnClosed(EventArgs e)
    {
        _viewModel.Dispose();
        base.OnClosed(e);
    }

    private async void Choice_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: GeniusChoice choice })
        {
            Scroller.ScrollToTop();
            await _viewModel.ChooseAsync(choice);
        }
    }

    private async void MoreComments_Click(object sender, RoutedEventArgs e) => await _viewModel.ShowMoreCommentsAsync();

    private void OpenPage_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel.PageUrl is { } url)
            LinkedText.Open(url);
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
