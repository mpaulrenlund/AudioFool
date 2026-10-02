using AudioFool.Core.Library;
using AudioFool.Core.Models;

namespace AudioFool.Core.Tests;

/// <summary>
/// The album header's track-count line (spec 6.5): "21 tracks", "1 track".
/// </summary>
public class AlbumHeaderTextTests
{
    [Theory]
    [InlineData(1, "1 track")]
    [InlineData(2, "2 tracks")]
    [InlineData(21, "21 tracks")]
    public void Track_count_is_singular_for_one(int count, string expected)
    {
        var album = AlbumOf(count);
        Assert.Equal(expected, album.TrackCountDisplay);
    }

    [Theory]
    [InlineData(9, 13, "9 of 13 tracks")]
    [InlineData(1, 13, "1 of 13 tracks")]
    [InlineData(13, 13, "13 tracks")]
    [InlineData(1, 1, "1 track")]
    public void Part_of_an_album_names_the_whole(int shown, int whole, string expected)
    {
        Assert.Equal(expected, AlbumOf(shown).TrackCountDisplayWithin(AlbumOf(whole)));
    }

    /// <summary>
    /// The trap: a Statistics filter showed tracks 1-9 of a 13-track album, and
    /// its header and album dialog both took the 9 for the album.
    /// </summary>
    [Fact]
    public void Filtered_album_is_found_whole_in_the_library()
    {
        var tracks = Enumerable.Range(1, 13).Select(i => new Track
        {
            FilePath = $@"D:\Music\Andy Timmons\[1994] Ear X-Tacy\{i:00}.mp3",
            Artist = "Andy Timmons",
            Album = "Ear X-Tacy",
            TrackNumber = i,
            TrackCount = i <= 9 ? null : 13,
        }).ToList();
        var other = new Track { FilePath = @"D:\Music\Other\01.mp3", Artist = "Other", Album = "Other" };
        var library = LibraryScanner.Build([.. tracks, other]);

        var filtered = LibraryScanner.Build(tracks.Where(t => t.TrackCount is null));
        var shown = filtered.Artists.Single().Albums.Single();
        var whole = library.WholeAlbumOf(shown);

        Assert.Equal(9, shown.Tracks.Count);
        Assert.Equal(13, whole.Tracks.Count);
        Assert.Equal(tracks.Select(t => t.FilePath), whole.Tracks.Select(t => t.FilePath));
        Assert.Equal("9 of 13 tracks", shown.TrackCountDisplayWithin(whole));
    }

    [Fact]
    public void Album_the_library_lacks_is_its_own_whole()
    {
        var shown = AlbumOf(3);
        Assert.Same(shown, MusicLibrary.Empty.WholeAlbumOf(shown));
    }

    private static Album AlbumOf(int count) => new()
    {
        Title = "Album",
        ArtistName = "Artist",
        Tracks = Enumerable.Range(0, count).Select(i => new Track
        {
            FilePath = $@"C:\x\{i}.flac",
            Duration = TimeSpan.FromMinutes(3),
        }).ToList(),
    };
}
