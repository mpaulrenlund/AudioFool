using AudioFool.Core.Library;

namespace AudioFool.Core.Tests;

/// <summary>
/// Leading zeros in track and disc numbers: the dialog must show "01" so the user
/// can see it needs fixing, and a save must write "1" - TagLib on its own writes
/// every track number as "01".
/// </summary>
public class NumberSpellingTests
{
    /// <summary>Writes each format's own number fields directly, bypassing TagLib's formatting.</summary>
    private static void SetRaw(string path, string track, string trackTotal, string disc, string discTotal = "")
    {
        using var file = TagLib.File.Create(path);
        if (file.GetTag(TagLib.TagTypes.Xiph, false) is TagLib.Ogg.XiphComment xiph)
        {
            xiph.SetField("TRACKNUMBER", track);
            xiph.SetField("TRACKTOTAL", trackTotal);
            xiph.SetField("DISCNUMBER", disc);
            if (discTotal.Length > 0)
                xiph.SetField("DISCTOTAL", discTotal);
        }
        if (file is not TagLib.Flac.File && file.GetTag(TagLib.TagTypes.Id3v2, true) is TagLib.Id3v2.Tag id3)
        {
            id3.SetTextFrame("TRCK", $"{track}/{trackTotal}");
            id3.SetTextFrame("TPOS", discTotal.Length > 0 ? $"{disc}/{discTotal}" : disc);
        }
        file.Save();
    }

    [Theory]
    [InlineData("sample.flac")]
    [InlineData("sample.mp3")]
    public void Album_edit_can_remove_every_leading_zero(string fixture)
    {
        using var file = new TempAudioFile(fixture);
        SetRaw(file.Path, "03", "012", "01", "02");
        var track = TagReader.Read(file.Path);

        var edit = new AlbumTagEdit("Artist", "Album Artist", "Album", 2020) { RemoveLeadingZeros = true };
        Assert.True(TagWriter.WriteAlbumTrackTags(track, edit, art: null, folderArtPath: null).Success);

        Assert.Equal(new NumberTexts("3", "12", "1", "2"), TagReader.ReadDetails(file.Path)!.Numbers);
    }

    [Fact]
    public void Removing_zeros_keeps_a_disc_number_the_edit_sets()
    {
        using var file = new TempAudioFile("sample.flac");
        SetRaw(file.Path, "03", "12", "01");
        var track = TagReader.Read(file.Path);

        var edit = new AlbumTagEdit("Artist", "Album Artist", "Album", 2020)
        {
            RemoveLeadingZeros = true,
            DiscNumber = new NumberEdit(2),
        };
        Assert.True(TagWriter.WriteAlbumTrackTags(track, edit, art: null, folderArtPath: null).Success);

        Assert.Equal(new NumberTexts("3", "12", "2", ""), TagReader.ReadDetails(file.Path)!.Numbers);
    }

    [Fact]
    public void Removing_zeros_leaves_a_missing_number_missing()
    {
        using var file = new TempAudioFile("sample.mp3");
        SetRaw(file.Path, "04", "12", "1");
        using (var tagged = TagLib.File.Create(file.Path)) { tagged.Tag.Disc = 0; tagged.Save(); }
        var track = TagReader.Read(file.Path);

        var edit = new AlbumTagEdit("Artist", "Album Artist", "Album", 2020) { RemoveLeadingZeros = true };
        Assert.True(TagWriter.WriteAlbumTrackTags(track, edit, art: null, folderArtPath: null).Success);

        Assert.Equal(new NumberTexts("4", "12", "", ""), TagReader.ReadDetails(file.Path)!.Numbers);
    }

    /// <summary>The track field as it sits in the file: Xiph TRACKNUMBER or ID3v2 TRCK.</summary>
    private static string? RawTrack(string path)
    {
        using var file = TagLib.File.Create(path);
        if (file.GetTag(TagLib.TagTypes.Xiph, false) is TagLib.Ogg.XiphComment xiph)
            return xiph.GetFirstField("TRACKNUMBER");
        var id3 = (TagLib.Id3v2.Tag)file.GetTag(TagLib.TagTypes.Id3v2, false);
        return TagLib.Id3v2.TextInformationFrame.Get(id3, "TRCK", false)?.ToString();
    }

    private static string? RawDisc(string path)
    {
        using var file = TagLib.File.Create(path);
        if (file.GetTag(TagLib.TagTypes.Xiph, false) is TagLib.Ogg.XiphComment xiph)
            return xiph.GetFirstField("DISCNUMBER");
        var id3 = (TagLib.Id3v2.Tag)file.GetTag(TagLib.TagTypes.Id3v2, false);
        return TagLib.Id3v2.TextInformationFrame.Get(id3, "TPOS", false)?.ToString();
    }

    [Theory]
    [InlineData("sample.flac")]
    [InlineData("sample.mp3")]
    public void ReadDetails_keeps_leading_zeros(string fixture)
    {
        using var file = new TempAudioFile(fixture);
        SetRaw(file.Path, "01", "012", "01");

        var numbers = TagReader.ReadDetails(file.Path)!.Numbers;

        Assert.Equal(new NumberTexts("01", "012", "01", ""), numbers);
        // The cache and the grid still hold the number.
        Assert.Equal(1, TagReader.Read(file.Path).TrackNumber);
    }

    [Theory]
    [InlineData("sample.flac")]
    [InlineData("sample.mp3")]
    public void Plain_numbers_read_as_they_are(string fixture)
    {
        using var file = new TempAudioFile(fixture);
        SetRaw(file.Path, "7", "12", "2");

        Assert.Equal(new NumberTexts("7", "12", "2", ""), TagReader.ReadDetails(file.Path)!.Numbers);
    }

    [Theory]
    [InlineData("sample.flac", "1")]
    [InlineData("sample.mp3", "1/12")]
    public void Track_dialog_save_writes_the_number_without_a_zero(string fixture, string expected)
    {
        using var file = new TempAudioFile(fixture);
        SetRaw(file.Path, "01", "12", "01");
        var track = TagReader.Read(file.Path);

        var edit = new TrackTagEdit("Title", "Artist", "Album Artist", "Album", 2020, 1, 12, 1, null);
        Assert.True(TagWriter.WriteTrackTags(track, edit, art: null, folderArtPath: null).Success);

        Assert.Equal(expected, RawTrack(file.Path));
        Assert.Equal("1", RawDisc(file.Path));
        Assert.Equal(new NumberTexts("1", "12", "1", ""), TagReader.ReadDetails(file.Path)!.Numbers);
    }

    [Theory]
    [InlineData("sample.flac")]
    [InlineData("sample.mp3")]
    public void Grid_edit_of_the_number_writes_it_without_a_zero(string fixture)
    {
        using var file = new TempAudioFile(fixture);
        SetRaw(file.Path, "05", "12", "1");
        var track = TagReader.Read(file.Path);

        var edit = new TracksTagEdit { TrackNumber = new NumberEdit(4) };
        Assert.True(TagWriter.WriteSelectedTrackTags(track, edit).Success);

        Assert.Equal("4", TagReader.ReadDetails(file.Path)!.Numbers.TrackNumber);
    }

    [Fact]
    public void Album_disc_edit_leaves_a_padded_track_number_alone()
    {
        using var file = new TempAudioFile("sample.flac");
        SetRaw(file.Path, "03", "12", "01");
        var track = TagReader.Read(file.Path);

        var edit = new AlbumTagEdit("Artist", "Album Artist", "Album", 2020) { DiscNumber = new NumberEdit(1) };
        Assert.True(TagWriter.WriteAlbumTrackTags(track, edit, art: null, folderArtPath: null).Success);

        Assert.Equal("03", RawTrack(file.Path));   // not part of the edit
        Assert.Equal("1", RawDisc(file.Path));      // the edit
    }

    [Fact]
    public void Changing_an_mp3_track_total_does_not_pad_its_number()
    {
        // ID3v2 keeps number and total in one frame, so TagLib rewrites TRCK -
        // as "03/10" - when only the total changes.
        using var file = new TempAudioFile("sample.mp3");
        SetRaw(file.Path, "3", "12", "1");
        var track = TagReader.Read(file.Path);

        var edit = new AlbumTagEdit("Artist", "Album Artist", "Album", 2020) { TrackCount = new NumberEdit(10) };
        Assert.True(TagWriter.WriteAlbumTrackTags(track, edit, art: null, folderArtPath: null).Success);

        Assert.Equal("3/10", RawTrack(file.Path));
    }

    [Fact]
    public void Clearing_the_track_number_removes_it()
    {
        using var file = new TempAudioFile("sample.mp3");
        SetRaw(file.Path, "01", "12", "1");
        var track = TagReader.Read(file.Path);

        var edit = new TracksTagEdit { TrackNumber = new NumberEdit(null) };
        Assert.True(TagWriter.WriteSelectedTrackTags(track, edit).Success);

        Assert.Null(TagReader.Read(file.Path).TrackNumber);
        Assert.Equal("", TagReader.ReadDetails(file.Path)!.Numbers.TrackNumber);
    }
}
