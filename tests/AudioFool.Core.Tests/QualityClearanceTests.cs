using AudioFool.Core.Analysis;
using AudioFool.Core.Models;

namespace AudioFool.Core.Tests;

/// <summary>
/// Clearing songs from Quality Check rows: what is saved, what a clearance
/// survives (a retag, a new drive letter) and what ends it (a different file),
/// and how the Statistics rows and their filters leave cleared songs out.
/// </summary>
public class QualityClearanceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "AudioFoolClearanceTests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }

    private string FilePath => Path.Combine(_dir, "quality-cleared.json");

    private static readonly DateTime Stamp = new(2026, 10, 7, 9, 0, 0, DateTimeKind.Utc);

    private static Track Hires(string path, string album = "Album", int bits = 24, int rate = 96000) => new()
    {
        FilePath = path, FileSize = 1000, ModifiedUtc = Stamp, Kind = "FLAC", BitDepth = bits, SampleRate = rate,
        Bitrate = 2900, Title = Path.GetFileName(path), Artist = "Artist", Album = album,
    };

    private static Track Mp3(string path, int bitrate = 320) => new()
    {
        FilePath = path, FileSize = 500, ModifiedUtc = Stamp, Kind = "MP3", Bitrate = bitrate, SampleRate = 44100,
        Title = Path.GetFileName(path), Artist = "Artist", Album = "Album",
    };

    [Fact]
    public void A_clearance_is_saved_and_read_back()
    {
        var track = Hires(@"D:\Music\a.flac");
        var store = QualityClearances.Load(FilePath, Stamp);
        Assert.Equal(1, store.Clear([track], QualityFlag.Padded24Bit, Stamp));

        var reloaded = QualityClearances.Load(FilePath, Stamp);
        Assert.Equal(1, reloaded.Count);
        Assert.True(reloaded.IsCleared(track, QualityFlag.Padded24Bit));
        Assert.False(reloaded.IsCleared(track, QualityFlag.Upsampled));
    }

    [Fact]
    public void Clearing_one_row_leaves_the_songs_other_rows_alone()
    {
        var track = Hires(@"D:\Music\a.flac");
        var store = QualityClearances.Empty(FilePath);
        store.Clear([track], QualityFlag.Padded24Bit, Stamp);

        Assert.True(store.IsCleared(track, QualityFlag.Padded24Bit));
        Assert.False(store.IsCleared(track, QualityFlag.Upsampled));
        Assert.False(store.IsCleared(track, QualityFlag.PossiblyUpsampled));
        Assert.Equal([QualityFlag.Padded24Bit], store.RowsClearedFor(track));
    }

    [Fact]
    public void A_row_and_its_possibly_twin_are_cleared_together()
    {
        var track = Hires(@"D:\Music\a.flac");
        var store = QualityClearances.Empty(FilePath);
        store.Clear([track], QualityFlag.PossiblyUpsampled, Stamp);

        Assert.True(store.IsCleared(track, QualityFlag.Upsampled));
        Assert.True(store.IsCleared(track, QualityFlag.PossiblyUpsampled));
        Assert.Equal("Not Fake Hi-Res", QualityClearances.ClearLabel(QualityFlag.PossiblyUpsampled));
        Assert.Equal("Transcoded lossless", QualityClearances.RowName(QualityFlag.PossiblyLossySource));
    }

    [Fact]
    public void Clearing_twice_adds_nothing()
    {
        var track = Hires(@"D:\Music\a.flac");
        var store = QualityClearances.Empty(FilePath);
        Assert.Equal(1, store.Clear([track], QualityFlag.Padded24Bit, Stamp));
        Assert.Equal(0, store.Clear([track, track], QualityFlag.Padded24Bit, Stamp));
        Assert.Equal(1, store.Count);
    }

    [Fact]
    public void A_retag_keeps_the_clearance()
    {
        var store = QualityClearances.Empty(FilePath);
        store.Clear([Hires(@"D:\Music\a.flac")], QualityFlag.Padded24Bit, Stamp);

        // New tags, size and write time: the quality cache would see a new file.
        var retagged = new Track
        {
            FilePath = @"D:\Music\a.flac", FileSize = 2000, ModifiedUtc = Stamp.AddDays(1), Kind = "FLAC", BitDepth = 24,
            SampleRate = 96000, Bitrate = 2950, Title = "New title", Artist = "Someone else", Album = "Album",
        };
        Assert.True(store.IsCleared(retagged, QualityFlag.Padded24Bit));
    }

    [Fact]
    public void A_new_drive_letter_finds_the_song_and_repoints_it()
    {
        var store = QualityClearances.Load(FilePath, Stamp);
        store.Clear([Hires(@"D:\Music\Artist\Album\a.flac")], QualityFlag.Padded24Bit, Stamp);

        var moved = Hires(@"E:\Music\Artist\Album\a.flac");
        Assert.True(store.IsCleared(moved, QualityFlag.Padded24Bit));

        Assert.True(store.Repoint([moved]));
        var reloaded = QualityClearances.Load(FilePath, Stamp);
        // Repointed: now found by path, so an album retag no longer loses it.
        Assert.True(reloaded.IsCleared(Hires(@"E:\Music\Artist\Album\a.flac", album: "Renamed"), QualityFlag.Padded24Bit));
    }

    [Theory]
    [InlineData(@"D:\Music\Artist\Album\CD1\01 Intro.flac", @"D:\Music\Artist\Album\CD2\01 Intro.flac")]
    [InlineData(@"D:\Music\Artist\Album (Disc 1)\01 Intro.flac", @"D:\Music\Artist\Album (Disc 2)\01 Intro.flac")]
    public void Another_discs_song_of_the_same_name_is_not_cleared_with_it(string disc1, string disc2)
    {
        var store = QualityClearances.Empty(FilePath);
        var first = Hires(disc1);
        var second = Hires(disc2);
        store.Clear([first], QualityFlag.Padded24Bit, Stamp);

        Assert.True(store.IsCleared(first, QualityFlag.Padded24Bit));
        Assert.False(store.IsCleared(second, QualityFlag.Padded24Bit));
        Assert.False(store.IsClearedFromAny(second));

        // Putting the other disc's song back leaves this one's clearance.
        Assert.Equal(0, store.PutBack([second]));
        Assert.True(store.IsCleared(first, QualityFlag.Padded24Bit));

        // And clearing it is a clearance of its own.
        Assert.Equal(1, store.Clear([second], QualityFlag.Padded24Bit, Stamp));
        Assert.Equal(2, store.Count);
    }

    [Fact]
    public void A_moved_music_folder_still_finds_a_discs_song()
    {
        var store = QualityClearances.Empty(FilePath);
        store.Clear([Hires(@"D:\Music\Artist\Album\CD2\01 Intro.flac")], QualityFlag.Padded24Bit, Stamp);

        Assert.True(store.IsCleared(Hires(@"E:\Audio\Artist\Album\CD2\01 Intro.flac"), QualityFlag.Padded24Bit));
        Assert.False(store.IsCleared(Hires(@"E:\Audio\Artist\Album\CD1\01 Intro.flac"), QualityFlag.Padded24Bit));
    }

    [Fact]
    public void Repointing_leaves_an_entry_whose_own_file_is_still_there()
    {
        var store = QualityClearances.Empty(FilePath);
        var onD = Hires(@"D:\Music\a.flac");
        store.Clear([onD], QualityFlag.Padded24Bit, Stamp);

        Assert.False(store.Repoint([onD, Hires(@"E:\Music\a.flac")]));
        Assert.True(store.IsCleared(onD, QualityFlag.Padded24Bit));
    }

    [Fact]
    public void A_different_file_at_the_same_path_is_no_longer_cleared()
    {
        var store = QualityClearances.Empty(FilePath);
        store.Clear([Hires(@"D:\Music\a.flac")], QualityFlag.Upsampled, Stamp);

        Assert.False(store.IsCleared(Hires(@"D:\Music\a.flac", rate: 48000), QualityFlag.Upsampled));
        Assert.False(store.IsCleared(Hires(@"D:\Music\a.flac", bits: 16), QualityFlag.Upsampled));
    }

    [Fact]
    public void An_mp3s_bitrate_is_part_of_what_was_cleared()
    {
        var store = QualityClearances.Empty(FilePath);
        store.Clear([Mp3(@"D:\Music\a.mp3")], QualityFlag.ReEncodedLossy, Stamp);

        Assert.True(store.IsCleared(Mp3(@"D:\Music\a.mp3"), QualityFlag.PossiblyReEncodedLossy));
        Assert.False(store.IsCleared(Mp3(@"D:\Music\a.mp3", bitrate: 256), QualityFlag.ReEncodedLossy));
    }

    [Fact]
    public void Put_back_takes_one_row_or_every_row()
    {
        var a = Hires(@"D:\Music\a.flac");
        var b = Hires(@"D:\Music\b.flac");
        var store = QualityClearances.Load(FilePath, Stamp);
        store.Clear([a, b], QualityFlag.Padded24Bit, Stamp);
        store.Clear([a], QualityFlag.Upsampled, Stamp);

        Assert.Equal(1, store.PutBack([a], QualityFlag.Upsampled));
        Assert.True(store.IsCleared(a, QualityFlag.Padded24Bit));
        Assert.False(store.IsCleared(a, QualityFlag.Upsampled));

        Assert.Equal(2, store.PutBack([a, b]));
        Assert.Equal(0, QualityClearances.Load(FilePath, Stamp).Count);
    }

    [Fact]
    public void A_damaged_file_is_read_from_the_save_before()
    {
        var a = Hires(Path.Combine(_dir, "a.flac"));
        var b = Hires(Path.Combine(_dir, "b.flac"));
        var store = QualityClearances.Load(FilePath, Stamp);
        store.Clear([a], QualityFlag.Padded24Bit, Stamp);
        store.Clear([b], QualityFlag.Padded24Bit, Stamp);
        Assert.Equal(2, QualityClearances.Load(FilePath, Stamp).Count);

        File.WriteAllText(FilePath, "");
        var reloaded = QualityClearances.Load(FilePath, Stamp);

        Assert.True(reloaded.IsCleared(a, QualityFlag.Padded24Bit));
        Assert.False(reloaded.IsCleared(b, QualityFlag.Padded24Bit));
        Assert.Single(Directory.GetFiles(_dir, "quality-cleared.unreadable-*.json"));
        Assert.Equal(1, QualityClearances.Load(FilePath, Stamp).Count);
        Assert.False(File.Exists(FilePath + ".tmp"));
    }

    [Fact]
    public void An_unreadable_file_is_moved_aside_not_overwritten()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(FilePath, "{ not json");

        var store = QualityClearances.Load(FilePath, Stamp);
        Assert.Equal(0, store.Count);
        Assert.False(File.Exists(FilePath));
        Assert.Single(Directory.GetFiles(_dir, "quality-cleared.unreadable-*.json"));
    }

    [Fact]
    public void Statistics_rows_leave_cleared_songs_out_and_count_them_apart()
    {
        var a = Hires(@"D:\Music\a.flac");
        var b = Hires(@"D:\Music\b.flac");
        var c = Hires(@"D:\Music\c.flac");
        var tracks = new[] { a, b, c };
        var cache = QualityCache.Empty();
        cache.Set(a, new QualityResult(Verdict.Inconsistent, [QualityFlag.Upsampled, QualityFlag.Padded24Bit]));
        cache.Set(b, new QualityResult(Verdict.Inconsistent, [QualityFlag.Padded24Bit]));
        cache.Set(c, new QualityResult(Verdict.Suspect, [QualityFlag.PossiblyUpsampled]));

        var store = QualityClearances.Empty(FilePath);
        store.Clear([a], QualityFlag.Padded24Bit, Stamp);
        store.Clear([c], QualityFlag.Upsampled, Stamp);

        var stats = QualityStatistics.Compute(tracks, cache, store);
        QualityGap Row(QualityFlag flag) => stats.Gaps.Single(g => g.Flag == flag);

        Assert.Equal(1, Row(QualityFlag.Padded24Bit).Tracks);
        Assert.Equal(1, Row(QualityFlag.Upsampled).Tracks);
        Assert.Equal(0, Row(QualityFlag.PossiblyUpsampled).Tracks);
        foreach (var gap in stats.Gaps)
            Assert.Equal(gap.Tracks, gap.Filter.Apply(tracks).Count);

        Assert.Equal(QualityFlag.Padded24Bit, Row(QualityFlag.Padded24Bit).Filter.QualityFlag);
        Assert.Equal(2, stats.Cleared);
        Assert.True(stats.ClearedFilter!.ShowsQualityClearances);
        Assert.Equal([a, c], stats.ClearedFilter.Apply(tracks));
    }

    [Fact]
    public void A_rows_filter_drops_a_song_cleared_after_the_statistics_were_counted()
    {
        var a = Hires(@"D:\Music\a.flac");
        var b = Hires(@"D:\Music\b.flac");
        var cache = QualityCache.Empty();
        cache.Set(a, new QualityResult(Verdict.Inconsistent, [QualityFlag.Padded24Bit]));
        cache.Set(b, new QualityResult(Verdict.Inconsistent, [QualityFlag.Padded24Bit]));
        var store = QualityClearances.Empty(FilePath);

        var filter = QualityStatistics.Compute([a, b], cache, store).Gaps.Single(g => g.Flag == QualityFlag.Padded24Bit).Filter;
        Assert.Equal(2, filter.Apply([a, b]).Count);

        // The filter stays on in the main window while songs are cleared from it.
        store.Clear([a], QualityFlag.Padded24Bit, Stamp);
        Assert.Equal([b], filter.Apply([a, b]));

        store.PutBack([a]);
        Assert.Equal(2, filter.Apply([a, b]).Count);
    }

    [Fact]
    public void Flagged_counts_each_song_still_in_a_row_once()
    {
        var a = Hires(@"D:\Music\a.flac");
        var b = Hires(@"D:\Music\b.flac");
        var c = Hires(@"D:\Music\c.flac");
        var d = Hires(@"D:\Music\d.flac");
        var cache = QualityCache.Empty();
        cache.Set(a, new QualityResult(Verdict.Inconsistent, [QualityFlag.Upsampled, QualityFlag.Padded24Bit]));
        cache.Set(b, new QualityResult(Verdict.Inconsistent, [QualityFlag.Padded24Bit]));
        cache.Set(c, new QualityResult(Verdict.Consistent, []));
        cache.Set(d, new QualityResult(Verdict.Suspect, [QualityFlag.PossiblyUpsampled]));
        var store = QualityClearances.Empty(FilePath);
        store.Clear([b], QualityFlag.Padded24Bit, Stamp);

        // a is in two rows and counts once; b is cleared; c was never flagged.
        Assert.Equal(2, QualityStatistics.Compute([a, b, c, d], cache, store).Flagged);
    }

    [Fact]
    public void Clearing_everything_uncleared_empties_every_row()
    {
        var a = Hires(@"D:\Music\a.flac");
        var b = Hires(@"D:\Music\b.flac");
        var c = Mp3(@"D:\Music\c.mp3");
        var tracks = new[] { a, b, c };
        var cache = QualityCache.Empty();
        cache.Set(a, new QualityResult(Verdict.Inconsistent, [QualityFlag.Upsampled, QualityFlag.Padded24Bit]));
        cache.Set(b, new QualityResult(Verdict.Inconsistent, [QualityFlag.Padded24Bit]));
        cache.Set(c, new QualityResult(Verdict.Suspect, [QualityFlag.PossiblyReEncodedLossy]));
        var store = QualityClearances.Empty(FilePath);
        store.Clear([b], QualityFlag.Padded24Bit, Stamp);

        var pending = QualityStatistics.Uncleared(tracks, cache, store);
        Assert.Equal(3, pending.Count);
        Assert.DoesNotContain((b, QualityFlag.Padded24Bit), pending);

        foreach (var (track, flag) in pending)
            store.Clear([track], flag, Stamp);

        var stats = QualityStatistics.Compute(tracks, cache, store);
        Assert.All(stats.Gaps, gap => Assert.Equal(0, gap.Tracks));
        Assert.Equal(0, stats.Flagged);
        Assert.Equal(3, stats.Cleared);
        Assert.Empty(QualityStatistics.Uncleared(tracks, cache, store));
    }
}
