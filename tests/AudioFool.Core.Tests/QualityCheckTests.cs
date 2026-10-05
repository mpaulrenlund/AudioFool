using AudioFool.Core.Analysis;
using AudioFool.Core.Models;

namespace AudioFool.Core.Tests;

/// <summary>
/// The library check: the saved results, the scanner (with a fake analyser, so
/// no audio is decoded) and the Statistics rows built from them.
/// </summary>
public class QualityCheckTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "AudioFoolQualityTests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }

    private static readonly DateTime Stamp = new(2026, 10, 5, 9, 0, 0, DateTimeKind.Utc);

    private static Track Flac(string path, long size = 1000) => new()
    {
        FilePath = path, FileSize = size, ModifiedUtc = Stamp, Kind = "FLAC", BitDepth = 16, SampleRate = 44100, Title = Path.GetFileName(path),
    };

    private static Track Mp3(string path) => new()
    {
        FilePath = path, FileSize = 500, ModifiedUtc = Stamp, Kind = "MP3", Bitrate = 320, SampleRate = 44100, Title = Path.GetFileName(path),
    };

    /// <summary>Music falling 2 dB per kHz, cut dead at <paramref name="cutoff"/> when given.</summary>
    private static SpectrumAnalysis Spectrum(double? cutoff)
    {
        const int rate = 44100;
        var size = SpectrumAccumulator.FftSizeFor(rate);
        var average = new double[(size / 2) + 1];
        for (var k = 0; k < average.Length; k++)
        {
            var hz = (double)k * rate / size;
            average[k] = cutoff is { } c && hz >= c ? -140 : -40 - (2 * (hz - 1000) / 1000);
        }

        return new SpectrumAnalysis
        {
            SampleRate = rate, Channels = 2, Duration = TimeSpan.FromSeconds(30), FftSize = size,
            Average = average, Peak = average, Columns = 1, Rows = 1, Picture = [-60f], PeakSampleDb = -1,
        };
    }

    /// <summary>"lossy" in the name cuts at 16 kHz; "broken" can't be decoded; the rest are clean.</summary>
    private static SpectrumAnalysis FakeAnalyze(string path) =>
        path.Contains("broken") ? throw new AnalysisException("bad file")
        : Spectrum(path.Contains("lossy") ? 16000 : null);

    [Fact]
    public void A_result_follows_its_file_to_another_drive_letter()
    {
        var cache = QualityCache.Empty();
        cache.Set(Flac(@"D:\Music\A\song.flac"), new QualityResult(Verdict.Inconsistent, [QualityFlag.LossySource]));

        Assert.True(cache.TryGet(Flac(@"E:\Music\A\song.flac"), out var moved));
        Assert.Equal([QualityFlag.LossySource], moved.Flags);

        // Retagged: same path, new size and time, so it's checked again.
        Assert.False(cache.TryGet(Flac(@"D:\Music\A\song.flac", size: 1001), out _));
    }

    [Fact]
    public void Results_survive_a_save_and_load_and_older_rules_are_dropped()
    {
        var path = Path.Combine(_dir, "quality.json");
        var cache = QualityCache.Empty();
        cache.Set(Flac(@"D:\a.flac"), new QualityResult(Verdict.Suspect, [QualityFlag.PossiblyLossySource]));
        cache.Set(Flac(@"D:\b.flac"), new QualityResult(Verdict.Consistent, []));
        cache.Save(path);

        var loaded = QualityCache.Load(path);
        Assert.Equal(2, loaded.Count);
        Assert.True(loaded.TryGet(Flac(@"D:\a.flac"), out var a));
        Assert.Equal(Verdict.Suspect, a.Verdict);
        Assert.Equal([QualityFlag.PossiblyLossySource], a.Flags);

        File.WriteAllText(path, File.ReadAllText(path).Replace($"\"Version\":{QualityCache.CurrentVersion}", "\"Version\":0"));
        Assert.Equal(0, QualityCache.Load(path).Count);
    }

    [Fact]
    public void A_missing_or_corrupt_file_loads_as_empty()
    {
        Assert.Equal(0, QualityCache.Load(Path.Combine(_dir, "none.json")).Count);

        Directory.CreateDirectory(_dir);
        var bad = Path.Combine(_dir, "bad.json");
        File.WriteAllText(bad, "{ not json");
        Assert.Equal(0, QualityCache.Load(bad).Count);
    }

    [Fact]
    public async Task The_scanner_checks_each_track_once_and_records_what_it_found()
    {
        var tracks = new[]
        {
            Flac(@"D:\clean.flac"), Flac(@"D:\lossy.flac"), Mp3(@"D:\lossy.mp3"),
            Flac(@"D:\broken.flac"), Flac(@"D:\gone.flac"),
        };
        var cache = QualityCache.Empty();
        var saves = 0;
        var reports = new List<QualityScanProgress>();

        var summary = await QualityScanner.RunAsync(tracks, cache, new SyncProgress(reports.Add), CancellationToken.None,
            save: () => saves++, workers: 2, analyze: FakeAnalyze, exists: p => !p.Contains("gone"));

        Assert.Equal(new QualityScanSummary(Checked: 3, AlreadyKnown: 0, Flagged: 2, Unreadable: 1, Missing: 1, Cancelled: false), summary);
        Assert.True(saves >= 1);
        Assert.Equal(new QualityScanProgress(5, 5, 2), reports.Last());

        Assert.True(cache.TryGet(tracks[1], out var lossy));
        Assert.Equal([QualityFlag.LossySource], lossy.Flags);
        Assert.True(cache.TryGet(tracks[2], out var mp3));
        Assert.Equal([QualityFlag.ReEncodedLossy], mp3.Flags);
        Assert.True(cache.TryGet(tracks[3], out var broken));
        Assert.Equal(Verdict.Inconclusive, broken.Verdict);
        Assert.False(cache.TryGet(tracks[4], out _)); // the drive's not there: try again later

        // A second run has only the missing file left to look at.
        var again = await QualityScanner.RunAsync(tracks, cache, null, CancellationToken.None,
            workers: 2, analyze: FakeAnalyze, exists: _ => true);
        Assert.Equal(4, again.AlreadyKnown);
        Assert.Equal(1, again.Checked);
    }

    [Fact]
    public async Task A_cancelled_scan_keeps_what_it_had_done()
    {
        var tracks = Enumerable.Range(0, 200).Select(i => Flac($@"D:\{i}.flac")).ToArray();
        var cache = QualityCache.Empty();
        using var cancel = new CancellationTokenSource();
        var count = 0;

        SpectrumAnalysis SlowAnalyze(string path)
        {
            if (Interlocked.Increment(ref count) == 20)
                cancel.Cancel();
            return Spectrum(null);
        }

        var summary = await QualityScanner.RunAsync(tracks, cache, null, cancel.Token, workers: 1, analyze: SlowAnalyze, exists: _ => true);

        Assert.True(summary.Cancelled);
        Assert.InRange(cache.Count, 20, 21);
    }

    [Fact]
    public void Each_statistics_row_counts_its_tracks_and_its_filter_finds_exactly_those()
    {
        var tracks = new[] { Flac(@"D:\a.flac"), Flac(@"D:\b.flac"), Mp3(@"D:\c.mp3"), Flac(@"D:\d.flac") };
        var cache = QualityCache.Empty();
        cache.Set(tracks[0], new QualityResult(Verdict.Inconsistent, [QualityFlag.LossySource]));
        cache.Set(tracks[1], new QualityResult(Verdict.Inconsistent, [QualityFlag.LossySource, QualityFlag.Padded24Bit]));
        cache.Set(tracks[2], new QualityResult(Verdict.Suspect, [QualityFlag.PossiblyReEncodedLossy]));

        var stats = QualityStatistics.Compute(tracks, cache);

        Assert.Equal(4, stats.Total);
        Assert.Equal(3, stats.Checked);
        Assert.Equal(1, stats.Unchecked);
        Assert.Equal(QualityStatistics.Rows.Count, stats.Gaps.Count);

        foreach (var gap in stats.Gaps)
            Assert.Equal(gap.Tracks, gap.Filter.Apply(tracks).Count);

        Assert.Equal(2, stats.Gaps.Single(g => g.Flag == QualityFlag.LossySource).Tracks);
        Assert.Equal(0.5, stats.Gaps.Single(g => g.Flag == QualityFlag.LossySource).Share);
        // A rebuilt library (new Track objects) still matches; a retagged file doesn't.
        var rebuilt = new[] { Flac(@"E:\a.flac"), Flac(@"D:\b.flac", size: 2000) };
        Assert.Single(stats.Gaps.Single(g => g.Flag == QualityFlag.LossySource).Filter.Apply(rebuilt));
    }

    [Fact]
    public void Statistics_rows_run_from_highest_claimed_quality_to_lowest()
    {
        Assert.Equal(
            [
                "Fake 24-bit",
                "Fake hi-res",
                "Possibly fake hi-res",
                "Likely transcoded lossless",
                "Possibly transcoded lossless",
                "Upscaled MP3",
                "Possibly upscaled MP3",
            ],
            QualityStatistics.Rows.Select(r => r.Label));
    }

    /// <summary>Reports straight away on the reporting thread, unlike Progress&lt;T&gt;.</summary>
    private sealed class SyncProgress(Action<QualityScanProgress> report) : IProgress<QualityScanProgress>
    {
        public void Report(QualityScanProgress value)
        {
            lock (this)
                report(value);
        }
    }
}
