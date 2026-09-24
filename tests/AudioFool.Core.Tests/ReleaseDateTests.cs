using AudioFool.Core.Library;

namespace AudioFool.Core.Tests;

public class ReleaseDateTests
{
    [Theory]
    [InlineData("2026", "2026", 2026)]
    [InlineData("2026-10", "2026-10", 2026)]
    [InlineData("2026-10-02", "2026-10-02", 2026)]
    [InlineData(" 2026-10-02 ", "2026-10-02", 2026)]
    [InlineData("2026-1-2", "2026-01-02", 2026)]
    [InlineData("2024-02-29", "2024-02-29", 2024)]
    public void Accepts_year_month_and_full_date(string typed, string normalized, int year)
    {
        Assert.True(ReleaseDate.TryParse(typed, out var n, out var y));
        Assert.Equal((normalized, year), (n, y));
    }

    [Theory]
    [InlineData("0-02")]        // the half-typed date in the bug report
    [InlineData("abc")]
    [InlineData("26")]
    [InlineData("2026-13-01")]
    [InlineData("2026-02-30")]
    [InlineData("2025-02-29")]
    [InlineData("02-10-2026")] // day first is ambiguous; only year-month-day is taken
    [InlineData("2026/10/02")]
    [InlineData("0999")]
    [InlineData("")]
    public void Rejects_anything_else(string typed) =>
        Assert.False(ReleaseDate.TryParse(typed, out _, out _));

    [Theory]
    [InlineData("2014-05-01T07:00:00Z", "2014-05-01")] // MP4
    [InlineData("2014-05-01T12:30", "2014-05-01")]     // ID3v2.4 timestamp
    [InlineData("2014-05", "2014-05")]
    [InlineData("2014", "2014")]
    [InlineData("May 2014", null)]
    [InlineData(null, null)]
    public void Reads_the_date_part_of_a_stored_timestamp(string? raw, string? expected) =>
        Assert.Equal(expected, ReleaseDate.FromTag(raw));

    [Theory]
    [InlineData("sample.flac")]
    [InlineData("sample.mp3")]
    public void Full_date_survives_a_write_and_reread(string fixture)
    {
        using var file = new TempAudioFile(fixture);
        var track = TagReader.Read(file.Path);
        var edit = new TrackTagEdit("T", "A", "AA", "Al", 2026, 1, 9, 1, 1) { Date = "2026-10-02" };

        var result = TagWriter.WriteTrackTags(track, edit, art: null, folderArtPath: null);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal(2026, TagReader.Read(file.Path).Year);
        Assert.Equal("2026-10-02", TagReader.ReadDetails(file.Path)!.Date);
    }

    /// <summary>
    /// On disk, not through TagLib: TagLib reads back its own ID3v2.3 TDAT
    /// mistake, so a TagLib round trip would pass while every other player saw
    /// the day and month swapped.
    /// </summary>
    [Fact]
    public void Mp3_full_date_is_stored_as_id3v24_tdrc()
    {
        using var file = new TempAudioFile("sample.mp3");
        var track = TagReader.Read(file.Path);
        TagWriter.WriteTrackTags(track, new TrackTagEdit("T", "A", "AA", "Al", 2026, 1, 9, 1, 1) { Date = "2026-10-25" }, null, null);

        var bytes = File.ReadAllBytes(file.Path);
        Assert.Equal((byte)'I', bytes[0]);
        Assert.Equal(4, bytes[3]); // ID3v2 major version

        var text = System.Text.Encoding.Latin1.GetString(bytes);
        Assert.DoesNotContain("TDAT", text);
        var frame = text.IndexOf("TDRC", StringComparison.Ordinal);
        Assert.True(frame >= 0);
        Assert.Contains("2026-10-25", text.Substring(frame, 40).Replace("\0", ""));
    }

    [Theory]
    [InlineData("sample.flac")]
    [InlineData("sample.mp3")]
    public void Typing_a_plain_year_replaces_a_full_date(string fixture)
    {
        using var file = new TempAudioFile(fixture);
        var track = TagReader.Read(file.Path);
        TagWriter.WriteTrackTags(track, new TrackTagEdit("T", "A", "AA", "Al", 2026, 1, 9, 1, 1) { Date = "2026-10-02" }, null, null);

        var result = TagWriter.WriteTrackTags(TagReader.Read(file.Path), new TrackTagEdit("T", "A", "AA", "Al", 2019, 1, 9, 1, 1), null, null);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal(2019, TagReader.Read(file.Path).Year);
        Assert.Equal("", TagReader.ReadDetails(file.Path)!.Date);
    }

    [Theory]
    [InlineData("sample.flac")]
    [InlineData("sample.mp3")]
    public void Album_edit_writes_the_full_date_to_every_track(string fixture)
    {
        using var file = new TempAudioFile(fixture);
        var track = TagReader.Read(file.Path);
        var edit = new AlbumTagEdit("A", "AA", "Al", 2026) { Date = "2026-10-02" };

        var result = TagWriter.WriteAlbumTrackTags(track, edit, art: null, folderArtPath: null);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal(2026, TagReader.Read(file.Path).Year);
        Assert.Equal("2026-10-02", TagReader.ReadDetails(file.Path)!.Date);
    }
}
