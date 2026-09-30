using AudioFool.Core.Library;
using AudioFool.Core.Models;

namespace AudioFool.Core.Tests;

public class MusicFolderListTests
{
    private static Track T(string path) => new() { FilePath = path };

    [Theory]
    [InlineData(@"E:\Music", @"E:\Music")]
    [InlineData(@"E:\Music\", @"E:\Music")]
    [InlineData(@" E:\Music\\ ", @"E:\Music")]
    [InlineData(@"E:\", @"E:\")]
    [InlineData(@"E:", @"E:\")]
    public void Normalize_drops_a_trailing_separator_but_keeps_a_drive_root(string input, string expected) =>
        Assert.Equal(expected, MusicFolderList.Normalize(input));

    [Fact]
    public void Same_folder_ignores_case_and_a_trailing_separator()
    {
        Assert.True(MusicFolderList.SameFolder(@"E:\Music", @"e:\music\"));
        Assert.False(MusicFolderList.SameFolder(@"E:\Music", @"E:\MusicOld"));
        Assert.False(MusicFolderList.SameFolder(@"E:\Music", @"D:\Music"));
    }

    [Fact]
    public void Distinct_merges_repeats_and_keeps_the_first_in_order()
    {
        // The list found on the user's machine: E:\Music twice, once from Add folder
        // and once from the drive-letter re-point.
        var folders = MusicFolderList.Distinct(
            [@"C:\Users\Someone\Music", @"E:\Music", @"E:\Music", @"e:\music\", "", "  "]);

        Assert.Equal([@"C:\Users\Someone\Music", @"E:\Music"], folders);
    }

    [Fact]
    public void Removing_a_folder_takes_only_its_tracks()
    {
        var tracks = new[]
        {
            T(@"E:\Music\A\1.flac"),
            T(@"E:\MusicOld\B\2.flac"),
            T(@"C:\Users\Someone\Music\3.mp3"),
        };

        var left = MusicFolderList.WithoutFolder(tracks, @"E:\Music\", [@"C:\Users\Someone\Music"]);

        Assert.Equal([@"E:\MusicOld\B\2.flac", @"C:\Users\Someone\Music\3.mp3"], left.Select(t => t.FilePath));
    }

    [Fact]
    public void Removing_a_folder_keeps_tracks_another_folder_still_covers()
    {
        var tracks = new[] { T(@"E:\Music\A\1.flac"), T(@"E:\Music\B\2.flac") };

        // E:\ is still watched, so nothing under E:\Music leaves the library.
        Assert.Equal(2, MusicFolderList.WithoutFolder(tracks, @"E:\Music", [@"E:\"]).Count);

        // E:\Music\A is still watched, so only B goes.
        var left = MusicFolderList.WithoutFolder(tracks, @"E:\Music", [@"E:\Music\A"]);
        Assert.Equal([@"E:\Music\A\1.flac"], left.Select(t => t.FilePath));
    }
}
