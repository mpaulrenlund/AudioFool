using System.Security.Cryptography;
using AudioFool.Core.Art;
using AudioFool.Core.Library;

namespace AudioFool.Core.Tests;

/// <summary>
/// Keeping only the best embedded cover and cutting the room the others leave.
/// The original must never be changed unless the result checks out.
/// </summary>
public class CoverCleanerTests
{
    private static byte[] Png(int width, int height, int filler)
    {
        byte[] head =
        [
            0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A,
            0, 0, 0, 13, (byte)'I', (byte)'H', (byte)'D', (byte)'R',
            (byte)(width >> 24), (byte)(width >> 16), (byte)(width >> 8), (byte)width,
            (byte)(height >> 24), (byte)(height >> 16), (byte)(height >> 8), (byte)height,
        ];
        var bytes = new byte[head.Length + filler];
        head.CopyTo(bytes, 0);
        // Not zeros: a run of zeros at the end of a frame would pass for padding.
        new Random(width).NextBytes(bytes.AsSpan(head.Length));
        return bytes;
    }

    private static TagLib.Picture Picture(byte[] data) =>
        new(new TagLib.ByteVector(data)) { Type = TagLib.PictureType.FrontCover, MimeType = "image/png" };

    private static readonly byte[] Small = Png(500, 500, 40_000);
    private static readonly byte[] Large = Png(1500, 1500, 120_000);

    /// <summary>The fixture with a title and three covers, the largest in the middle.</summary>
    private static TempAudioFile WithThreeCovers(string fixture)
    {
        var file = new TempAudioFile(fixture);
        using var tagged = TagLib.File.Create(file.Path);
        tagged.Tag.Title = "Kept Title";
        tagged.Tag.Performers = ["Kept Artist"];
        tagged.Tag.Pictures = [Picture(Small), Picture(Large), Picture(Png(500, 500, 30_000))];
        tagged.Save();
        return file;
    }

    private static string AudioHash(string path)
    {
        var start = TagPadding.Read(path)!.AudioStart;
        var bytes = File.ReadAllBytes(path);
        return Convert.ToHexString(SHA256.HashData(bytes.AsSpan((int)start)));
    }

    [Theory]
    [InlineData("sample.flac")]
    [InlineData("sample.mp3")]
    public void Keeps_the_best_cover_and_cuts_the_padding(string fixture)
    {
        using var file = WithThreeCovers(fixture);
        var before = new FileInfo(file.Path).Length;
        var audio = AudioHash(file.Path);

        var result = CoverCleaner.Clean(file.Path);

        Assert.Equal(CoverCleanOutcome.Cleaned, result.Outcome);
        Assert.True(result.Shrunk);
        Assert.Equal(before - new FileInfo(file.Path).Length, result.BytesFreed);
        // Both extra covers (70,000 bytes), less the reserve kept.
        Assert.True(result.BytesFreed >= 70_000 - TagPadding.Reserve, $"freed {result.BytesFreed}");

        using (var reread = TagLib.File.Create(file.Path))
        {
            var only = Assert.Single(reread.Tag.Pictures);
            Assert.Equal(Large, only.Data.Data);
            Assert.Equal("Kept Title", reread.Tag.Title);
            Assert.Equal(["Kept Artist"], reread.Tag.Performers);
        }

        var layout = TagPadding.Read(file.Path)!;
        Assert.InRange(layout.Padding, TagPadding.Reserve, TagPadding.Reserve + 4);
        Assert.Equal(audio, AudioHash(file.Path));
    }

    [Theory]
    [InlineData("sample.flac")]
    [InlineData("sample.mp3")]
    public void Keeps_the_creation_date_and_leaves_no_working_files(string fixture)
    {
        using var source = WithThreeCovers(fixture);
        // A folder of its own, so other tests' files in %TEMP% don't count.
        var folder = Path.Combine(Path.GetTempPath(), $"AudioFool-covers-{Guid.NewGuid():N}");
        Directory.CreateDirectory(folder);
        try
        {
            var path = Path.Combine(folder, "song" + Path.GetExtension(source.Path));
            File.Copy(source.Path, path);
            var created = new DateTime(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc);
            File.SetCreationTimeUtc(path, created);

            Assert.Equal(CoverCleanOutcome.Cleaned, CoverCleaner.Clean(path).Outcome);

            Assert.Equal(created, File.GetCreationTimeUtc(path));
            Assert.Equal([path], Directory.GetFiles(folder));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Theory]
    [InlineData("sample.flac")]
    [InlineData("sample.mp3")]
    public void A_file_with_one_cover_is_not_written(string fixture)
    {
        using var file = new TempAudioFile(fixture);
        using (var tagged = TagLib.File.Create(file.Path))
        {
            tagged.Tag.Pictures = [Picture(Large)];
            tagged.Save();
        }

        var stamp = FileStamp.For(file.Path);

        Assert.Equal(CoverCleanOutcome.NothingToDo, CoverCleaner.Clean(file.Path).Outcome);
        Assert.Equal(stamp, FileStamp.For(file.Path));
    }

    [Theory]
    [InlineData("sample.flac")]
    [InlineData("sample.mp3")]
    public void Survey_counts_the_pictures_and_what_cleaning_would_free(string fixture)
    {
        using var file = WithThreeCovers(fixture);

        var finding = CoverCleaner.Survey(file.Path)!;
        var freed = CoverCleaner.Clean(file.Path).BytesFreed;

        Assert.Equal(3, finding.Pictures);
        Assert.True(finding.HasExtras);
        // The survey counts picture bytes; the file also loses each one's few header bytes.
        Assert.InRange(finding.Freeable, freed - 200, freed);
        Assert.Equal(new CoverFinding(1, 0), CoverCleaner.Survey(file.Path));
    }

    [Theory]
    [InlineData("sample.flac")]
    [InlineData("sample.mp3")]
    public void The_check_catches_changed_audio(string fixture)
    {
        using var original = WithThreeCovers(fixture);
        var copy = original.Path + ".copy" + Path.GetExtension(original.Path);
        try
        {
            File.Copy(original.Path, copy);
            using (var tagged = TagLib.File.Create(copy))
            {
                tagged.Tag.Pictures = [Picture(Large)];
                tagged.Save();
            }

            Assert.Null(CoverCleaner.Verify(original.Path, copy, Large));

            // The fixtures' audio is short (the FLAC's is 72 bytes), so the
            // byte flipped is the last one before any ID3v1 tag at the end.
            using (var stream = new FileStream(copy, FileMode.Open, FileAccess.ReadWrite))
            {
                stream.Position = stream.Length - 128;
                var tag = new byte[3];
                stream.ReadExactly(tag);
                var last = tag is [(byte)'T', (byte)'A', (byte)'G'] ? stream.Length - 129 : stream.Length - 1;
                stream.Position = last;
                var b = stream.ReadByte();
                stream.Position = last;
                stream.WriteByte((byte)(b ^ 0xFF));
            }

            Assert.Equal("the audio didn't come out identical", CoverCleaner.Verify(original.Path, copy, Large));
        }
        finally
        {
            File.Delete(copy);
        }
    }

    [Theory]
    [InlineData("sample.flac")]
    [InlineData("sample.mp3")]
    public void The_check_catches_changed_tags_and_the_wrong_cover(string fixture)
    {
        using var original = WithThreeCovers(fixture);
        var copy = original.Path + ".copy" + Path.GetExtension(original.Path);
        try
        {
            File.Copy(original.Path, copy);
            using (var tagged = TagLib.File.Create(copy))
            {
                tagged.Tag.Pictures = [Picture(Small)];
                tagged.Save();
            }

            Assert.Equal("the cover left wasn't the best one", CoverCleaner.Verify(original.Path, copy, Large));

            using (var tagged = TagLib.File.Create(copy))
            {
                tagged.Tag.Pictures = [Picture(Large)];
                tagged.Tag.Title = "Changed";
                tagged.Save();
            }

            Assert.Equal("the tags didn't read back the same", CoverCleaner.Verify(original.Path, copy, Large));
        }
        finally
        {
            File.Delete(copy);
        }
    }

    [Theory]
    [InlineData("sample.flac")]
    [InlineData("sample.mp3")]
    public void A_file_playback_holds_is_let_go_for_the_swap(string fixture)
    {
        using var file = WithThreeCovers(fixture);
        using var playback = new HoldingPlayback(file.Path);

        var result = CoverCleaner.Clean(file.Path, playback);

        Assert.Equal(CoverCleanOutcome.Cleaned, result.Outcome);
        Assert.Equal([file.Path], playback.Released);
        using var reread = TagLib.File.Create(file.Path);
        Assert.Single(reread.Tag.Pictures);
    }

    [Fact]
    public void Padding_already_small_is_not_rewritten()
    {
        using var file = WithThreeCovers("sample.flac");
        Assert.Equal(CoverCleanOutcome.Cleaned, CoverCleaner.Clean(file.Path).Outcome);

        var target = file.Path + ".again";
        Assert.False(TagPadding.TryShrink(file.Path, target));
        Assert.False(File.Exists(target));
    }

    [Fact]
    public void An_unsynchronised_id3_tag_is_left_alone()
    {
        using var file = WithThreeCovers("sample.mp3");
        var bytes = File.ReadAllBytes(file.Path);
        bytes[5] |= 0x80;
        File.WriteAllBytes(file.Path, bytes);

        Assert.Null(TagPadding.Read(file.Path));
        Assert.False(TagPadding.TryShrink(file.Path, file.Path + ".x"));
    }

    /// <summary>Holds the file as BASS does, and lets go only inside Saving.</summary>
    private sealed class HoldingPlayback(string path) : IFileHolder, IDisposable
    {
        private FileStream? _held = Hold(path);

        public List<string> Released { get; } = [];

        private static FileStream Hold(string p) =>
            new(p, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

        public bool HoldsFile(string p) => _held is not null && p == path;

        public T Saving<T>(string p, Func<bool> needsRelease, Func<T> save)
        {
            if (!HoldsFile(p) || !needsRelease())
                return save();

            Released.Add(p);
            _held!.Dispose();
            _held = null;
            try
            {
                return save();
            }
            finally
            {
                _held = Hold(path);
            }
        }

        public void Dispose() => _held?.Dispose();
    }
}
