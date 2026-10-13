using AudioFool.Core.Art;
using AudioFool.Core.Models;

namespace AudioFool.Core.Tests;

/// <summary>The PNG COVERS section: embedded PNGs from the cover check, and .png files in the folders.</summary>
public sealed class PngCoverTests : IDisposable
{
    private static readonly DateTime Stamp = new(2026, 10, 13, 12, 0, 0, DateTimeKind.Utc);

    private readonly string _root = Path.Combine(Path.GetTempPath(), $"AudioFool-png-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private static Track Song(string path, string album = "Album") => new()
    {
        FilePath = path, FileSize = 1000, ModifiedUtc = Stamp, Kind = "FLAC",
        Title = Path.GetFileName(path), Artist = "Artist", Album = album,
    };

    private string Folder(string name)
    {
        var folder = Path.Combine(_root, name);
        Directory.CreateDirectory(folder);
        return folder;
    }

    [Fact]
    public void The_embedded_row_counts_songs_albums_and_png_bytes()
    {
        Track[] tracks =
        [
            Song(@"D:\a.flac", "One"), Song(@"D:\b.flac", "One"), Song(@"D:\c.flac", "Two"), Song(@"D:\d.flac", "Three"),
        ];
        var cache = CoverCache.Empty();
        cache.Set(tracks[0], new CoverFinding(1, 0, 400_000));
        cache.Set(tracks[1], new CoverFinding(2, 50_000, 300_000));
        cache.Set(tracks[2], new CoverFinding(1, 0));
        // d.flac not read yet.

        var stats = PngCoverStatistics.Compute(tracks, cache);

        Assert.Equal((4, 3, 1), (stats.Total, stats.Checked, stats.Unchecked));
        Assert.Equal((2, 1, 700_000L), (stats.EmbeddedTracks, stats.EmbeddedAlbums, stats.EmbeddedBytes));
        Assert.Equal(PngCoverStatistics.EmbeddedLabel, stats.EmbeddedFilter!.Description);
        Assert.Equal([@"D:\a.flac", @"D:\b.flac"], stats.EmbeddedFilter.Apply(tracks).Select(t => t.FilePath));
        Assert.Null(stats.Files);
    }

    [Fact]
    public void No_embedded_png_means_no_filter()
    {
        var song = Song(@"D:\a.flac");
        var cache = CoverCache.Empty();
        cache.Set(song, new CoverFinding(1, 0));

        var stats = PngCoverStatistics.Compute([song], cache);

        Assert.Equal(0, stats.EmbeddedTracks);
        Assert.Null(stats.EmbeddedFilter);
    }

    [Fact]
    public void A_changed_file_leaves_the_embedded_filter()
    {
        var song = Song(@"D:\a.flac");
        var cache = CoverCache.Empty();
        cache.Set(song, new CoverFinding(1, 0, 400_000));
        var filter = PngCoverStatistics.Compute([song], cache).EmbeddedFilter!;

        // Converted: same file, new size and time.
        var converted = new Track
        {
            FilePath = song.FilePath, FileSize = 900, ModifiedUtc = Stamp.AddMinutes(1), Kind = "FLAC",
            Title = song.Title, Artist = song.Artist, Album = song.Album,
        };

        Assert.Empty(filter.Apply([converted]));
    }

    [Fact]
    public void Png_files_of_any_name_are_counted_per_folder()
    {
        var one = Folder("One");
        var two = Folder("[2014] Two, Deluxe");
        var three = Folder("Three");
        File.WriteAllBytes(Path.Combine(one, "cover.png"), new byte[300]);
        File.WriteAllBytes(Path.Combine(one, "back.PNG"), new byte[200]);
        File.WriteAllBytes(Path.Combine(two, "folder.png"), new byte[100]);
        File.WriteAllBytes(Path.Combine(three, "cover.jpg"), new byte[50]);
        File.WriteAllBytes(Path.Combine(three, "notes.pngx"), new byte[10]);

        Track[] tracks =
        [
            Song(Path.Combine(one, "1.flac"), "One"), Song(Path.Combine(one, "2.flac"), "One"),
            Song(Path.Combine(two, "1.flac"), "Two"), Song(Path.Combine(three, "1.flac"), "Three"),
        ];

        var files = PngCoverStatistics.CountFiles(tracks);

        Assert.Equal((3, 600L, 2, 3, 2), (files.Files, files.Bytes, files.Folders, files.AllFolders, files.Albums));
        Assert.Equal(PngCoverStatistics.FilesLabel, files.Filter!.Description);
        Assert.Equal(3, files.Filter.Apply(tracks).Count);
        Assert.DoesNotContain(files.Filter.Apply(tracks), t => t.Album == "Three");
    }

    [Fact]
    public void An_unreachable_folder_is_skipped()
    {
        var missing = Enumerable.Range('F', 'Z' - 'F' + 1)
            .Select(c => $"{(char)c}:\\")
            .First(root => !Directory.Exists(root));

        var files = PngCoverStatistics.CountFiles([Song(Path.Combine(missing, "Music", "1.flac"))]);

        Assert.Equal((0, 0, 1), (files.Files, files.Folders, files.AllFolders));
        Assert.Null(files.Filter);
    }

    [Fact]
    public void The_cache_drops_results_from_before_png_bytes()
    {
        var path = Path.Combine(Folder("cache"), "covers.json");
        File.WriteAllText(path, """{"Version":1,"Results":{"a.flac|1000|1":{"Pictures":1,"Freeable":0}}}""");

        var song = new Track { FilePath = @"D:\a.flac", FileSize = 1000, ModifiedUtc = new DateTime(1, DateTimeKind.Utc) };

        Assert.False(CoverCache.Load(path).TryGet(song, out _));
    }
}
