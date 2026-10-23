using AudioFool.Core.Art;
using AudioFool.Core.Models;

namespace AudioFool.Core.Tests;

/// <summary>The CUT-OFF COVERS section: cut-off pictures in songs, from the cover check, and cut-off folder cover files.</summary>
public sealed class CutOffCoverTests : IDisposable
{
    private static readonly DateTime Stamp = new(2026, 10, 22, 12, 0, 0, DateTimeKind.Utc);

    private readonly string _root = Path.Combine(Path.GetTempPath(), $"AudioFool-cutoffs-{Guid.NewGuid():N}");

    public CutOffCoverTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private static Track Song(string path, string album = "Album", string? folderArt = null) => new()
    {
        FilePath = path, FileSize = 1000, ModifiedUtc = Stamp, Kind = "FLAC",
        Title = Path.GetFileName(path), Artist = "Artist", Album = album, FolderArtPath = folderArt,
    };

    private static byte[] Fixture(string name) => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "TestData", name));

    [Fact]
    public void The_embedded_row_counts_songs_and_albums()
    {
        Track[] tracks = [Song(@"D:\a.flac", "One"), Song(@"D:\b.flac", "One"), Song(@"D:\c.flac", "Two"), Song(@"D:\d.flac", "Three")];
        var cache = CoverCache.Empty();
        cache.Set(tracks[0], new CoverFinding(1, 0, CutOff: 1));
        cache.Set(tracks[1], new CoverFinding(2, 50_000, CutOff: 1));
        cache.Set(tracks[2], new CoverFinding(1, 0));
        // d.flac not read yet.

        var stats = CutOffCoverStatistics.Compute(tracks, cache);

        Assert.Equal((4, 3, 1), (stats.Total, stats.Checked, stats.Unchecked));
        Assert.Equal((2, 1), (stats.EmbeddedTracks, stats.EmbeddedAlbums));
        Assert.Equal(CutOffCoverStatistics.EmbeddedLabel, stats.EmbeddedFilter!.Description);
        Assert.Equal([@"D:\a.flac", @"D:\b.flac"], stats.EmbeddedFilter.Apply(tracks).Select(t => t.FilePath));
    }

    [Fact]
    public void No_cut_off_picture_means_no_filter()
    {
        var song = Song(@"D:\a.flac");
        var cache = CoverCache.Empty();
        cache.Set(song, new CoverFinding(1, 0));

        Assert.Null(CutOffCoverStatistics.Compute([song], cache).EmbeddedFilter);
    }

    [Fact]
    public void A_fixed_file_leaves_the_embedded_filter()
    {
        var song = Song(@"D:\a.flac");
        var cache = CoverCache.Empty();
        cache.Set(song, new CoverFinding(1, 0, CutOff: 1));
        var filter = CutOffCoverStatistics.Compute([song], cache).EmbeddedFilter!;

        var fixedSong = new Track
        {
            FilePath = song.FilePath, FileSize = 1200, ModifiedUtc = Stamp.AddMinutes(1), Kind = "FLAC",
            Title = song.Title, Artist = song.Artist, Album = song.Album,
        };

        Assert.Empty(filter.Apply([fixedSong]));
    }

    [Fact]
    public void The_files_row_counts_the_cut_off_cover_files_the_songs_show()
    {
        var one = Directory.CreateDirectory(Path.Combine(_root, "One")).FullName;
        var two = Directory.CreateDirectory(Path.Combine(_root, "Two")).FullName;
        var whole = Fixture("noise-64.jpg");
        File.WriteAllBytes(Path.Combine(one, "cover.jpg"), whole.AsSpan(0, whole.Length / 4).ToArray());
        File.WriteAllBytes(Path.Combine(two, "cover.jpg"), whole);

        Track[] tracks =
        [
            Song(Path.Combine(one, "1.flac"), "One", Path.Combine(one, "cover.jpg")),
            Song(Path.Combine(one, "2.flac"), "One", Path.Combine(one, "cover.jpg")),
            Song(Path.Combine(two, "1.flac"), "Two", Path.Combine(two, "cover.jpg")),
            Song(Path.Combine(_root, "loose.flac"), "Loose"),
            Song(Path.Combine(_root, "Gone", "1.flac"), "Gone", Path.Combine(_root, "Gone", "cover.jpg")),   // not there: not counted
        ];

        var files = CutOffCoverStatistics.CountFiles(tracks);

        Assert.Equal((1, 3, 1), (files.Files, files.AllFiles, files.Albums));
        Assert.Equal(CutOffCoverStatistics.FilesLabel, files.Filter!.Description);
        Assert.All(files.Filter.Apply(tracks), t => Assert.Equal("One", t.Album));
        Assert.Equal(2, files.Filter.Apply(tracks).Count);
    }

    [Fact]
    public void The_cover_check_counts_a_songs_cut_off_pictures()
    {
        var whole = Fixture("cover.jpg");
        var big = Fixture("noise-64.jpg");
        using var file = new TempAudioFile("sample.flac");
        using (var tagged = TagLib.File.Create(file.Path))
        {
            tagged.Tag.Pictures =
            [
                new TagLib.Picture(new TagLib.ByteVector(big.AsSpan(0, 900).ToArray())) { Type = TagLib.PictureType.FrontCover, MimeType = "image/jpeg" },
                new TagLib.Picture(new TagLib.ByteVector(whole)) { Type = TagLib.PictureType.FrontCover, MimeType = "image/jpeg" },
            ];
            tagged.Save();
        }

        var finding = CoverCleaner.Survey(file.Path)!;

        Assert.Equal((2, 1), (finding.Pictures, finding.CutOff));
        Assert.True(finding.HasCutOff);
    }

    [Fact]
    public void The_cache_drops_results_from_before_cut_off_pictures()
    {
        var path = Path.Combine(_root, "covers.json");
        File.WriteAllText(path, """{"Version":2,"Results":{"a.flac|1000|1":{"Pictures":1,"Freeable":0,"PngBytes":0}}}""");

        var song = new Track { FilePath = @"D:\a.flac", FileSize = 1000, ModifiedUtc = new DateTime(1, DateTimeKind.Utc) };

        Assert.False(CoverCache.Load(path).TryGet(song, out _));
    }

    [Fact]
    public void A_cut_off_count_survives_a_save_and_load()
    {
        var path = Path.Combine(_root, "covers.json");
        var song = Song(@"D:\a.flac");
        var cache = CoverCache.Empty();
        cache.Set(song, new CoverFinding(2, 10, 0, CutOff: 1));
        cache.Save(path);

        Assert.True(CoverCache.Load(path).TryGet(song, out var finding));
        Assert.Equal(1, finding.CutOff);
    }
}
