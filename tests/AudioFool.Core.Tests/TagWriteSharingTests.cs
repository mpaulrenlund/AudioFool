using System.Security.Cryptography;
using AudioFool.Core.Library;

namespace AudioFool.Core.Tests;

/// <summary>
/// Saving a file that playback has open. BASS holds the playing track and the
/// next one open for reading and lets others write; the writer must share too,
/// and must refuse a save that would move the audio under the open stream.
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
    public void A_held_file_takes_a_save_that_keeps_its_size(string fixture)
    {
        using var file = new TempAudioFile(fixture);

        // The MP3 fixture has no tag, so any title grows it. One save first gives
        // it a tag and the padding after it, as the library's files have.
        Assert.True(TagWriter.WriteSelectedTrackTags(TagReader.Read(file.Path), new TracksTagEdit { Title = "Seed" }).Success);
        var track = TagReader.Read(file.Path);
        var size = new FileInfo(file.Path).Length;

        TagWriteResult result;
        using (HoldLikePlayback(file.Path))
            result = TagWriter.WriteSelectedTrackTags(track, SmallEdit, holdsFile: _ => true);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal(size, new FileInfo(file.Path).Length);
        Assert.Equal("Retitled", TagReader.Read(file.Path).Title);
    }

    [Theory]
    [InlineData("sample.flac")]
    [InlineData("sample.mp3")]
    public void A_held_file_refuses_a_save_that_would_resize_it_and_is_left_untouched(string fixture)
    {
        using var file = new TempAudioFile(fixture);
        var track = TagReader.Read(file.Path);
        var before = Hash(file.Path);
        var asked = new List<string>();

        TagWriteResult result;
        using (HoldLikePlayback(file.Path))
            result = TagWriter.WriteSelectedTrackTags(track, LargeEdit, holdsFile: p => { asked.Add(p); return true; });

        Assert.False(result.Success);
        Assert.Contains("playing or up next", result.ErrorMessage);
        Assert.Equal([file.Path], asked);
        Assert.Equal(before, Hash(file.Path));
        Assert.Empty(Directory.GetFiles(Path.GetTempPath(), "AudioFool-trial-*"));
    }

    [Theory]
    [InlineData("sample.flac")]
    [InlineData("sample.mp3")]
    public void The_same_resizing_save_goes_through_when_nothing_holds_the_file(string fixture)
    {
        using var file = new TempAudioFile(fixture);
        var track = TagReader.Read(file.Path);
        var size = new FileInfo(file.Path).Length;

        var result = TagWriter.WriteSelectedTrackTags(track, LargeEdit, holdsFile: _ => false);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.True(new FileInfo(file.Path).Length > size);
        Assert.Equal(300_000, TagReader.ReadDetails(file.Path)!.Comment.Length);
    }
}
