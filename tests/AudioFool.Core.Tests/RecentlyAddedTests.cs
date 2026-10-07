using AudioFool.Core.Library;
using AudioFool.Core.Models;
using AudioFool.Core.Playlists;

namespace AudioFool.Core.Tests;

/// <summary>
/// Recently Added: which songs fall in the 30 days, how they group into albums,
/// and the order the Albums and Songs panels list them in.
/// </summary>
public class RecentlyAddedTests
{
    private static readonly DateTime Now = new(2026, 10, 7, 12, 0, 0, DateTimeKind.Utc);

    private static Track T(string artist, string album, int track, DateTime? added, int disc = 1) => new()
    {
        FilePath = $@"D:\Music\{artist}\{album}\{disc}-{track:00}.flac",
        Artist = artist,
        AlbumArtist = artist,
        Album = album,
        Title = $"{album} {disc}-{track}",
        TrackNumber = track,
        DiscNumber = disc,
        Duration = TimeSpan.FromSeconds(60),
        AddedUtc = added,
    };

    private static IReadOnlyList<Album> Recent(params Track[] tracks) =>
        RecentlyAdded.Albums(LibraryScanner.Build(tracks), Now);

    [Fact]
    public void Only_songs_added_in_the_last_30_days_show()
    {
        var albums = Recent(
            T("Loam", "New", 1, Now.AddDays(-2)),
            T("Rush", "Old", 1, Now.AddDays(-90)),
            T("Keyan", "Undated", 1, null));

        var album = Assert.Single(albums);
        Assert.Equal("New", album.Title);
    }

    [Fact]
    public void The_window_includes_exactly_30_days_ago_and_not_a_moment_earlier()
    {
        var albums = Recent(
            T("A", "Edge", 1, Now.AddDays(-30)),
            T("B", "Gone", 1, Now.AddDays(-30).AddSeconds(-1)));

        Assert.Equal(["Edge"], albums.Select(a => a.Title));
    }

    [Fact]
    public void Newest_album_comes_first()
    {
        var albums = Recent(
            T("Rush", "Older", 1, Now.AddDays(-20)),
            T("Loam", "Newest", 1, Now.AddDays(-1)),
            T("Keyan", "Middle", 1, Now.AddDays(-10)));

        Assert.Equal(["Newest", "Middle", "Older"], albums.Select(a => a.Title));
    }

    [Fact]
    public void An_album_is_placed_by_its_newest_song()
    {
        var albums = Recent(
            T("Rush", "Grew", 1, Now.AddDays(-25)),
            T("Rush", "Grew", 2, Now.AddDays(-1)),
            T("Loam", "Once", 1, Now.AddDays(-5)));

        Assert.Equal(["Grew", "Once"], albums.Select(a => a.Title));
    }

    [Fact]
    public void Songs_within_an_album_keep_disc_and_track_order_not_copy_order()
    {
        // Copied out of order: track 3 landed first, disc 2 before disc 1.
        var albums = Recent(
            T("Rush", "Set", 3, Now.AddDays(-1).AddMilliseconds(1)),
            T("Rush", "Set", 1, Now.AddDays(-1).AddMilliseconds(5), disc: 2),
            T("Rush", "Set", 2, Now.AddDays(-1).AddMilliseconds(9)),
            T("Rush", "Set", 1, Now.AddDays(-1).AddMilliseconds(3)));

        var album = Assert.Single(albums);
        Assert.Equal(["Set 1-1", "Set 1-2", "Set 1-3", "Set 2-1"], album.Tracks.Select(t => t.Title));
    }

    [Fact]
    public void Part_of_an_album_holds_only_its_new_songs_and_reads_as_part_of_the_whole()
    {
        var library = LibraryScanner.Build([
            T("Rush", "Bonus", 1, Now.AddDays(-200)),
            T("Rush", "Bonus", 2, Now.AddDays(-200)),
            T("Rush", "Bonus", 3, Now.AddDays(-3)),
        ]);

        var album = Assert.Single(RecentlyAdded.Albums(library, Now));
        Assert.Equal(["Bonus 1-3"], album.Tracks.Select(t => t.Title));
        Assert.Equal("1 of 3 tracks", album.TrackCountDisplayWithin(library.WholeAlbumOf(album)));
    }

    [Fact]
    public void A_wholly_new_album_is_the_library_album_itself()
    {
        var library = LibraryScanner.Build([
            T("Loam", "Fresh", 1, Now.AddDays(-1)),
            T("Loam", "Fresh", 2, Now.AddDays(-1)),
        ]);

        var album = Assert.Single(RecentlyAdded.Albums(library, Now));
        Assert.Same(library.Artists[0].Albums[0], album);
    }

    [Fact]
    public void Ties_fall_back_to_artist_then_title()
    {
        var same = Now.AddDays(-4);
        var albums = Recent(
            T("The Zombies", "Z", 1, same),
            T("Ash", "B", 1, same),
            T("Ash", "A", 1, same));

        Assert.Equal(["Ash - A", "Ash - B", "The Zombies - Z"], albums.Select(a => a.ToString()));
    }

    [Fact]
    public void Tracks_run_album_by_album_newest_first()
    {
        var albums = Recent(
            T("Rush", "Old", 2, Now.AddDays(-9)),
            T("Rush", "Old", 1, Now.AddDays(-9)),
            T("Loam", "New", 1, Now.AddDays(-1)));

        Assert.Equal(["New 1-1", "Old 1-1", "Old 1-2"], RecentlyAdded.Tracks(albums).Select(t => t.Title));
    }

    [Fact]
    public void An_empty_library_has_nothing_recent()
    {
        Assert.Empty(RecentlyAdded.Albums(MusicLibrary.Empty, Now));
    }
}
