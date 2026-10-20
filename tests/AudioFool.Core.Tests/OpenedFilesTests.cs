using AudioFool.Core.Library;

namespace AudioFool.Core.Tests;

public sealed class OpenedFilesTests
{
    private static readonly HashSet<string> OnDisk = new(StringComparer.OrdinalIgnoreCase)
    {
        @"C:\Users\me\Downloads\Song.flac",
        @"D:\Music\Artist\Album\01 One.flac",
        @"C:\Users\me\Downloads\notes.txt",
    };

    private static IReadOnlyList<string> Playable(params string[] args) => OpenedFiles.Playable(args, OnDisk.Contains);

    [Fact]
    public void A_song_on_disk_is_kept_as_a_full_path()
    {
        Assert.Equal([@"C:\Users\me\Downloads\Song.flac"], Playable(@"C:\Users\me\Downloads\Song.flac"));
    }

    [Fact]
    public void Missing_files_and_other_kinds_are_dropped()
    {
        Assert.Empty(Playable(@"C:\Users\me\Downloads\gone.flac", @"C:\Users\me\Downloads\notes.txt", "", "  "));
    }

    [Fact]
    public void Quotes_are_stripped_and_a_file_given_twice_counts_once()
    {
        Assert.Equal(
            [@"C:\Users\me\Downloads\Song.flac", @"D:\Music\Artist\Album\01 One.flac"],
            Playable("\"C:\\Users\\me\\Downloads\\Song.flac\"", @"D:\Music\Artist\Album\01 One.flac", @"c:\users\me\downloads\song.flac"));
    }

    [Fact]
    public void A_batch_plays_by_folder_then_name_with_numbers_by_value()
    {
        var order = OpenedFiles.InPlayOrder(
        [
            @"D:\Music\B\10 Ten.flac",
            @"D:\Music\B\2 Two.flac",
            @"D:\Music\A\z.flac",
            @"D:\Music\B\01 One.flac",
        ]);

        Assert.Equal(
            [@"D:\Music\A\z.flac", @"D:\Music\B\01 One.flac", @"D:\Music\B\2 Two.flac", @"D:\Music\B\10 Ten.flac"],
            order);
    }

    [Fact]
    public void Names_compare_without_case()
    {
        Assert.Equal(
            [@"C:\x\apple.flac", @"C:\x\Banana.flac"],
            OpenedFiles.InPlayOrder([@"C:\x\Banana.flac", @"C:\x\apple.flac"]));
    }

    [Fact]
    public void A_file_under_a_music_folder_may_be_in_the_library()
    {
        string[] folders = [@"D:\Music", @"E:\More Music\"];

        Assert.True(OpenedFiles.InMusicFolders(@"D:\Music\Artist\Album\01 One.flac", folders));
        Assert.True(OpenedFiles.InMusicFolders(@"e:\more music\x.flac", folders));
        Assert.False(OpenedFiles.InMusicFolders(@"D:\Musical\x.flac", folders));
        Assert.False(OpenedFiles.InMusicFolders(@"C:\Users\me\Downloads\Song.flac", folders));
    }
}
