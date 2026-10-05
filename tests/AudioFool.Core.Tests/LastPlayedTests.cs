using AudioFool.Core.Library;
using AudioFool.Core.Models;
using AudioFool.Core.Settings;

namespace AudioFool.Core.Tests;

/// <summary>
/// Opening on the track that was playing last time: remembering it, and finding
/// it again, including after the music drive has come back under another letter.
/// </summary>
public class LastPlayedTests
{
    private static Track T(string path, string artist, string album, int n) => new()
    {
        FilePath = path,
        Artist = artist,
        AlbumArtist = artist,
        Album = album,
        Title = $"Song {n}",
        TrackNumber = n,
    };

    private static MusicLibrary Library() => LibraryScanner.Build(
    [
        T(@"D:\Music\Jingoro\Back\01.flac", "Jingoro", "Back to the Future", 1),
        T(@"D:\Music\Jingoro\Back\02.flac", "Jingoro", "Back to the Future", 2),
        T(@"D:\Music\Jingoro\Back\03.flac", "Jingoro", "Back to the Future", 3),
        T(@"D:\Music\Other\X\01.flac", "Other", "X", 1),
    ]);

    [Fact]
    public void A_remembered_track_is_found_by_its_path()
    {
        var library = Library();
        var track = library.AllTracks.Single(t => t.FilePath.EndsWith(@"Back\03.flac"));

        var last = library.Remember(track);
        Assert.Equal(new LastPlayedTrack(track.FilePath, "Jingoro", "Back to the Future"), last);

        var found = library.Locate(last!);
        Assert.Equal("Jingoro", found!.Value.Artist.Name);
        Assert.Equal("Back to the Future", found.Value.Album.Title);
        Assert.Same(track, found.Value.Track);
    }

    [Fact]
    public void After_a_drive_letter_change_the_names_and_file_name_still_find_it()
    {
        var library = Library();
        var last = new LastPlayedTrack(@"E:\Music\Jingoro\Back\03.flac", "jingoro", "BACK TO THE FUTURE");

        var found = library.Locate(last)!.Value;

        Assert.Equal("Back to the Future", found.Album.Title);
        Assert.EndsWith(@"Back\03.flac", found.Track!.FilePath);
    }

    [Fact]
    public void An_album_without_the_file_still_opens_the_album()
    {
        var found = Library().Locate(new LastPlayedTrack(@"E:\gone\99.flac", "Jingoro", "Back to the Future"))!.Value;

        Assert.Equal("Back to the Future", found.Album.Title);
        Assert.Null(found.Track);
    }

    [Fact]
    public void A_track_that_is_gone_entirely_is_not_found()
    {
        Assert.Null(Library().Locate(new LastPlayedTrack(@"E:\gone\01.flac", "Nobody", "Nothing")));
    }

    [Fact]
    public void A_track_outside_the_library_is_not_remembered()
    {
        Assert.Null(Library().Remember(T(@"C:\elsewhere\01.flac", "Z", "Z", 1)));
    }

    [Fact]
    public void The_last_played_track_survives_a_settings_round_trip()
    {
        var json = System.Text.Json.JsonSerializer.Serialize(
            new AppSettings { LastPlayed = new LastPlayedTrack(@"D:\a.flac", "A", "B") });
        var back = System.Text.Json.JsonSerializer.Deserialize<AppSettings>(json)!;

        Assert.Equal(new LastPlayedTrack(@"D:\a.flac", "A", "B"), back.LastPlayed);
    }
}
