using AudioFool.Core.Library;
using AudioFool.Core.Models;

namespace AudioFool.Core.Tests;

/// <summary>
/// The drive going partway through a scan. The scan used to store a blank
/// "Unknown Artist" track, with the file's real size and date, for every file it
/// couldn't open, and never read it again; and it took every file it hadn't
/// listed yet as deleted.
/// </summary>
public sealed class DriveLostMidScanTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "audiofool-lost-" + Guid.NewGuid().ToString("N"));

    public DriveLostMidScanTests() => Directory.CreateDirectory(_folder);

    public void Dispose()
    {
        if (Directory.Exists(_folder))
            Directory.Delete(_folder, recursive: true);
    }

    private string WriteFile(string name)
    {
        var path = Path.Combine(_folder, name);
        File.WriteAllText(path, "not really audio");
        return path;
    }

    /// <summary>What the cache held for a file before it last changed, so the scan has to read it.</summary>
    private static Track Cached(string path, string title) => new()
    {
        FilePath = path,
        Title = title,
        Artist = "Artist",
        Album = "Album",
        FileSize = 1,
        ModifiedUtc = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc),
    };

    private static Dictionary<string, Track> Known(params Track[] tracks) =>
        tracks.ToDictionary(t => t.FilePath, StringComparer.OrdinalIgnoreCase);

    /// <summary>Reports straight away, on the scan's own thread, so the test can act mid-scan.</summary>
    private sealed class AtFirstReport(Action act) : IProgress<ScanProgress>
    {
        private bool _done;

        public void Report(ScanProgress value)
        {
            if (_done)
                return;

            _done = true;
            act();
        }
    }

    [Fact]
    public async Task A_known_file_that_cant_be_opened_keeps_the_tags_it_had()
    {
        var path = WriteFile("locked.mp3");

        ScanResult result;
        using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
            result = await LibraryScanner.ScanAsync([_folder], Known(Cached(path, "Kept")));

        var track = Assert.Single(result.Library.AllTracks);
        Assert.Equal("Kept", track.Title);
        Assert.Equal("Artist", track.Artist);
        Assert.Equal(0, result.Summary.Read);
        Assert.Equal(1, result.Summary.Unopened);
        Assert.Equal(0, result.Summary.Removed);
    }

    [Fact]
    public async Task A_new_file_that_cant_be_opened_is_listed_and_read_again_next_time()
    {
        var path = WriteFile("locked.mp3");

        ScanResult first;
        using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
            first = await LibraryScanner.ScanAsync([_folder]);

        // Listed, so nothing silently disappears, but not trusted as read.
        var listed = Assert.Single(first.Library.AllTracks);
        Assert.Equal(0, listed.FileSize);

        var second = await LibraryScanner.ScanAsync([_folder], Known([.. first.Library.AllTracks]));

        Assert.Equal(1, second.Summary.Read);
        Assert.Equal(new FileInfo(path).Length, Assert.Single(second.Library.AllTracks).FileSize);
    }

    [Fact]
    public void A_locked_file_is_told_apart_from_one_that_opens_but_cant_be_parsed()
    {
        var locked = WriteFile("locked.mp3");
        var damaged = WriteFile("damaged.mp3");

        using (new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            TagReader.Read(locked, null, out var lockedUnopened);
            Assert.True(lockedUnopened);
        }

        // Reads the same next time, so it keeps its real stamp and isn't retried.
        var track = TagReader.Read(damaged, null, out var damagedUnopened);
        Assert.False(damagedUnopened);
        Assert.Equal(new FileInfo(damaged).Length, track.FileSize);
    }

    [Fact]
    public async Task A_folder_that_goes_mid_scan_is_unplugged_not_deleted()
    {
        var one = WriteFile("one.mp3");
        var two = WriteFile("two.mp3");
        WriteFile("new.mp3");

        // The folder goes after the files are listed and before their tags are read:
        // the drive pulled partway through.
        var progress = new AtFirstReport(() => Directory.Delete(_folder, recursive: true));
        var result = await LibraryScanner.ScanAsync([_folder], Known(Cached(one, "One"), Cached(two, "Two")), progress);

        Assert.Equal(_folder, Assert.Single(result.UnavailableFolders));
        Assert.Equal(0, result.Summary.Removed);
        Assert.Equal(["One", "Two"], result.Library.AllTracks.Select(t => t.Title).Order());
    }

    [Fact]
    public async Task A_folder_that_is_still_there_mid_scan_is_scanned_as_usual()
    {
        var kept = WriteFile("kept.mp3");
        var gone = Path.Combine(_folder, "gone.mp3");

        var result = await LibraryScanner.ScanAsync([_folder], Known(Cached(kept, "Kept"), Cached(gone, "Gone")));

        Assert.Empty(result.UnavailableFolders);
        Assert.Equal(1, result.Summary.Removed);
        Assert.Equal(1, result.Summary.Read);
    }
}
