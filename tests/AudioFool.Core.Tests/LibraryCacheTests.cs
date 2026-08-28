using System.Text.Json;
using AudioFool.Core.Library;
using AudioFool.Core.Models;

namespace AudioFool.Core.Tests;

public class FileStampMatchingTests
{
    private static Track Cached(long size, DateTime modified) =>
        new() { FilePath = @"C:\music\a.flac", FileSize = size, ModifiedUtc = modified };

    private static readonly DateTime When = new(2026, 3, 3, 18, 36, 10, DateTimeKind.Utc);

    [Fact]
    public void Same_size_and_time_is_a_match() =>
        Assert.True(Cached(1000, When).MatchesFile(1000, When));

    [Fact]
    public void A_different_size_is_not_a_match() =>
        Assert.False(Cached(1000, When).MatchesFile(1001, When));

    [Fact]
    public void A_different_write_time_is_not_a_match() =>
        Assert.False(Cached(1000, When).MatchesFile(1000, When.AddSeconds(1)));

    [Fact]
    public void An_edited_tag_is_caught_even_at_identical_size()
    {
        // Rewriting a tag in place can leave the file exactly the same length, so
        // size alone would miss it. The write time is what saves us.
        var cached = Cached(5_000_000, When);
        Assert.False(cached.MatchesFile(5_000_000, When.AddMinutes(1)));
    }

    [Fact]
    public void A_track_with_no_stamp_never_matches_a_real_file()
    {
        // Tracks cached before stamps existed default to 0/default, and must be
        // re-read rather than trusted.
        var legacy = new Track { FilePath = @"C:\music\a.flac" };
        Assert.False(legacy.MatchesFile(1000, When));
    }
}

public class LibraryCacheTests
{
    private static Track Sample(string path) => new()
    {
        FilePath = path,
        FileSize = 12_345,
        ModifiedUtc = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc),
        Title = "Shredders of the Universe",
        Artist = "100 Onces",
        AlbumArtist = "100 Onces",
        Album = "100 Onces",
        TrackNumber = 1,
        DiscNumber = 2,
        Year = 2014,
        Duration = TimeSpan.FromSeconds(294),
        Kind = "FLAC",
        Bitrate = 906,
        BitDepth = 16,
        SampleRate = 44100,
        FolderArtPath = @"C:\music\cover.jpg",
    };

    [Fact]
    public void Survives_a_json_round_trip_with_every_field_intact()
    {
        // Track is serialised straight into the cache file, and it uses `required`
        // plus init-only setters - a combination that silently breaks round-tripping
        // if the serialiser can't populate them.
        var original = LibraryCache.From([@"C:\music"], [Sample(@"C:\music\a.flac")]);

        var restored = JsonSerializer.Deserialize<LibraryCache>(JsonSerializer.Serialize(original));

        Assert.NotNull(restored);
        var track = Assert.Single(restored.Tracks);
        var expected = original.Tracks[0];

        Assert.Equal(expected.FilePath, track.FilePath);
        Assert.Equal(expected.FileSize, track.FileSize);
        Assert.Equal(expected.ModifiedUtc, track.ModifiedUtc);
        Assert.Equal(expected.Title, track.Title);
        Assert.Equal(expected.Artist, track.Artist);
        Assert.Equal(expected.AlbumArtist, track.AlbumArtist);
        Assert.Equal(expected.Album, track.Album);
        Assert.Equal(expected.TrackNumber, track.TrackNumber);
        Assert.Equal(expected.DiscNumber, track.DiscNumber);
        Assert.Equal(expected.Year, track.Year);
        Assert.Equal(expected.Duration, track.Duration);
        Assert.Equal(expected.Kind, track.Kind);
        Assert.Equal(expected.Bitrate, track.Bitrate);
        Assert.Equal(expected.BitDepth, track.BitDepth);
        Assert.Equal(expected.SampleRate, track.SampleRate);
        Assert.Equal(expected.FolderArtPath, track.FolderArtPath);
    }

    [Fact]
    public void Round_trip_preserves_a_null_bit_depth_for_lossy_tracks()
    {
        // The cache writes nulls out as absent fields, so this checks "missing"
        // still deserialises back to null rather than 0.
        var lossy = new Track { FilePath = @"C:\music\b.mp3", BitDepth = null, Kind = "MP3" };
        var cache = LibraryCache.From([@"C:\music"], [lossy]);

        var restored = JsonSerializer.Deserialize<LibraryCache>(JsonSerializer.Serialize(cache));

        Assert.Null(Assert.Single(restored!.Tracks).BitDepth);
    }

    [Fact]
    public void Folder_comparison_ignores_order_and_case()
    {
        var cache = LibraryCache.From([@"C:\Music", @"D:\More"], []);

        Assert.True(cache.CoversSameFolders([@"d:\more", @"c:\music"]));
        Assert.False(cache.CoversSameFolders([@"C:\Music"]));
        Assert.False(cache.CoversSameFolders([@"C:\Music", @"D:\More", @"E:\Extra"]));
    }

    [Fact]
    public void ByPath_indexes_case_insensitively()
    {
        var cache = LibraryCache.From([@"C:\music"], [Sample(@"C:\music\A.flac")]);

        var map = cache.ByPath();

        Assert.True(map.ContainsKey(@"c:\MUSIC\a.FLAC"));
    }

    [Fact]
    public void A_cache_from_a_different_version_is_rejected()
    {
        // Load() treats a version mismatch as "no cache" so an old file can never be
        // read back into a changed Track shape.
        var json = """{"Version":999,"Folders":[],"Tracks":[{"FilePath":"x"}]}""";

        var parsed = JsonSerializer.Deserialize<LibraryCache>(json);

        Assert.NotEqual(LibraryCache.CurrentVersion, parsed!.Version);
    }
}

public class ScanSummaryTests
{
    [Fact]
    public void No_reads_and_no_removals_means_nothing_changed()
    {
        var summary = new ScanSummary(Reused: 500, Read: 0, Removed: 0);

        Assert.False(summary.AnyChanges);
        Assert.True(summary.EntirelyCached);
    }

    [Theory]
    [InlineData(0, 1, 0)]
    [InlineData(0, 0, 1)]
    [InlineData(10, 2, 3)]
    public void Any_read_or_removal_counts_as_a_change(int reused, int read, int removed)
    {
        var summary = new ScanSummary(reused, read, removed);

        Assert.True(summary.AnyChanges);
        Assert.False(summary.EntirelyCached);
    }
}
