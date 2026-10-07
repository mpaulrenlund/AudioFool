using System.Security.Cryptography;
using AudioFool.Core.Library;

namespace AudioFool.Core.Tests;

/// <summary>
/// Saving a file that playback has open. BASS holds the playing track and the
/// next one open for reading and lets others write; the writer must share too,
/// and a save that would move the audio under the open stream must be made
/// while playback lets go of the file.
/// </summary>
public class TagWriteSharingTests
{
    /// <summary>Opens the file the way BASS does: read access, others may read and write.</summary>
    private static FileStream HoldLikePlayback(string path) =>
        new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    private static readonly TracksTagEdit SmallEdit = new() { Title = "Retitled" };

    private static readonly TracksTagEdit LargeEdit = new()
    {
        Details = new TagDetailsEdit { Comment = new string('x', 300_000) },
    };

    /// <summary>
    /// Playback holding one file as BASS would. While released, the file has no
    /// handle on it; the write is checked to have run inside the release.
    /// </summary>
    private sealed class FakePlayback(string path) : IFileHolder, IDisposable
    {
        private FileStream? _held = HoldLikePlayback(path);

        public List<string> Saved { get; } = [];
        public List<string> Released { get; } = [];
        public long? SizeWhileReleased { get; private set; }

        public bool HoldsFile(string p) => _held is not null && p == path;

        public T Saving<T>(string p, Func<bool> needsRelease, Func<T> write)
        {
            Saved.Add(p);
            if (!HoldsFile(p) || !needsRelease())
                return write();

            Released.Add(p);
            _held!.Dispose();
            _held = null;
            try
            {
                var result = write();
                SizeWhileReleased = new FileInfo(path).Length;
                return result;
            }
            finally
            {
                _held = HoldLikePlayback(path);
            }
        }

        public void Dispose() => _held?.Dispose();
    }

    [Theory]
    [InlineData("sample.flac")]
    [InlineData("sample.mp3")]
    public void A_file_open_for_playback_can_be_saved(string fixture)
    {
        using var file = new TempAudioFile(fixture);
        var track = TagReader.Read(file.Path);

        TagWriteResult result;
        using (HoldLikePlayback(file.Path))
            result = TagWriter.WriteSelectedTrackTags(track, SmallEdit);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal("Retitled", TagReader.Read(file.Path).Title);
    }

    [Theory]
    [InlineData("sample.flac")]
    [InlineData("sample.mp3")]
    public void A_held_file_takes_a_save_that_keeps_its_size_without_being_released(string fixture)
    {
        using var file = new TempAudioFile(fixture);

        // The MP3 fixture has no tag, so any title grows it. One save first gives
        // it a tag and the padding after it, as the library's files have.
        Assert.True(TagWriter.WriteSelectedTrackTags(TagReader.Read(file.Path), new TracksTagEdit { Title = "Seed" }).Success);
        var track = TagReader.Read(file.Path);
        var size = new FileInfo(file.Path).Length;

        TagWriteResult result;
        using (var playback = new FakePlayback(file.Path))
        {
            result = TagWriter.WriteSelectedTrackTags(track, SmallEdit, playback);
            Assert.Empty(playback.Released);
            Assert.Equal([file.Path], playback.Saved);
        }

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal(size, new FileInfo(file.Path).Length);
        Assert.Equal("Retitled", TagReader.Read(file.Path).Title);
    }

    [Theory]
    [InlineData("sample.flac")]
    [InlineData("sample.mp3")]
    public void A_resizing_save_to_a_held_file_is_made_while_playback_lets_go_of_it(string fixture)
    {
        using var file = new TempAudioFile(fixture);
        var track = TagReader.Read(file.Path);
        var size = new FileInfo(file.Path).Length;

        TagWriteResult result;
        using (var playback = new FakePlayback(file.Path))
        {
            result = TagWriter.WriteSelectedTrackTags(track, LargeEdit, playback);
            Assert.Equal([file.Path], playback.Released);
            Assert.Equal([file.Path], playback.Saved);
            Assert.True(playback.SizeWhileReleased > size);
        }

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal(300_000, TagReader.ReadDetails(file.Path)!.Comment.Length);
        // Only this test's own copy: other runs (the app, a parallel test run) make theirs in the same folder.
        Assert.Empty(Directory.GetFiles(Path.GetTempPath(), $"{TagWriter.TrialPrefix}*-{Path.GetFileName(file.Path)}"));
    }

    [Fact]
    public void A_held_file_whose_trial_save_fails_is_not_released_and_left_untouched()
    {
        using var file = new TempAudioFile("sample.flac");
        var track = TagReader.Read(file.Path);
        File.WriteAllBytes(file.Path, [1, 2, 3, 4]);
        var before = Hash(file.Path);

        TagWriteResult result;
        using (var playback = new FakePlayback(file.Path))
        {
            result = TagWriter.WriteSelectedTrackTags(track, LargeEdit, playback);
            Assert.Empty(playback.Released);
        }

        Assert.False(result.Success);
        Assert.Equal(before, Hash(file.Path));
    }

    [Theory]
    [InlineData("sample.flac")]
    [InlineData("sample.mp3")]
    public void The_same_resizing_save_goes_through_when_nothing_holds_the_file(string fixture)
    {
        using var file = new TempAudioFile(fixture);
        var track = TagReader.Read(file.Path);
        var size = new FileInfo(file.Path).Length;

        var result = TagWriter.WriteSelectedTrackTags(track, LargeEdit);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.True(new FileInfo(file.Path).Length > size);
        Assert.Equal(300_000, TagReader.ReadDetails(file.Path)!.Comment.Length);
    }

    /// <summary>
    /// A file playback isn't holding is still saved through it, so that playback
    /// can keep it from being opened mid-save; nothing is tried or released.
    /// </summary>
    [Fact]
    public void A_save_to_a_file_playback_does_not_hold_still_goes_through_playback()
    {
        using var file = new TempAudioFile("sample.flac");
        using var other = new TempAudioFile("sample.mp3");
        var track = TagReader.Read(file.Path);

        TagWriteResult result;
        using (var playback = new FakePlayback(other.Path))
        {
            result = TagWriter.WriteSelectedTrackTags(track, LargeEdit, playback);
            Assert.Equal([file.Path], playback.Saved);
            Assert.Empty(playback.Released);
        }

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal(300_000, TagReader.ReadDetails(file.Path)!.Comment.Length);
    }
}
