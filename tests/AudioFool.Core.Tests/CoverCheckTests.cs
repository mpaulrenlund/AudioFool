using AudioFool.Core.Art;
using AudioFool.Core.Models;

namespace AudioFool.Core.Tests;

/// <summary>The library-wide cover check: what it reads, what it remembers, and the Statistics row.</summary>
public class CoverCheckTests
{
    private static readonly DateTime Stamp = new(2026, 10, 8, 12, 0, 0, DateTimeKind.Utc);

    private static Track Song(string path, string album = "Album", long size = 1000) => new()
    {
        FilePath = path, FileSize = size, ModifiedUtc = Stamp, Kind = "FLAC",
        Title = Path.GetFileName(path), Artist = "Artist", Album = album,
    };

    private static readonly Dictionary<string, CoverFinding> Findings = new()
    {
        [@"D:\a.flac"] = new CoverFinding(3, 2_000_000),
        [@"D:\b.flac"] = new CoverFinding(2, 500_000),
        [@"D:\c.flac"] = new CoverFinding(1, 0),
        [@"D:\d.flac"] = new CoverFinding(0, 0),
    };

    private static Task<CoverScanSummary> Run(IReadOnlyList<Track> tracks, CoverCache cache, List<string>? read = null) =>
        CoverScanner.RunAsync(tracks, cache, null, CancellationToken.None, workers: 2,
            survey: path =>
            {
                lock (Findings)
                    read?.Add(path);
                return Findings.GetValueOrDefault(path);
            },
            exists: path => path.StartsWith(@"D:\"));

    [Fact]
    public void The_row_counts_tracks_albums_and_space_with_extra_covers()
    {
        Track[] tracks =
        [
            Song(@"D:\a.flac", "One"), Song(@"D:\b.flac", "Two"), Song(@"D:\c.flac", "Two"), Song(@"D:\d.flac", "Three"),
        ];
        var cache = CoverCache.Empty();
        var summary = Run(tracks, cache).Result;

        Assert.Equal((4, 2), (summary.Checked, summary.Found));
        var stats = CoverStatistics.Compute(tracks, cache);
        Assert.Equal((4, 4, 2, 2, 2_500_000L), (stats.Total, stats.Checked, stats.Tracks, stats.Albums, stats.Freeable));
        Assert.True(stats.Filter!.ShowsExtraCovers);
        Assert.Equal([@"D:\a.flac", @"D:\b.flac"], stats.Filter.Apply(tracks).Select(t => t.FilePath));
    }

    [Fact]
    public void A_checked_track_is_not_read_again_until_it_changes()
    {
        var a = Song(@"D:\a.flac");
        var cache = CoverCache.Empty();
        Run([a], cache).Wait();

        var read = new List<string>();
        Run([a], cache, read).Wait();
        Assert.Empty(read);

        // Cleaned: a new size, so the old result no longer applies and it is read again.
        var cleaned = Song(@"D:\a.flac", size: 900);
        Assert.Empty(CoverStatistics.Compute([cleaned], CoverCache.Load("no such file")).Filter?.Apply([cleaned]) ?? []);
        Run([cleaned], cache, read).Wait();
        Assert.Equal([@"D:\a.flac"], read);
    }

    [Fact]
    public void A_missing_file_is_not_recorded()
    {
        var gone = Song(@"E:\gone.flac");
        var cache = CoverCache.Empty();

        var summary = Run([gone], cache).Result;

        Assert.Equal(1, summary.Missing);
        Assert.False(cache.TryGet(gone, out _));
    }

    [Fact]
    public void Results_survive_a_save_and_a_new_drive_letter()
    {
        var path = Path.Combine(Path.GetTempPath(), $"AudioFool-covers-{Guid.NewGuid():N}.json");
        try
        {
            var cache = CoverCache.Empty();
            Run([Song(@"D:\a.flac")], cache).Wait();
            cache.Save(path);

            var loaded = CoverCache.Load(path);
            Assert.True(loaded.TryGet(Song(@"F:\a.flac"), out var finding));
            Assert.Equal(new CoverFinding(3, 2_000_000), finding);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void No_extra_covers_means_no_filter()
    {
        var c = Song(@"D:\c.flac");
        var cache = CoverCache.Empty();
        Run([c], cache).Wait();

        var stats = CoverStatistics.Compute([c], cache);

        Assert.Equal((1, 0), (stats.Checked, stats.Tracks));
        Assert.Null(stats.Filter);
    }
}
