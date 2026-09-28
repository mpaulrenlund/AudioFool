using AudioFool.Core.Library;
using AudioFool.Core.Models;

namespace AudioFool.Core.Tests;

/// <summary>
/// Copies a fixture from TestData to a fresh temp path so tests never mutate the
/// checked-in original, and cleans up on dispose even if the test fails.
/// </summary>
public sealed class TempAudioFile : IDisposable
{
    public string Path { get; }

    public TempAudioFile(string fixtureName)
    {
        var source = System.IO.Path.Combine(AppContext.BaseDirectory, "TestData", fixtureName);
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"{Guid.NewGuid()}_{fixtureName}");
        File.Copy(source, Path);
    }

    public void Dispose()
    {
        if (File.Exists(Path))
            File.Delete(Path);
    }
}

public class TagWriterTests
{
    private static TrackTagEdit SampleTrackEdit() =>
        new("New Title", "New Artist", "New Album Artist", "New Album", 1999, 7, 12, 2, 3);

    [Theory]
    [InlineData("sample.flac")]
    [InlineData("sample.mp3")]
    public void WriteTrackTags_round_trips_every_field(string fixture)
    {
        using var file = new TempAudioFile(fixture);
        var track = TagReader.Read(file.Path);

        var result = TagWriter.WriteTrackTags(track, SampleTrackEdit(), art: null, folderArtPath: track.FolderArtPath);

        Assert.True(result.Success, result.ErrorMessage);

        // Independent re-read, not the in-memory UpdatedTrack - this proves the
        // bytes on disk actually changed, not just the in-memory record.
        var reread = TagReader.Read(file.Path);
        Assert.Equal("New Title", reread.Title);
        Assert.Equal("New Artist", reread.Artist);
        Assert.Equal("New Album Artist", reread.AlbumArtist);
        Assert.Equal("New Album", reread.Album);
        Assert.Equal(1999, reread.Year);
        Assert.Equal(7, reread.TrackNumber);
        Assert.Equal(12, reread.TrackCount);
        Assert.Equal(2, reread.DiscNumber);
        Assert.Equal(3, reread.DiscCount);
    }

    [Theory]
    [InlineData("sample.flac")]
    [InlineData("sample.mp3")]
    public void WriteTrackTags_restamps_size_and_modified_time(string fixture)
    {
        using var file = new TempAudioFile(fixture);
        var before = TagReader.Read(file.Path);

        var result = TagWriter.WriteTrackTags(before, SampleTrackEdit(), art: null, folderArtPath: null);

        Assert.True(result.Success, result.ErrorMessage);
        var after = result.UpdatedTrack!;

        // A real assertion, not vacuous - this is the whole basis of MatchesFile
        // staying correct so the next scan doesn't think the file needs re-reading
        // for no reason (or worse, disagrees with what's actually on disk).
        Assert.True(before.MatchesFile(before.FileSize, before.ModifiedUtc));
        Assert.False(before.MatchesFile(after.FileSize, after.ModifiedUtc));
        Assert.True(after.MatchesFile(after.FileSize, after.ModifiedUtc));
    }

    [Theory]
    [InlineData("sample.flac")]
    [InlineData("sample.mp3")]
    public void Embedded_art_round_trips_byte_for_byte(string fixture)
    {
        using var file = new TempAudioFile(fixture);
        var track = TagReader.Read(file.Path);
        var bytes = File.ReadAllBytes(System.IO.Path.Combine(AppContext.BaseDirectory, "TestData", "cover.jpg"));

        var result = TagWriter.WriteTrackTags(
            track, SampleTrackEdit(), new ArtPayload(bytes, "image/jpeg"), folderArtPath: null);

        Assert.True(result.Success, result.ErrorMessage);

        var reread = TagReader.ReadEmbeddedArt(file.Path);
        Assert.NotNull(reread);
        Assert.Equal(bytes, reread);
    }

    [Fact]
    public void WriteAlbumTrackTags_leaves_title_and_track_number_untouched()
    {
        using var file = new TempAudioFile("sample.flac");

        // Seed a known Title/TrackNumber first, since the ffmpeg-generated fixture
        // starts untagged - otherwise "untouched" would be indistinguishable from
        // "never had a value to begin with".
        var seeded = TagWriter.WriteTrackTags(
            TagReader.Read(file.Path), SampleTrackEdit(), art: null, folderArtPath: null);
        Assert.True(seeded.Success, seeded.ErrorMessage);
        var track = seeded.UpdatedTrack!;

        var edit = new AlbumTagEdit("Album Artist Edit", "Album Artist Edit", "Batch Album", 2001);
        var result = TagWriter.WriteAlbumTrackTags(track, edit, art: null, folderArtPath: null);

        Assert.True(result.Success, result.ErrorMessage);

        var reread = TagReader.Read(file.Path);
        Assert.Equal(track.Title, reread.Title);
        Assert.Equal(track.TrackNumber, reread.TrackNumber);
        Assert.Equal(track.DiscNumber, reread.DiscNumber);
        Assert.Equal("Album Artist Edit", reread.Artist);
        Assert.Equal("Batch Album", reread.Album);
        Assert.Equal(2001, reread.Year);
        Assert.Equal(12, reread.TrackCount);
        Assert.Equal(3, reread.DiscCount);
    }

    [Fact]
    public void WriteTrackTags_on_a_missing_file_fails_without_throwing()
    {
        var ghost = new Track { FilePath = @"C:\does\not\exist\ghost.flac" };

        var result = TagWriter.WriteTrackTags(ghost, SampleTrackEdit(), art: null, folderArtPath: null);

        Assert.False(result.Success);
        Assert.NotNull(result.ErrorMessage);
    }

    [Theory]
    [InlineData("sample.flac")]
    [InlineData("sample.mp3")]
    public void An_empty_total_clears_a_count_the_file_already_had(string fixture)
    {
        using var file = new TempAudioFile(fixture);

        var seeded = TagWriter.WriteTrackTags(
            TagReader.Read(file.Path), SampleTrackEdit(), art: null, folderArtPath: null);
        Assert.True(seeded.Success, seeded.ErrorMessage);
        Assert.Equal(12, TagReader.Read(file.Path).TrackCount);

        // Blanking the box is an instruction, not an omission: the dialog pre-fills
        // from the track, so an empty total can only mean the user emptied it.
        var cleared = SampleTrackEdit() with { TrackCount = null, DiscCount = null };
        var result = TagWriter.WriteTrackTags(seeded.UpdatedTrack!, cleared, art: null, folderArtPath: null);

        Assert.True(result.Success, result.ErrorMessage);

        var reread = TagReader.Read(file.Path);
        Assert.Null(reread.TrackCount);
        Assert.Null(reread.DiscCount);

        // The in-memory copy has to agree with the file, or the grid would keep
        // showing "7/12" until the next scan re-read the tag.
        Assert.Null(result.UpdatedTrack!.TrackCount);
        Assert.Null(result.UpdatedTrack!.DiscCount);
    }
}

public class TagDetailsTests
{
    private static TrackTagEdit SampleTrackEdit() =>
        new("Title", "Artist", "Album Artist", "Album", 2020, 3, 12, 1, 2);

    private static TagDetailsEdit FullDetails() => new()
    {
        Publisher = "Deutsche Grammophon",
        Composer = "Bach; Handel",
        Conductor = "Karajan",
        Genre = "Classical; Baroque",
        Comment = "Remastered\nfrom the original tapes",
    };

    /// <summary>Writes <paramref name="details"/> through a track edit and re-reads the file.</summary>
    private static TagDetails WriteAndReread(TempAudioFile file, TagDetailsEdit details)
    {
        var result = TagWriter.WriteTrackTags(
            TagReader.Read(file.Path), SampleTrackEdit() with { Details = details }, art: null, folderArtPath: null);
        Assert.True(result.Success, result.ErrorMessage);

        var reread = TagReader.ReadDetails(file.Path);
        Assert.NotNull(reread);
        return reread;
    }

    [Theory]
    [InlineData("sample.flac")]
    [InlineData("sample.mp3")]
    public void Every_detail_field_round_trips(string fixture)
    {
        using var file = new TempAudioFile(fixture);

        var reread = WriteAndReread(file, FullDetails());

        Assert.Equal("Deutsche Grammophon", reread.Publisher);
        Assert.Equal("Bach; Handel", reread.Composer);
        Assert.Equal("Karajan", reread.Conductor);
        Assert.Equal("Classical; Baroque", reread.Genre);
        Assert.Equal("Remastered\nfrom the original tapes", reread.Comment);
    }

    [Theory]
    [InlineData("sample.flac")]
    [InlineData("sample.mp3")]
    public void A_null_detail_leaves_the_file_value_alone(string fixture)
    {
        using var file = new TempAudioFile(fixture);
        WriteAndReread(file, FullDetails());

        var reread = WriteAndReread(file, new TagDetailsEdit { Genre = "Jazz" });

        Assert.Equal("Jazz", reread.Genre);
        Assert.Equal("Deutsche Grammophon", reread.Publisher);
        Assert.Equal("Bach; Handel", reread.Composer);
        Assert.Equal("Karajan", reread.Conductor);
        Assert.Equal("Remastered\nfrom the original tapes", reread.Comment);
    }

    [Theory]
    [InlineData("sample.flac")]
    [InlineData("sample.mp3")]
    public void An_empty_detail_clears_it(string fixture)
    {
        using var file = new TempAudioFile(fixture);
        WriteAndReread(file, FullDetails());

        var reread = WriteAndReread(file, new TagDetailsEdit
        {
            Publisher = "", Composer = "", Conductor = "", Genre = "", Comment = "",
        });

        Assert.Equal(new TagDetails("", "", "", "", "") { Numbers = reread.Numbers }, reread);
    }

    [Fact]
    public void Separators_are_normalised_and_empty_entries_dropped()
    {
        Assert.Equal(["Bach", "Handel"], TagDetails.Split(" Bach ;; Handel ; "));
        Assert.Equal("Bach; Handel", TagDetails.Join(["Bach", " ", "Handel "]));
        Assert.Equal("", TagDetails.Join(null));
    }

    [Fact]
    public void ReadDetails_on_a_missing_file_is_null_not_empty()
    {
        Assert.Null(TagReader.ReadDetails(@"C:\does\not\exist\ghost.flac"));
    }

    [Fact]
    public void Album_edit_writes_only_the_optional_fields_it_sets()
    {
        using var file = new TempAudioFile("sample.flac");
        WriteAndReread(file, FullDetails());
        var track = TagReader.Read(file.Path);

        var edit = new AlbumTagEdit("Artist", "Album Artist", "Album", 2020)
        {
            TrackCount = new NumberEdit(14),
            DiscCount = new NumberEdit(null),
            Details = new TagDetailsEdit { Publisher = "ECM" },
        };
        var result = TagWriter.WriteAlbumTrackTags(track, edit, art: null, folderArtPath: null);
        Assert.True(result.Success, result.ErrorMessage);

        var reread = TagReader.Read(file.Path);
        Assert.Equal(14, reread.TrackCount);
        Assert.Equal(1, reread.DiscNumber);     // not set: kept
        Assert.Null(reread.DiscCount);          // set to nothing: cleared
        Assert.Equal(3, reread.TrackNumber);

        var details = TagReader.ReadDetails(file.Path)!;
        Assert.Equal("ECM", details.Publisher);
        Assert.Equal("Bach; Handel", details.Composer);

        // The in-memory copy must agree with the file, or the grid would show
        // stale counts until the next scan.
        var updated = result.UpdatedTrack!;
        Assert.Equal(14, updated.TrackCount);
        Assert.Equal(1, updated.DiscNumber);
        Assert.Null(updated.DiscCount);
    }

    [Fact]
    public void Album_edit_can_set_the_disc_number_on_every_track()
    {
        using var file = new TempAudioFile("sample.mp3");
        var track = TagWriter.WriteTrackTags(
            TagReader.Read(file.Path), SampleTrackEdit(), art: null, folderArtPath: null).UpdatedTrack!;

        var edit = new AlbumTagEdit("Artist", "Album Artist", "Album", 2020) { DiscNumber = new NumberEdit(2) };
        var result = TagWriter.WriteAlbumTrackTags(track, edit, art: null, folderArtPath: null);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal(2, TagReader.Read(file.Path).DiscNumber);
        Assert.Equal(2, TagReader.Read(file.Path).DiscCount);
        Assert.Equal(12, TagReader.Read(file.Path).TrackCount);
    }
}

public class WriteFolderArtTests
{
    private static ArtPayload Jpeg(byte value = 1) => new([value, value, value], "image/jpeg");
    private static ArtPayload Png(byte value = 2) => new([value, value, value], "image/png");

    [Fact]
    public void Creates_cover_jpg_when_no_existing_folder_art()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var result = TagWriter.WriteFolderArt(dir.FullName, Jpeg(), existingFolderArtPath: null);

            Assert.True(result.Success, result.ErrorMessage);
            Assert.Equal(Path.Combine(dir.FullName, "cover.jpg"), result.FolderArtPath);
            Assert.True(File.Exists(result.FolderArtPath));
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void Overwrites_in_place_when_existing_extension_matches()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var existing = Path.Combine(dir.FullName, "folder.jpg");
            File.WriteAllBytes(existing, [0]);

            var result = TagWriter.WriteFolderArt(dir.FullName, Jpeg(9), existingFolderArtPath: existing);

            Assert.True(result.Success, result.ErrorMessage);
            Assert.Equal(existing, result.FolderArtPath);
            Assert.Equal(Jpeg(9).Bytes, File.ReadAllBytes(existing));
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void Deletes_stale_cover_when_format_changes()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var existing = Path.Combine(dir.FullName, "cover.png");
            File.WriteAllBytes(existing, [0]);

            var result = TagWriter.WriteFolderArt(dir.FullName, Jpeg(), existingFolderArtPath: existing);

            Assert.True(result.Success, result.ErrorMessage);
            Assert.False(File.Exists(existing));
            Assert.Equal(Path.Combine(dir.FullName, "cover.jpg"), result.FolderArtPath);
            Assert.True(File.Exists(result.FolderArtPath));
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }
}
