using CommunityToolkit.Mvvm.ComponentModel;
using AudioFool.Core.Playlists;

namespace AudioFool.ViewModels;

/// <summary>
/// One row in the Playlists panel: the name over "N tracks", like an artist row.
/// The count is of the songs the library has, as the header shows.
/// </summary>
public sealed partial class PlaylistItemViewModel(Playlist playlist) : ObservableObject
{
    public Playlist Playlist { get; } = playlist;

    public bool IsLiked => Playlist.IsLiked;

    public bool CanRenameOrDelete => !Playlist.IsLiked;

    [ObservableProperty]
    private string _name = playlist.Name;

    [ObservableProperty]
    private string _trackSummary = "";

    [ObservableProperty]
    private bool _hasPicture = playlist.Picture is not null;

    /// <summary>Picks up a change made through the store.</summary>
    public void Refresh(int trackCount)
    {
        Name = Playlist.Name;
        HasPicture = Playlist.Picture is not null;
        TrackSummary = PlaylistText.TrackCount(trackCount);
    }
}
