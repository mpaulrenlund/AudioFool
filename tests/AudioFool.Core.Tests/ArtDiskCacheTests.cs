using AudioFool.Core.Art;
using AudioFool.Core.Library;
using AudioFool.Core.Models;

namespace AudioFool.Core.Tests;

/// <summary>
/// The saved small covers (session 47): names that survive a drive-letter change
/// and change with the files, the "no cover" record, pruning; and reading the tag
/// windows' detail fields several files at once.
/// </summary>
public sealed class ArtDiskCacheTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), $"AudioFool-art-{Guid.NewGuid():N}");
    private static readonly DateTime Now = new(2026, 10, 12, 12, 0, 0, DateTimeKind.Utc);

    public void Dispose()
    {
        if (Directory.Exists(_folder))
            Directory.Delete(_folder, recursive: true);
    }

    private static Album AlbumAt(string root, long size = 1000, int tracks = 3, DateTime? modified = null) => new()
    {
        Title = "Moving Pictures",
        ArtistName = "Rush",
        Tracks = Enumerable.Range(1, tracks).Select(i => new Track
        {
            FilePath = $@"{root}\Rush\Moving Pictures\{i:00}.flac",
            FileSize = size + i,
            ModifiedUtc = modified ?? new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        }).ToList(),
    };

    [Fact]
    public void Same_album_on_another_drive_letter_has_the_same_name()
    {
        Assert.Equal(ArtDiskCache.KeyFor(AlbumAt("D:"), 96), ArtDiskCache.KeyFor(AlbumAt("F:"), 96));
    }

    [Fact]
    public void A_changed_file_or_another_size_gives_another_name()
    {
        var key = ArtDiskCache.KeyFor(AlbumAt("D:"), 96);
        Assert.NotEqual(key, ArtDiskCache.KeyFor(AlbumAt("D:", modified: Now), 96));
        Assert.NotEqual(key, ArtDiskCache.KeyFor(AlbumAt("D:", size: 2000), 96));
        Assert.NotEqual(key, ArtDiskCache.KeyFor(AlbumAt("D:"), 320));
    }

    [Fact]
    public void Only_the_first_five_tracks_count()
    {
        // AlbumArtService looks for a cover in the first five tracks only.
        var album = AlbumAt("D:", tracks: 12);
        var sixthChanged = new Album
        {
            Title = album.Title,
            ArtistName = album.ArtistName,
            Tracks = album.Tracks.Select((t, i) => i < 5 ? t : new Track
            {
                FilePath = t.FilePath,
                FileSize = 1,
                ModifiedUtc = t.ModifiedUtc,
            }).ToList(),
        };
        Assert.Equal(ArtDiskCache.KeyFor(album, 96), ArtDiskCache.KeyFor(sixthChanged, 96));
    }

    [Fact]
    public void A_copy_reads_back_and_an_empty_one_means_no_cover()
    {
        var cache = new ArtDiskCache(_folder);
        Assert.False(cache.TryRead("a", Now, out _));

        cache.Write("a", [1, 2, 3]);
        cache.Write("b", null);

        Assert.True(cache.TryRead("a", Now, out var image));
        Assert.Equal([1, 2, 3], image);
        Assert.True(cache.TryRead("b", Now, out var none));
        Assert.Null(none);
        Assert.True(cache.Contains("b"));
    }

    [Fact]
    public void Remove_deletes_a_copy()
    {
        var cache = new ArtDiskCache(_folder);
        cache.Write("a", [1]);
        cache.Remove("a");
        cache.Remove("never written");
        Assert.False(cache.Contains("a"));
    }

    [Fact]
    public void Prune_deletes_copies_unused_for_90_days_and_old_temp_files()
    {
        var cache = new ArtDiskCache(_folder);
        cache.Write("old", [1]);
        cache.Write("recent", [2]);
        File.SetLastWriteTimeUtc(cache.PathFor("old"), Now.AddDays(-91));
        File.SetLastWriteTimeUtc(cache.PathFor("recent"), Now.AddDays(-89));
        var temp = Path.Combine(_folder, "x.art.123.tmp");
        File.WriteAllBytes(temp, [0]);
        File.SetLastWriteTimeUtc(temp, Now.AddDays(-2));
        var other = Path.Combine(_folder, "notes.txt");
        File.WriteAllText(other, "not ours");
        File.SetLastWriteTimeUtc(other, Now.AddDays(-400));

        Assert.Equal(2, cache.Prune(Now));
        Assert.False(cache.Contains("old"));
        Assert.True(cache.Contains("recent"));
        Assert.False(File.Exists(temp));
        Assert.True(File.Exists(other));
    }

    [Fact]
    public void Using_an_old_copy_keeps_it_from_being_pruned()
    {
        var cache = new ArtDiskCache(_folder);
        cache.Write("a", [1]);
        File.SetLastWriteTimeUtc(cache.PathFor("a"), Now.AddDays(-60));

        Assert.True(cache.TryRead("a", Now, out _));

        Assert.Equal(0, cache.Prune(Now.AddDays(45)));
        Assert.True(cache.Contains("a"));
    }

    [Fact]
    public void Prune_with_no_folder_yet_does_nothing()
    {
        Assert.Equal(0, new ArtDiskCache(_folder).Prune(Now));
    }

    [Fact]
    public void Reading_art_tells_no_cover_from_an_unreadable_file()
    {
        using var flac = new TempAudioFile("sample.flac");
        Assert.True(TagReader.TryReadEmbeddedArt(flac.Path, out var art));
        Assert.Null(art);
        Assert.False(TagReader.TryReadEmbeddedArt(Path.Combine(_folder, "missing.flac"), out _));
    }

    [Fact]
    public void Details_of_many_files_come_back_in_order_with_null_for_unreadable_ones()
    {
        using var flac = new TempAudioFile("sample.flac");
        using var mp3 = new TempAudioFile("sample.mp3");
        using (var tag = TagLib.File.Create(flac.Path)) { tag.Tag.Genres = ["Ambient"]; tag.Save(); }
        using (var tag = TagLib.File.Create(mp3.Path)) { tag.Tag.Genres = ["Jazz"]; tag.Save(); }
        var missing = Path.Combine(_folder, "missing.flac");

        string[] paths = [flac.Path, missing, mp3.Path, flac.Path];
        var read = TagReader.ReadDetails(paths);

        Assert.Equal(["Ambient", null, "Jazz", "Ambient"], read.Select(d => d?.Genre));
        Assert.Equal(paths.Select(p => TagReader.ReadDetails(p)).Select(d => d?.Genre), read.Select(d => d?.Genre));
    }
}
