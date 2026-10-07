using AudioFool.Core.Library;
using AudioFool.Core.Models;

namespace AudioFool.Core.Tests;

/// <summary>
/// Several artists or albums picked in the browser lists, for one tag edit:
/// whole artists and albums, each track once, and the dialog title's wording.
/// </summary>
public class TagSelectionTests
{
    // One album split across three artist rows: what the user selects to merge.
    private static readonly MusicLibrary Library = LibraryScanner.Build(
    [
        Song("Rush", "Moving Pictures", 1),
        Song("Rush", "Moving Pictures", 2),
        Song("Rush", "Signals", 1),
        Song("Rush feat. Geddy Lee", "Moving Pictures", 3),
        Song("Rush & Friends", "Moving Pictures", 4),
        Song("Rush & Friends", "Moving Pictures (Bonus)", 5),
        Song("Other", "Other", 1),
    ]);

    private static Track Song(string artist, string album, int number) => new()
    {
        FilePath = $@"D:\Music\{artist}\{album}\{number:00}.flac",
        Artist = artist,
        Album = album,
        TrackNumber = number,
    };

    private static ArtistGroup Artist(MusicLibrary library, string name) =>
        library.Artists.Single(a => a.Name == name);

    [Fact]
    public void Artists_cover_every_album_of_each()
    {
        var selection = TagSelection.ForArtists(Library,
            [Artist(Library, "Rush"), Artist(Library, "Rush & Friends")]);

        Assert.Equal(5, selection.Tracks.Count);
        Assert.Contains(selection.Tracks, t => t.Album == "Signals");
        Assert.Contains(selection.Tracks, t => t.Album == "Moving Pictures (Bonus)");
        Assert.DoesNotContain(selection.Tracks, t => t.Artist is "Rush feat. Geddy Lee" or "Other");
        Assert.Equal("2 artists, 5 tracks", selection.Description);
    }

    [Fact]
    public void Tracks_follow_the_order_of_the_rows()
    {
        var selection = TagSelection.ForArtists(Library,
            [Artist(Library, "Rush & Friends"), Artist(Library, "Rush")]);

        Assert.Equal("Rush & Friends", selection.Tracks[0].Artist);
    }

    /// <summary>A search shows only the artist's matching tracks; the edit takes the whole artist.</summary>
    [Fact]
    public void A_searched_artist_is_edited_whole()
    {
        var shown = LibraryScanner.Build(LibrarySearch.Filter(Library.AllTracks, "Signals"));

        var selection = TagSelection.ForArtists(Library, [Artist(shown, "Rush")]);

        Assert.Equal(3, selection.Tracks.Count);
        Assert.Equal("1 artist, 3 tracks", selection.Description);
    }

    [Fact]
    public void Albums_cover_each_whole_album()
    {
        var shown = LibraryScanner.Build(Library.AllTracks.Where(t => t.TrackNumber != 2).ToList());
        var rush = Artist(shown, "Rush");

        var selection = TagSelection.ForAlbums(Library, rush.Albums);

        Assert.Equal(3, selection.Tracks.Count);
        Assert.Equal("2 albums, 3 tracks", selection.Description);
    }

    [Fact]
    public void The_same_album_twice_counts_once()
    {
        var album = Artist(Library, "Rush").Albums[0];
        var shownTwice = LibraryScanner.Build(album.Tracks.ToList()).Artists[0].Albums[0];

        var selection = TagSelection.ForAlbums(Library, [album, shownTwice]);

        Assert.Equal(album.Tracks.Count, selection.Tracks.Count);
        Assert.Equal($"1 album, {album.Tracks.Count} tracks", selection.Description);
    }

    /// <summary>How the album tag window finds its album again after a save or a rescan.</summary>
    [Fact]
    public void An_album_is_found_by_any_of_its_files()
    {
        var album = Artist(Library, "Rush").Albums[0];

        Assert.Same(album, Library.AlbumHolding(album.Tracks[^1].FilePath.ToUpperInvariant()));
        Assert.Null(Library.AlbumHolding(@"D:\Music\Nowhere\01.flac"));
    }

    [Fact]
    public void An_artist_not_in_the_library_is_taken_as_shown()
    {
        var stranger = LibraryScanner.Build([Song("Nobody", "Nothing", 1)]).Artists[0];

        var selection = TagSelection.ForArtists(Library, [stranger]);

        Assert.Equal("1 artist, 1 track", selection.Description);
    }
}
