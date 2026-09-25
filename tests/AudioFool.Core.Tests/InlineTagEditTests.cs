using AudioFool.Core.Library;
using AudioFool.Core.Models;

namespace AudioFool.Core.Tests;

/// <summary>
/// Editing one cell of the track grid (<see cref="InlineTagEdit"/>): the typed
/// text must become an edit to that one field, nothing when it is unchanged, and
/// the file must come out with every other tag as it was.
/// </summary>
public class InlineTagEditTests
{
    private static readonly Track Sample = new()
    {
        FilePath = @"C:\Music\Artist\Album\03 Song.flac",
        TrackNumber = 3,
        TrackCount = 12,
        Title = "Song",
        Artist = "Artist",
        AlbumArtist = "Album Artist",
        Album = "Album",
    };

    [Theory]
    [InlineData(InlineField.TrackNumber, "3")]
    [InlineData(InlineField.TrackNumber, " 3 ")]
    [InlineData(InlineField.TrackNumber, "03")]
    [InlineData(InlineField.Title, "Song")]
    [InlineData(InlineField.Artist, "Artist ")]
    [InlineData(InlineField.Album, "Album")]
    public void Unchanged_text_writes_nothing(InlineField field, string text)
    {
        var result = InlineTagEdit.Build(Sample, field, text);

        Assert.Null(result.Edit);
        Assert.Null(result.Error);
    }

    [Fact]
    public void A_title_less_track_keeps_its_file_name_out_of_the_title()
    {
        // The grid shows the file name when there is no title; leaving the box
        // as it started must not write that name in as one.
        var untitled = new Track { FilePath = @"C:\Music\03 Song.flac" };

        Assert.Equal("03 Song", InlineTagEdit.InitialText(untitled, InlineField.Title));
        Assert.Null(InlineTagEdit.Build(untitled, InlineField.Title, "03 Song").Edit);
        Assert.Equal("Song", InlineTagEdit.Build(untitled, InlineField.Title, "Song").Edit!.Title);
    }

    [Fact]
    public void Each_field_sets_only_itself()
    {
        var title = InlineTagEdit.Build(Sample, InlineField.Title, " New Song ").Edit!;
        Assert.Equal(new TracksTagEdit { Title = "New Song" }, title);

        var artist = InlineTagEdit.Build(Sample, InlineField.Artist, "Guest").Edit!;
        Assert.Equal(new TracksTagEdit { Artist = "Guest" }, artist);

        var album = InlineTagEdit.Build(Sample, InlineField.Album, "Other").Edit!;
        Assert.Equal(new TracksTagEdit { Album = "Other" }, album);

        var number = InlineTagEdit.Build(Sample, InlineField.TrackNumber, "7").Edit!;
        Assert.Equal(new TracksTagEdit { TrackNumber = new NumberEdit(7) }, number);
    }

    [Fact]
    public void An_empty_track_number_clears_it()
    {
        var result = InlineTagEdit.Build(Sample, InlineField.TrackNumber, "");

        Assert.Equal(new NumberEdit(null), result.Edit!.TrackNumber);
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("0")]
    [InlineData("-2")]
    [InlineData("3/12")]
    public void A_bad_track_number_is_refused(string text)
    {
        var result = InlineTagEdit.Build(Sample, InlineField.TrackNumber, text);

        Assert.Null(result.Edit);
        Assert.NotNull(result.Error);
    }

    [Fact]
    public void An_album_cannot_be_emptied()
    {
        var result = InlineTagEdit.Build(Sample, InlineField.Album, "  ");

        Assert.Null(result.Edit);
        Assert.NotNull(result.Error);
    }

    [Theory]
    [InlineData("sample.flac")]
    [InlineData("sample.mp3")]
    public void Title_and_track_number_are_written_alone(string fixture)
    {
        using var file = new TempAudioFile(fixture);
        var seeded = TagWriter.WriteTrackTags(
            TagReader.Read(file.Path),
            new TrackTagEdit("Title", "Track Artist", "Album Artist", "Album", 2020, 3, 12, 1, 2)
            {
                Details = new TagDetailsEdit { Comment = "Keep me" },
            },
            art: null, folderArtPath: null).UpdatedTrack!;

        var afterTitle = TagWriter.WriteSelectedTrackTags(
            seeded, InlineTagEdit.Build(seeded, InlineField.Title, "Renamed").Edit!);
        Assert.True(afterTitle.Success, afterTitle.ErrorMessage);

        var afterNumber = TagWriter.WriteSelectedTrackTags(
            afterTitle.UpdatedTrack!, InlineTagEdit.Build(afterTitle.UpdatedTrack!, InlineField.TrackNumber, "5").Edit!);
        Assert.True(afterNumber.Success, afterNumber.ErrorMessage);

        var reread = TagReader.Read(file.Path);
        Assert.Equal("Renamed", reread.Title);
        Assert.Equal(5, reread.TrackNumber);
        Assert.Equal(12, reread.TrackCount);
        Assert.Equal("Track Artist", reread.Artist);
        Assert.Equal("Album Artist", reread.AlbumArtist);
        Assert.Equal("Album", reread.Album);
        Assert.Equal(1, reread.DiscNumber);
        Assert.Equal(2, reread.DiscCount);
        Assert.Equal("Keep me", TagReader.ReadDetails(file.Path)!.Comment);

        // The in-memory copy must match the file, or the grid shows stale values.
        var updated = afterNumber.UpdatedTrack!;
        Assert.Equal("Renamed", updated.Title);
        Assert.Equal(5, updated.TrackNumber);
        Assert.Equal(12, updated.TrackCount);
    }

    [Theory]
    [InlineData("sample.flac")]
    [InlineData("sample.mp3")]
    public void Clearing_the_track_number_leaves_the_count(string fixture)
    {
        using var file = new TempAudioFile(fixture);
        var seeded = TagWriter.WriteTrackTags(
            TagReader.Read(file.Path),
            new TrackTagEdit("Title", "Artist", "Artist", "Album", 2020, 3, 12, 1, 1),
            art: null, folderArtPath: null).UpdatedTrack!;

        var result = TagWriter.WriteSelectedTrackTags(
            seeded, InlineTagEdit.Build(seeded, InlineField.TrackNumber, "").Edit!);
        Assert.True(result.Success, result.ErrorMessage);

        var reread = TagReader.Read(file.Path);
        Assert.Null(reread.TrackNumber);
        Assert.Equal(12, reread.TrackCount);
        Assert.Null(result.UpdatedTrack!.TrackNumber);
    }
}
