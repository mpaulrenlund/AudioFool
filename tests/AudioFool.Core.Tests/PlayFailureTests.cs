using AudioFool.Core.Playback;
using ManagedBass;

namespace AudioFool.Core.Tests;

/// <summary>
/// A play that didn't start says why (session 71): until then every failure,
/// an unplugged drive included, blamed the format.
/// </summary>
public class PlayFailureTests
{
    private const string Song = @"D:\Music\Artist\Album\01 Song.flac";

    [Theory]
    [InlineData(false, false, Errors.FileOpen, PlayFailureKind.DriveMissing)]
    [InlineData(false, true, Errors.FileOpen, PlayFailureKind.FileMissing)]
    [InlineData(true, true, Errors.FileOpen, PlayFailureKind.FileUnopenable)]
    [InlineData(true, true, Errors.FileFormat, PlayFailureKind.Format)]
    [InlineData(true, true, Errors.Codec, PlayFailureKind.Format)]
    [InlineData(true, true, Errors.Unknown, PlayFailureKind.Format)]
    public void A_song_that_wont_open_is_told_apart(bool fileExists, bool driveThere, Errors error, PlayFailureKind expected) =>
        Assert.Equal(expected, PlayFailure.ForFile(Song, fileExists, driveThere, error).Kind);

    [Fact]
    public void Each_kind_has_its_own_line()
    {
        Assert.Equal("Can't play 01 Song.flac: the drive isn't connected.",
            new PlayFailure(PlayFailureKind.DriveMissing, Song).Describe());
        Assert.Equal("Can't play 01 Song.flac: the file isn't there any more. Refresh to update the library.",
            new PlayFailure(PlayFailureKind.FileMissing, Song).Describe());
        Assert.Equal("Couldn't play 01 Song.flac: the file couldn't be opened. Another program may have it open.",
            new PlayFailure(PlayFailureKind.FileUnopenable, Song).Describe());
        Assert.Equal("Couldn't play 01 Song.flac. The format may need an add-on that isn't installed.",
            new PlayFailure(PlayFailureKind.Format, Song).Describe());
        Assert.Equal("Couldn't play 01 Song.flac: WASAPI would not open at 44100 Hz (Busy).",
            new PlayFailure(PlayFailureKind.Output, Song, "WASAPI would not open at 44100 Hz (Busy).").Describe());
        Assert.Equal("Couldn't play 01 Song.flac: the output device wouldn't open.",
            new PlayFailure(PlayFailureKind.Output, Song).Describe());
        Assert.Equal("Couldn't play 01 Song.flac: the audio engine didn't start (No sound device).",
            new PlayFailure(PlayFailureKind.Engine, Song, "No sound device.").Describe());
    }

    [Fact]
    public void A_missing_file_on_a_drive_that_is_there_is_missing_not_unplugged()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".flac");
        Assert.Equal(PlayFailureKind.FileMissing, PlayFailure.ForFile(path, Errors.FileOpen).Kind);
    }

    [Fact]
    public void A_file_on_a_drive_letter_that_isnt_there_is_an_unplugged_drive()
    {
        var used = DriveInfo.GetDrives().Select(d => char.ToUpperInvariant(d.Name[0])).ToHashSet();
        var free = Enumerable.Range('D', 'Z' - 'D' + 1).Select(c => (char)c).First(c => !used.Contains(c));

        Assert.Equal(PlayFailureKind.DriveMissing, PlayFailure.ForFile($@"{free}:\Music\a.flac", Errors.FileOpen).Kind);
    }
}
