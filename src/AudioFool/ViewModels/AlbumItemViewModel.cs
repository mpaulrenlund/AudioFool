using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using AudioFool.Core.Models;
using AudioFool.Services;

namespace AudioFool.ViewModels;

/// <summary>
/// One row in the album list. Wraps an <see cref="Album"/> so its thumbnail can
/// stream in after the list has already rendered.
/// </summary>
public sealed partial class AlbumItemViewModel : ObservableObject
{
    /// <summary>Matches the thumbnail size in the album list template.</summary>
    private const int ThumbnailWidth = 96;

    /// <param name="namesArtist">
    /// Adds a line with the artist under the title, for Recently Added, whose albums come
    /// from many artists (the user's request, 2026-10-07). The Albums list holds
    /// one artist's albums, so there it would repeat the selection.
    /// </param>
    public AlbumItemViewModel(Album album, AlbumArtService artService, bool namesArtist = false)
    {
        Album = album;
        ArtistLine = namesArtist ? album.ArtistName : null;

        // Loaded here rather than when the row scrolls into view.
        //
        // The obvious optimisation - decode only what's visible - was actively
        // wrong: the album list only ever holds one artist's albums, a handful at
        // a time, so there is nothing to save. Worse, it was driven by the row's
        // Loaded event, and this list recycles its containers. A recycled row gets
        // a new DataContext but does NOT raise Loaded again, so scrolling produced
        // rows whose art was never requested at all.
        LoadThumbnail(artService);
    }

    public Album Album { get; }

    public string Title => Album.Title;

    public string YearDisplay => Album.YearDisplay;

    /// <summary>The artist, on its own line above the year in Recently Added; null elsewhere.</summary>
    public string? ArtistLine { get; }

    public string TrackSummary =>
        Album.Tracks.Count == 1 ? "1 track" : $"{Album.Tracks.Count:N0} tracks";

    [ObservableProperty]
    private BitmapSource? _thumbnail;

    private async void LoadThumbnail(AlbumArtService artService)
    {
        // Decoding happens on a worker thread inside the service, and the result is
        // cached, so re-selecting an artist is instant.
        Thumbnail = await artService.GetAlbumArtAsync(Album, ThumbnailWidth);
    }
}
