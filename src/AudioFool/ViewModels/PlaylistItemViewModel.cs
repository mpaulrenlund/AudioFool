using CommunityToolkit.Mvvm.ComponentModel;
using AudioFool.Core.Playlists;

namespace AudioFool.ViewModels;

/// <summary>
/// One row in the Playlists panel: the name over "N tracks", like an artist row.
/// The count is of the songs the library has, as the header shows.
/// </summary>
/// <param name="isRecentlyAdded">
/// Recently Added: worked out from the library, not kept in the store, so its
/// <see cref="Playlist"/> is a stand-in that only carries the name.
/// </param>
public sealed partial class PlaylistItemViewModel(Playlist playlist, bool isRecentlyAdded = false) : ObservableObject
{
    public Playlist Playlist { get; } = playlist;

    public bool IsLiked => Playlist.IsLiked;

    public bool IsRecentlyAdded { get; } = isRecentlyAdded;

    public bool CanRenameOrDelete => !Playlist.IsLiked && !IsRecentlyAdded;

    /// <summary>Songs can be added, removed and reordered, and a picture chosen.</summary>
    public bool IsEditable => !IsRecentlyAdded;

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
