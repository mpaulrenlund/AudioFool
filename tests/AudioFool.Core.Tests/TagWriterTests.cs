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

    // The scan reads tags without loading pictures; a file with a cover must
    // still give every field, and the cover must still be there to read.
    [Theory]
    [InlineData("sample.flac")]
    [InlineData("sample.mp3")]
    public void Read_with_embedded_art_reads_every_field(string fixture)
    {
        using var file = new TempAudioFile(fixture);
        var bytes = File.ReadAllBytes(System.IO.Path.Combine(AppContext.BaseDirectory, "TestData", "cover.jpg"));
        var written = TagWriter.WriteTrackTags(
            TagReader.Read(file.Path), SampleTrackEdit(), new ArtPayload(bytes, "image/jpeg"), folderArtPath: null);
        Assert.True(written.Success, written.ErrorMessage);

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
        Assert.True(reread.Duration > TimeSpan.Zero);
        Assert.NotNull(reread.SampleRate);
        Assert.Equal(bytes, TagReader.ReadEmbeddedArt(file.Path));
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

        var edit = new AlbumTagEdit { Artist = "Album Artist Edit", AlbumArtist = "Album Artist Edit", Album = "Batch Album", Date = new DateEdit(2001, null) };
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

        var edit = new AlbumTagEdit
        {
            Artist = "Artist",
            AlbumArtist = "Album Artist",
            Album = "Album",
            Date = new DateEdit(2020, null),
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

        var edit = new AlbumTagEdit { Artist = "Artist", AlbumArtist = "Album Artist", Album = "Album", Date = new DateEdit(2020, null), DiscNumber = new NumberEdit(2) };
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

    /// <summary>Stands in for the Recycle Bin: takes the file (deletes it) and remembers it.</summary>
    private sealed class FakeBin(bool accepts = true)
    {
        public List<string> Taken { get; } = [];

        public bool Recycle(string path)
        {
            if (!accepts)
                return false;
            Taken.Add(path);
            File.Delete(path);
            return true;
        }
    }

    private static string[] WorkingFiles(DirectoryInfo dir) =>
        dir.GetFiles(FileSwap.Prefix + "*").Select(f => f.Name).ToArray();

    [Fact]
    public void Creates_cover_jpg_when_no_existing_folder_art()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var result = TagWriter.WriteFolderArt(dir.FullName, Jpeg(), existingFolderArtPath: null, new FakeBin().Recycle);

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

            var bin = new FakeBin();
            var result = TagWriter.WriteFolderArt(dir.FullName, Jpeg(9), existingFolderArtPath: existing, bin.Recycle);

            Assert.True(result.Success, result.ErrorMessage);
            Assert.Equal(existing, result.FolderArtPath);
            Assert.Equal(Jpeg(9).Bytes, File.ReadAllBytes(existing));
            Assert.Empty(bin.Taken);
            Assert.Empty(WorkingFiles(dir));
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void Recycles_stale_cover_when_format_changes()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var existing = Path.Combine(dir.FullName, "cover.png");
            File.WriteAllBytes(existing, [0]);

            var bin = new FakeBin();
            var result = TagWriter.WriteFolderArt(dir.FullName, Jpeg(), existingFolderArtPath: existing, bin.Recycle);

            Assert.True(result.Success, result.ErrorMessage);
            Assert.Null(result.Warning);
            Assert.Equal([existing], bin.Taken);
            Assert.False(File.Exists(existing));
            Assert.Equal(Path.Combine(dir.FullName, "cover.jpg"), result.FolderArtPath);
            Assert.Equal(Jpeg().Bytes, File.ReadAllBytes(result.FolderArtPath!));
            Assert.Empty(WorkingFiles(dir));
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    /// <summary>
    /// A drive with no Recycle Bin (an SD card): the old cover stays, the result
    /// says so, and its path is the file the library's lookup will show.
    /// </summary>
    [Fact]
    public void Keeps_stale_cover_when_the_bin_refuses_it()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var existing = Path.Combine(dir.FullName, "cover.jpg");
            File.WriteAllBytes(existing, [0]);

            var result = TagWriter.WriteFolderArt(dir.FullName, Png(), existingFolderArtPath: existing, new FakeBin(accepts: false).Recycle);

            Assert.True(result.Success, result.ErrorMessage);
            Assert.Contains("cover.jpg", result.Warning);
            Assert.Equal(new byte[] { 0 }, File.ReadAllBytes(existing));
            Assert.Equal(Png().Bytes, File.ReadAllBytes(Path.Combine(dir.FullName, "cover.png")));
            Assert.Equal(existing, result.FolderArtPath); // cover.jpg comes first in the lookup
            Assert.Empty(WorkingFiles(dir));
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    /// <summary>
    /// The review's finding: the old cover was deleted before the new one was
    /// written, so a write that failed left the folder with no cover at all.
    /// </summary>
    [Fact]
    public void A_failed_write_keeps_the_old_cover_of_another_format()
    {
        var dir = Directory.CreateTempSubdirectory();
        var blocker = Path.Combine(dir.FullName, "cover.jpg");
        try
        {
            var existing = Path.Combine(dir.FullName, "folder.png");
            File.WriteAllBytes(existing, [7]);
            File.WriteAllBytes(blocker, [8]);
            File.SetAttributes(blocker, FileAttributes.ReadOnly);

            var bin = new FakeBin();
            var result = TagWriter.WriteFolderArt(dir.FullName, Jpeg(), existingFolderArtPath: existing, bin.Recycle);

            Assert.False(result.Success);
            Assert.Equal(existing, result.FolderArtPath);
            Assert.Equal(new byte[] { 7 }, File.ReadAllBytes(existing));
            Assert.Empty(bin.Taken);
            Assert.Empty(WorkingFiles(dir));
        }
        finally
        {
            File.SetAttributes(blocker, FileAttributes.Normal);
            dir.Delete(recursive: true);
        }
    }

    /// <summary>
    /// A failed write leaves the cover as it was. Until session 62 the bytes
    /// went straight into the cover, so a write cut off partway left it
    /// half-written; now they go to a working file renamed over it.
    /// </summary>
    [Fact]
    public void A_failed_write_leaves_the_same_format_cover_whole()
    {
        var dir = Directory.CreateTempSubdirectory();
        var existing = Path.Combine(dir.FullName, "cover.jpg");
        try
        {
            File.WriteAllBytes(existing, [1, 2, 3, 4]);
            File.SetAttributes(existing, FileAttributes.ReadOnly);

            var result = TagWriter.WriteFolderArt(dir.FullName, Jpeg(9), existingFolderArtPath: existing, new FakeBin().Recycle);

            Assert.False(result.Success);
            Assert.Equal(new byte[] { 1, 2, 3, 4 }, File.ReadAllBytes(existing));
            Assert.Empty(WorkingFiles(dir));
        }
        finally
        {
            File.SetAttributes(existing, FileAttributes.Normal);
            dir.Delete(recursive: true);
        }
    }
}

public class FolderArtTargetsTests
{
    private const string Folder = @"D:\Music\Singles";

    private static Track Song(string folder, string file, string album) =>
        new() { FilePath = Path.Combine(folder, file), Album = album };

    private static Album AlbumOf(string title, params Track[] tracks) =>
        new() { Title = title, ArtistName = "A", Tracks = tracks };

    [Fact]
    public void A_folder_of_only_this_albums_songs_gets_the_file()
    {
        var one = Song(Folder, "1.flac", "Blue");
        var two = Song(Folder, "2.flac", "Blue");

        Assert.False(FolderArtTargets.HoldsOtherAlbums(AlbumOf("Blue", one, two), Folder, [one, two]));
    }

    [Fact]
    public void A_folder_shared_with_another_album_does_not()
    {
        var mine = Song(Folder, "1.flac", "Blue");
        var theirs = Song(Folder, "2.flac", "Red");

        Assert.True(FolderArtTargets.HoldsOtherAlbums(AlbumOf("Blue", mine), Folder, [mine, theirs]));
    }

    [Fact]
    public void Another_albums_song_in_a_different_folder_does_not_count()
    {
        var mine = Song(Folder, "1.flac", "Blue");
        var theirs = Song(@"D:\Music\Other", "2.flac", "Red");

        Assert.False(FolderArtTargets.HoldsOtherAlbums(AlbumOf("Blue", mine), Folder, [mine, theirs]));
    }

    [Fact]
    public void A_song_of_the_same_album_name_under_another_artist_counts_as_this_album()
    {
        var mine = Song(Folder, "1.flac", "Blue");
        var splitOff = Song(Folder, "2.flac", "blue ");

        Assert.False(FolderArtTargets.HoldsOtherAlbums(AlbumOf("Blue", mine), Folder, [mine, splitOff]));
    }

    /// <summary>Keep Best Cover hands over a folder's songs, which may be two albums.</summary>
    [Fact]
    public void Songs_of_two_albums_among_the_ones_given_make_a_shared_folder()
    {
        var blue = Song(Folder, "1.flac", "Blue");
        var red = Song(Folder, "2.flac", "Red");

        Assert.True(FolderArtTargets.HoldsOtherAlbums([blue, red], Folder, [blue, red]));
    }

    [Fact]
    public void Some_songs_of_one_album_given_and_the_rest_in_the_library_is_not_shared()
    {
        var one = Song(Folder, "1.flac", "Blue");
        var two = Song(Folder, "2.flac", "Blue");

        Assert.False(FolderArtTargets.HoldsOtherAlbums([one], Folder, [one, two]));
    }

    [Fact]
    public void Untagged_songs_of_other_artists_count_as_other_albums()
    {
        var mine = Song(Folder, "1.flac", "");
        var theirs = Song(Folder, "2.flac", "");

        Assert.True(FolderArtTargets.HoldsOtherAlbums(AlbumOf("Unknown Album", mine), Folder, [mine, theirs]));
    }
}
