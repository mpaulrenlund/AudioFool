using System.Security.Cryptography;
using AudioFool.Core.Library;

namespace AudioFool.Core.Tests;

/// <summary>
/// A tag save is made so that nothing stopping it partway can damage the song: a
/// save that fits the tag's room writes only the bytes it changes; one that
/// doesn't is made on a copy beside the song, checked and swapped in. Either way
/// the file must come out exactly as TagLib's own save makes it.
/// </summary>
public sealed class SafeTagSaveTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "audiofool-safesave-" + Guid.NewGuid().ToString("N"));

    public SafeTagSaveTests() => Directory.CreateDirectory(_folder);

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    private string Copy(string fixture, string name)
    {
        var path = Path.Combine(_folder, name + Path.GetExtension(fixture));
        File.Copy(Path.Combine(AppContext.BaseDirectory, "TestData", fixture), path);
        return path;
    }

    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    /// <summary>TagLib's own save, in place: what the result must match byte for byte.</summary>
    private static void SaveDirectly(string path, Action<TagLib.File> apply)
    {
        using var file = TagLib.File.Create(path);
        apply(file);
        file.Save();
    }

    /// <summary>One save first gives the file a tag and padding after it, as the library's files have.</summary>
    private static void Seed(string path) =>
        Assert.True(TagWriter.WriteSelectedTrackTags(TagReader.Read(path), new TracksTagEdit { Title = "Seed" }).Success);

    private IEnumerable<string> WorkingFiles() =>
        Directory.EnumerateFiles(_folder, FileSwap.Prefix + "*");

    [Theory]
    [InlineData("sample.flac")]
    [InlineData("sample.mp3")]
    [InlineData("sample.dsf")]
    public void A_save_that_fits_writes_exactly_what_taglib_would(string fixture)
    {
        var ours = Copy(fixture, "ours");
        var theirs = Copy(fixture, "theirs");
        Seed(ours);
        Seed(theirs);
        var size = new FileInfo(ours).Length;
        var holder = new RecordsWhetherAudioMoves(ours);

        var result = TagWriter.WriteSelectedTrackTags(TagReader.Read(ours), new TracksTagEdit { Title = "Retitled" }, holder);
        SaveDirectly(theirs, f => f.Tag.Title = "Retitled");

        Assert.True(result.Success, result.ErrorMessage);
        Assert.False(holder.MovesAudio);
        Assert.Equal(size, new FileInfo(ours).Length);
        Assert.Equal(Hash(theirs), Hash(ours));
        Assert.Empty(WorkingFiles());
    }

    [Theory]
    [InlineData("sample.flac")]
    [InlineData("sample.mp3")]
    [InlineData("sample.dsf")]
    public void A_save_that_moves_the_audio_is_made_on_a_copy_and_comes_out_the_same(string fixture)
    {
        var ours = Copy(fixture, "ours");
        var theirs = Copy(fixture, "theirs");
        var created = new DateTime(2024, 3, 4, 5, 6, 7, DateTimeKind.Utc);
        File.SetCreationTimeUtc(ours, created);
        var comment = new string('x', 300_000);
        var size = new FileInfo(ours).Length;
        var holder = new RecordsWhetherAudioMoves(ours);

        var result = TagWriter.WriteSelectedTrackTags(TagReader.Read(ours),
            new TracksTagEdit { Details = new TagDetailsEdit { Comment = comment } }, holder);
        SaveDirectly(theirs, f => f.Tag.Comment = comment);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.True(holder.MovesAudio);
        Assert.True(new FileInfo(ours).Length > size);
        Assert.Equal(Hash(theirs), Hash(ours));
        Assert.Equal(created, File.GetCreationTimeUtc(ours));     // Recently Added sorts by it
        Assert.Empty(WorkingFiles());
    }

    [Fact]
    public void A_new_cover_is_saved_through_the_copy_with_the_cover_in_place()
    {
        var path = Copy("sample.flac", "song");
        var cover = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "TestData", "cover.jpg"));
        var track = TagReader.Read(path);

        var result = TagWriter.WriteTrackTags(track,
            new TrackTagEdit("T", "A", "AA", "Al", 2019, 1, 9, 1, 1), new ArtPayload(cover, "image/jpeg"), folderArtPath: null);

        Assert.True(result.Success, result.ErrorMessage);
        using var file = TagLib.File.Create(path);
        Assert.Equal(cover, Assert.Single(file.Tag.Pictures).Data.Data);
        Assert.Empty(WorkingFiles());
    }

    [Fact]
    public void A_swap_that_is_refused_leaves_the_song_as_it_was_and_nothing_behind()
    {
        var path = Copy("sample.flac", "song");
        var before = Hash(path);

        TagWriteResult result;
        // Open elsewhere without letting it be renamed: the swap can't happen.
        using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            result = TagWriter.WriteSelectedTrackTags(TagReader.Read(path),
                new TracksTagEdit { Details = new TagDetailsEdit { Comment = new string('x', 300_000) } });
        }

        Assert.False(result.Success);
        Assert.Equal(before, Hash(path));
        Assert.Empty(WorkingFiles());
    }

    [Fact]
    public void A_file_changed_between_the_rehearsal_and_the_save_is_not_written()
    {
        var path = Copy("sample.flac", "song");
        Seed(path);
        var track = TagReader.Read(path);

        // A holder that lets something else write the file after the rehearsal.
        var holder = new ChangesFileAfterRehearsal(path);
        var result = TagWriter.WriteSelectedTrackTags(track, new TracksTagEdit { Title = "Retitled" }, holder);

        Assert.False(result.Success);
        Assert.Contains("changed", result.ErrorMessage);
        Assert.Equal("Seed", TagReader.Read(path).Title);
    }

    /// <summary>
    /// Says it holds the file, so the save asks whether playback must let go,
    /// which is whether the save moves the audio; records the answer and lets go
    /// of nothing.
    /// </summary>
    private sealed class RecordsWhetherAudioMoves(string path) : IFileHolder
    {
        public bool? MovesAudio { get; private set; }

        public bool HoldsFile(string p) => p == path;

        public T Saving<T>(string p, Func<bool> needsRelease, Func<T> save)
        {
            MovesAudio = needsRelease();
            return save();
        }
    }

    private sealed class ChangesFileAfterRehearsal(string path) : IFileHolder
    {
        public bool HoldsFile(string p) => p == path;

        public T Saving<T>(string p, Func<bool> needsRelease, Func<T> save)
        {
            needsRelease();
            File.SetLastWriteTimeUtc(path, File.GetLastWriteTimeUtc(path).AddMinutes(1));
            return save();
        }
    }
}

public class DryRunStreamTests
{
    private static string WriteTemp(byte[] bytes)
    {
        var path = Path.Combine(Path.GetTempPath(), "audiofool-dryrun-" + Guid.NewGuid().ToString("N"));
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private static byte[] ReadAll(Stream stream)
    {
        stream.Position = 0;
        var all = new byte[stream.Length];
        stream.ReadExactly(all);
        return all;
    }

    [Fact]
    public void Writes_are_read_back_and_the_file_is_never_touched()
    {
        var original = Enumerable.Range(0, 10_000).Select(i => (byte)i).ToArray();
        var path = WriteTemp(original);
        try
        {
            using (var stream = new DryRunStream(path, 1 << 20))
            {
                stream.Position = 4090;
                stream.Write([1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12]);

                var expected = (byte[])original.Clone();
                Array.Copy(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12 }, 0, expected, 4090, 12);
                Assert.Equal(expected, ReadAll(stream));
                Assert.True(stream.KeptSize);

                // Two pages, each with the file's own bytes around the write.
                var changes = stream.Changes();
                Assert.Equal([0L, 4096L], changes.Select(c => c.Offset));
                Assert.Equal(expected.AsSpan(4096, 4096).ToArray(), changes[1].Bytes);
            }

            Assert.Equal(original, File.ReadAllBytes(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Cutting_the_file_shorter_and_growing_it_again_gives_zeros_not_the_old_bytes()
    {
        var path = WriteTemp(Enumerable.Repeat((byte)7, 9000).ToArray());
        try
        {
            using var stream = new DryRunStream(path, 1 << 20);
            stream.SetLength(5000);
            stream.SetLength(9000);

            var all = ReadAll(stream);
            Assert.All(all.AsSpan(0, 5000).ToArray(), b => Assert.Equal(7, b));
            Assert.All(all.AsSpan(5000).ToArray(), b => Assert.Equal(0, b));
            Assert.False(stream.KeptSize);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Rewriting_more_than_the_limit_stops_the_save()
    {
        var path = WriteTemp(new byte[100_000]);
        try
        {
            using var stream = new DryRunStream(path, 8192);
            stream.Write(new byte[8192]);
            Assert.Throws<DryRunStream.TooMuchRewrittenException>(() => stream.Write([1]));
        }
        finally
        {
            File.Delete(path);
        }
    }
}

public sealed class LeftoverTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "audiofool-leftover-" + Guid.NewGuid().ToString("N"));

    public LeftoverTests() => Directory.CreateDirectory(_folder);

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    private string Touch(string name, TimeSpan age)
    {
        var path = Path.Combine(_folder, name);
        File.WriteAllText(path, "x");
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow - age);
        return path;
    }

    [Fact]
    public void Old_working_copies_go_and_backups_and_fresh_copies_stay()
    {
        var old = Touch($"{FileSwap.Prefix}save-1{FileSwap.WorkingSuffix}", TimeSpan.FromHours(2));
        var oldCover = Touch($"{FileSwap.Prefix}cover-2.shrunk{FileSwap.WorkingSuffix}", TimeSpan.FromHours(2));
        var fresh = Touch($"{FileSwap.Prefix}save-3{FileSwap.WorkingSuffix}", TimeSpan.FromMinutes(1));
        var backup = Touch($"{FileSwap.BackupPrefix}4-song.flac.bak", TimeSpan.FromDays(3));

        var deleted = FileSwap.DeleteLeftovers([old, oldCover, fresh, backup], DateTime.UtcNow);

        Assert.Equal(2, deleted);
        Assert.False(File.Exists(old));
        Assert.False(File.Exists(oldCover));
        Assert.True(File.Exists(fresh));
        Assert.True(File.Exists(backup));
    }

    [Fact]
    public async Task A_scan_reports_the_leftovers_beside_songs_and_never_takes_them_for_songs()
    {
        File.WriteAllText(Path.Combine(_folder, "song.mp3"), "not really audio");
        var part = Touch($"{FileSwap.Prefix}save-1{FileSwap.WorkingSuffix}", TimeSpan.FromHours(2));
        var backup = Touch($"{FileSwap.BackupPrefix}2-song.mp3.bak", TimeSpan.FromHours(2));

        var result = await LibraryScanner.ScanAsync([_folder]);

        Assert.Single(result.Library.AllTracks);
        Assert.Equal([backup, part], result.Leftovers.Order());
    }
}
