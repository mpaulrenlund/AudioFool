using AudioFool.Core.Library;
using AudioFool.Core.Models;

namespace AudioFool.Core.Tests;

public class DriveChangeTests
{
    private static Func<string, bool> Present(params string[] folders) =>
        folder => folders.Any(f => MusicFolderList.SameFolder(f, folder));

    [Fact]
    public void A_drive_that_holds_no_music_folder_changes_nothing()
    {
        // A phone or a USB stick plugged in with the library all there.
        Assert.False(DriveChange.WorthRefresh([@"D:\Music"], [], arrived: true, Present(@"D:\Music")));
        Assert.False(DriveChange.WorthRefresh([@"D:\Music"], [], arrived: false, Present(@"D:\Music")));
    }

    [Fact]
    public void A_folder_back_under_its_own_letter_refreshes()
    {
        Assert.True(DriveChange.WorthRefresh([@"D:\Music"], [@"D:\Music"], arrived: true, Present(@"D:\Music")));
    }

    [Fact]
    public void A_folder_gone_since_the_last_scan_refreshes()
    {
        Assert.True(DriveChange.WorthRefresh([@"D:\Music"], [], arrived: false, Present()));
    }

    [Fact]
    public void A_folder_already_known_gone_refreshes_only_when_a_drive_arrives()
    {
        // The new drive may hold it under another letter; pulling out another drive can't.
        Assert.True(DriveChange.WorthRefresh([@"D:\Music"], [@"D:\Music"], arrived: true, Present()));
        Assert.False(DriveChange.WorthRefresh([@"D:\Music"], [@"D:\Music"], arrived: false, Present()));
    }

    [Fact]
    public void Folders_are_matched_however_they_are_written()
    {
        Assert.False(DriveChange.WorthRefresh([@"D:\Music"], [@"d:\music\"], arrived: false, Present()));
    }

    [Fact]
    public void One_changed_folder_among_several_is_enough()
    {
        Assert.True(DriveChange.WorthRefresh(
            [@"C:\Music", @"D:\Music"], [], arrived: false, Present(@"C:\Music")));
    }

    [Fact]
    public void No_music_folders_never_refresh()
    {
        Assert.False(DriveChange.WorthRefresh([], [], arrived: true, Present()));
    }
}

public class MovedCopyTests
{
    private static readonly LibraryRelocator.Relocation[] Moves = [new(@"D:\Music", @"F:\Music")];

    [Fact]
    public void A_moved_track_becomes_the_librarys_copy_at_the_new_path()
    {
        var old = new Track { FilePath = @"D:\Music\A\01.flac", Title = "Old" };
        var copy = new Track { FilePath = @"F:\Music\A\01.flac", Title = "Library" };
        var byPath = new Dictionary<string, Track>(StringComparer.OrdinalIgnoreCase) { [copy.FilePath] = copy };

        Assert.Same(copy, LibraryRelocator.MovedCopy(old, Moves, byPath));
    }

    [Fact]
    public void A_moved_track_the_library_lacks_is_re_pointed_itself()
    {
        var old = new Track
        {
            FilePath = @"D:\Music\A\01.flac",
            FolderArtPath = @"D:\Music\A\cover.jpg",
            Title = "Kept",
        };

        var moved = LibraryRelocator.MovedCopy(old, Moves, new Dictionary<string, Track>());

        Assert.NotNull(moved);
        Assert.Equal(@"F:\Music\A\01.flac", moved.FilePath);
        Assert.Equal(@"F:\Music\A\cover.jpg", moved.FolderArtPath);
        Assert.Equal("Kept", moved.Title);
    }

    [Fact]
    public void A_track_outside_the_moved_folders_stays()
    {
        var other = new Track { FilePath = @"C:\Users\x\Downloads\song.flac" };
        Assert.Null(LibraryRelocator.MovedCopy(other, Moves, new Dictionary<string, Track>()));
    }
}
