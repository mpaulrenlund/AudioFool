using System.Reflection;
using AudioFool.Core.Library;
using AudioFool.Core.Models;

namespace AudioFool.Core.Tests;

public class PathContainmentTests
{
    [Theory]
    [InlineData(@"E:\Music\a.flac", @"E:\Music", true)]
    [InlineData(@"E:\Music\Sub\a.flac", @"E:\Music", true)]
    [InlineData(@"e:\music\a.flac", @"E:\Music", true)]
    [InlineData(@"E:\Music", @"E:\Music", true)]
    [InlineData(@"E:\Music\a.flac", @"E:\Music\", true)]
    [InlineData(@"F:\Music\a.flac", @"E:\Music", false)]
    [InlineData(@"E:\Other\a.flac", @"E:\Music", false)]
    public void Containment_is_recognised(string path, string folder, bool expected) =>
        Assert.Equal(expected, LibraryRelocator.IsUnder(path, folder));

    [Fact]
    public void A_sibling_with_a_shared_prefix_is_not_inside()
    {
        // Plain StartsWith would wrongly say "E:\MusicOld" is inside "E:\Music",
        // and a relocation would then rewrite paths that don't belong to it.
        Assert.False(LibraryRelocator.IsUnder(@"E:\MusicOld\a.flac", @"E:\Music"));
        Assert.False(LibraryRelocator.IsUnder(@"E:\Musical\a.flac", @"E:\Music"));
    }
}

public class RebaseTests
{
    [Theory]
    [InlineData(@"E:\Music\Artist\a.flac", @"E:\Music", @"F:\Music", @"F:\Music\Artist\a.flac")]
    [InlineData(@"E:\Music\a.flac", @"E:\Music", @"D:\Media\Music", @"D:\Media\Music\a.flac")]
    public void Paths_move_to_the_new_root(string path, string oldRoot, string newRoot, string expected) =>
        Assert.Equal(expected, LibraryRelocator.Rebase(path, oldRoot, newRoot));

    [Fact]
    public void Paths_outside_the_old_root_are_left_alone()
    {
        const string other = @"C:\Elsewhere\a.flac";
        Assert.Equal(other, LibraryRelocator.Rebase(other, @"E:\Music", @"F:\Music"));
    }

    [Fact]
    public void Rebasing_a_library_moves_both_the_file_and_its_folder_art()
    {
        Track[] tracks =
        [
            new()
            {
                FilePath = @"E:\Music\Artist\Album\01.flac",
                FolderArtPath = @"E:\Music\Artist\Album\cover.jpg",
            },
            new() { FilePath = @"C:\Other\keep.flac" },
        ];

        var moved = LibraryRelocator.Rebase(tracks, @"E:\Music", @"F:\Music");

        Assert.Equal(@"F:\Music\Artist\Album\01.flac", moved[0].FilePath);
        Assert.Equal(@"F:\Music\Artist\Album\cover.jpg", moved[0].FolderArtPath);
        Assert.Equal(@"C:\Other\keep.flac", moved[1].FilePath);
    }

    [Fact]
    public void Relocating_a_track_carries_every_other_field_across()
    {
        // Reflection rather than a hand-written list, so a property added to Track
        // later can't silently start getting dropped on relocation.
        var original = new Track
        {
            FilePath = @"E:\Music\a.flac",
            FolderArtPath = @"E:\Music\cover.jpg",
            FileSize = 4242,
            ModifiedUtc = new DateTime(2026, 5, 6, 7, 8, 9, DateTimeKind.Utc),
            AddedUtc = new DateTime(2026, 4, 1, 2, 3, 4, DateTimeKind.Utc),
            TrackNumber = 3,
            Title = "Title",
            Artist = "Artist",
            AlbumArtist = "Album Artist",
            Album = "Album",
            Duration = TimeSpan.FromSeconds(123),
            DiscNumber = 2,
            Year = 1999,
            Kind = "FLAC",
            Bitrate = 900,
            BitDepth = 24,
            SampleRate = 96000,
        };

        var moved = original.Relocated(@"F:\Music\a.flac", @"F:\Music\cover.jpg");

        var carried = typeof(Track)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanRead
                        && p.Name is not (nameof(Track.FilePath) or nameof(Track.FolderArtPath))
                        // Computed from FilePath, so it is expected to change.
                        && p.Name != nameof(Track.DisplayTitle));

        foreach (var property in carried)
        {
            Assert.Equal(property.GetValue(original), property.GetValue(moved));
        }

        Assert.Equal(@"F:\Music\a.flac", moved.FilePath);
        Assert.Equal(@"F:\Music\cover.jpg", moved.FolderArtPath);
    }
}

public class UnavailableFolderTests
{
    /// <summary>
    /// A path on a drive letter that cannot plausibly be mounted, standing in for
    /// an unplugged portable drive.
    /// </summary>
    private const string MissingDrive = @"Q:\Music";

    private static Track OnMissingDrive(string name) => new()
    {
        FilePath = $@"{MissingDrive}\Artist\{name}.flac",
        Title = name,
        Artist = "Artist",
        Album = "Album",
        FileSize = 1000,
        ModifiedUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
    };

    [Fact]
    public async Task An_unplugged_drive_does_not_look_like_a_deleted_library()
    {
        // The whole point: opening the app with the drive disconnected must not
        // report every track as removed, because the cache is then overwritten with
        // nothing and reconnecting costs a full rescan.
        Track[] cached = [OnMissingDrive("one"), OnMissingDrive("two"), OnMissingDrive("three")];
        var known = cached.ToDictionary(t => t.FilePath, t => t, StringComparer.OrdinalIgnoreCase);

        var result = await LibraryScanner.ScanAsync([MissingDrive], known);

        Assert.Equal(0, result.Summary.Removed);
        Assert.Equal(3, result.Library.AllTracks.Count);
        Assert.True(result.AnyFolderUnavailable);
        Assert.Equal(MissingDrive, Assert.Single(result.UnavailableFolders));
    }

    [Fact]
    public async Task Tracks_on_an_unplugged_drive_stay_browsable()
    {
        Track[] cached = [OnMissingDrive("one"), OnMissingDrive("two")];
        var known = cached.ToDictionary(t => t.FilePath, t => t, StringComparer.OrdinalIgnoreCase);

        var result = await LibraryScanner.ScanAsync([MissingDrive], known);

        var artist = Assert.Single(result.Library.Artists);
        Assert.Equal("Artist", artist.Name);
        Assert.Equal(2, artist.TrackCount);
    }

    [Fact]
    public async Task An_empty_but_present_folder_really_does_mean_removed()
    {
        // The counterpart: a folder that IS reachable and now has nothing in it
        // genuinely means the files are gone, and must be reported as such.
        var temp = Path.Combine(Path.GetTempPath(), "audiofool-empty-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);

        try
        {
            var known = new Dictionary<string, Track>(StringComparer.OrdinalIgnoreCase)
            {
                [Path.Combine(temp, "gone.flac")] = new() { FilePath = Path.Combine(temp, "gone.flac") },
            };

            var result = await LibraryScanner.ScanAsync([temp], known);

            Assert.False(result.AnyFolderUnavailable);
            Assert.Equal(1, result.Summary.Removed);
            Assert.Empty(result.Library.AllTracks);
        }
        finally
        {
            Directory.Delete(temp, recursive: true);
        }
    }

    [Fact]
    public void A_present_folder_is_never_reported_as_moved()
    {
        var temp = Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar);

        Assert.Null(LibraryRelocator.FindRelocation(temp, []));
    }

    [Fact]
    public void A_missing_folder_with_no_candidate_drive_is_not_guessed_at()
    {
        // Better to report the drive as absent than to remap onto something wrong.
        Assert.Null(LibraryRelocator.FindRelocation(@"Q:\NoSuchFolderAnywhere", []));
    }
}

public class RemovedCountTests
{
    /// <summary>Writes a real file so the scanner has something to stat.</summary>
    private static string WriteFile(string folder, string name, string content)
    {
        var path = Path.Combine(folder, name);
        File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public async Task A_changed_file_is_not_also_counted_as_removed()
    {
        // A file whose timestamp moved is "changed", never "removed". Counting it as
        // both made a copy that didn't preserve timestamps look like 72 files had
        // been deleted and 72 new ones appeared.
        var temp = Path.Combine(Path.GetTempPath(), "audiofool-changed-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);

        try
        {
            var kept = WriteFile(temp, "kept.mp3", "aaaa");
            var touched = WriteFile(temp, "touched.mp3", "bbbb");

            // Cache both as they are now.
            var first = await LibraryScanner.ScanAsync([temp]);
            var known = first.Library.AllTracks.ToDictionary(
                t => t.FilePath, t => t, StringComparer.OrdinalIgnoreCase);

            Assert.Equal(2, known.Count);

            // Move one file's write time, exactly as a non-preserving copy would.
            File.SetLastWriteTimeUtc(touched, File.GetLastWriteTimeUtc(touched).AddHours(3));

            var second = await LibraryScanner.ScanAsync([temp], known);

            Assert.Equal(1, second.Summary.Read);        // the touched one
            Assert.Equal(1, second.Summary.Reused);      // the untouched one
            Assert.Equal(0, second.Summary.Removed);     // nothing was deleted
            Assert.Equal(2, second.Library.AllTracks.Count);

            Assert.True(File.Exists(kept));
        }
        finally
        {
            Directory.Delete(temp, recursive: true);
        }
    }

    [Fact]
    public async Task A_genuinely_deleted_file_still_counts_as_removed()
    {
        var temp = Path.Combine(Path.GetTempPath(), "audiofool-deleted-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);

        try
        {
            WriteFile(temp, "stays.mp3", "aaaa");
            var doomed = WriteFile(temp, "goes.mp3", "bbbb");

            var first = await LibraryScanner.ScanAsync([temp]);
            var known = first.Library.AllTracks.ToDictionary(
                t => t.FilePath, t => t, StringComparer.OrdinalIgnoreCase);

            File.Delete(doomed);

            var second = await LibraryScanner.ScanAsync([temp], known);

            Assert.Equal(1, second.Summary.Removed);
            Assert.Equal(0, second.Summary.Read);
            Assert.Single(second.Library.AllTracks);
        }
        finally
        {
            Directory.Delete(temp, recursive: true);
        }
    }
}
