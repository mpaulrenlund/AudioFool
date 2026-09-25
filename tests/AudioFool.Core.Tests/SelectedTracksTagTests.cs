using AudioFool.Core.Library;
using AudioFool.Core.Models;

namespace AudioFool.Core.Tests;

/// <summary>
/// Edits to hand-picked tracks (<see cref="TracksTagEdit"/>): every field is
/// optional, so a file must come out with exactly the fields the edit set changed
/// and every other tag - artist and album included - exactly as it was.
/// </summary>
public class SelectedTracksTagTests
{
    private static TrackTagEdit Seed() =>
        new("Title", "Track Artist", "Album Artist", "Album", 2020, 3, 12, 1, 1)
        {
            Date = "2020-05-01",
            Details = new TagDetailsEdit { Genre = "Rock", Comment = "Keep me" },
        };

    private static Track Seeded(TempAudioFile file)
    {
        var result = TagWriter.WriteTrackTags(TagReader.Read(file.Path), Seed(), art: null, folderArtPath: null);
        Assert.True(result.Success, result.ErrorMessage);
        return result.UpdatedTrack!;
    }

    [Theory]
    [InlineData("sample.flac")]
    [InlineData("sample.mp3")]
    public void An_empty_edit_changes_no_tag(string fixture)
    {
        using var file = new TempAudioFile(fixture);
        var track = Seeded(file);

        var result = TagWriter.WriteSelectedTrackTags(track, new TracksTagEdit());
        Assert.True(result.Success, result.ErrorMessage);

        var reread = TagReader.Read(file.Path);
        Assert.Equal("Title", reread.Title);
        Assert.Equal("Track Artist", reread.Artist);
        Assert.Equal("Album Artist", reread.AlbumArtist);
        Assert.Equal("Album", reread.Album);
        Assert.Equal(2020, reread.Year);
        Assert.Equal("2020-05-01", reread.ReleaseDate);
        Assert.Equal(3, reread.TrackNumber);
        Assert.Equal(12, reread.TrackCount);
        Assert.Equal(1, reread.DiscNumber);
        Assert.Equal(1, reread.DiscCount);

        var details = TagReader.ReadDetails(file.Path)!;
        Assert.Equal("Rock", details.Genre);
        Assert.Equal("Keep me", details.Comment);
    }

    [Theory]
    [InlineData("sample.flac")]
    [InlineData("sample.mp3")]
    public void Disc_fields_can_be_set_without_touching_anything_else(string fixture)
    {
        // The case the feature exists for: pick disc 1's tracks, say "disc 1 of 2".
        using var file = new TempAudioFile(fixture);
        var track = Seeded(file);

        var edit = new TracksTagEdit
        {
            DiscNumber = new NumberEdit(1),
            DiscCount = new NumberEdit(2),
            TrackCount = new NumberEdit(9),
        };
        var result = TagWriter.WriteSelectedTrackTags(track, edit);
        Assert.True(result.Success, result.ErrorMessage);

        var reread = TagReader.Read(file.Path);
        Assert.Equal(1, reread.DiscNumber);
        Assert.Equal(2, reread.DiscCount);
        Assert.Equal(9, reread.TrackCount);
        Assert.Equal("Track Artist", reread.Artist);
        Assert.Equal("Album", reread.Album);
        Assert.Equal("2020-05-01", reread.ReleaseDate);

        // The in-memory copy must match the file, or the grid shows stale values.
        var updated = result.UpdatedTrack!;
        Assert.Equal(2, updated.DiscCount);
        Assert.Equal(9, updated.TrackCount);
        Assert.Equal("Track Artist", updated.Artist);
        Assert.Equal("2020-05-01", updated.ReleaseDate);
        Assert.Equal(3, updated.TrackNumber);
        Assert.Equal("Title", updated.Title);
    }

    [Theory]
    [InlineData("sample.flac")]
    [InlineData("sample.mp3")]
    public void Set_fields_are_written_and_empty_ones_cleared(string fixture)
    {
        using var file = new TempAudioFile(fixture);
        var track = Seeded(file);

        var edit = new TracksTagEdit
        {
            Artist = "New Artist",
            Album = "Disc One",
            Date = new DateEdit(2021, null),
            DiscCount = new NumberEdit(null),
            Details = new TagDetailsEdit { Genre = "" },
        };
        var result = TagWriter.WriteSelectedTrackTags(track, edit);
        Assert.True(result.Success, result.ErrorMessage);

        var reread = TagReader.Read(file.Path);
        Assert.Equal("New Artist", reread.Artist);
        Assert.Equal("Album Artist", reread.AlbumArtist);
        Assert.Equal("Disc One", reread.Album);
        Assert.Equal(2021, reread.Year);
        Assert.Null(reread.ReleaseDate);
        Assert.Null(reread.DiscCount);
        Assert.Equal(1, reread.DiscNumber);

        var details = TagReader.ReadDetails(file.Path)!;
        Assert.Equal("", details.Genre);
        Assert.Equal("Keep me", details.Comment);

        var updated = result.UpdatedTrack!;
        Assert.Equal(2021, updated.Year);
        Assert.Null(updated.ReleaseDate);
        Assert.Null(updated.DiscCount);
    }

    [Fact]
    public void A_missing_file_fails_without_throwing()
    {
        var ghost = new Track { FilePath = @"C:\does\not\exist\ghost.flac" };

        var result = TagWriter.WriteSelectedTrackTags(ghost, new TracksTagEdit { DiscNumber = new NumberEdit(1) });

        Assert.False(result.Success);
        Assert.NotNull(result.ErrorMessage);
    }
}
