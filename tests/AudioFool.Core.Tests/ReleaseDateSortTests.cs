using System.Text.Json;
using AudioFool.Core.Library;
using AudioFool.Core.Models;

namespace AudioFool.Core.Tests;

public class ReleaseDateSortTests
{
    private static Track Track(string album, int? year, string? date = null) => new()
    {
        FilePath = $@"C:\music\{album}\{Guid.NewGuid():N}.flac",
        Album = album,
        AlbumArtist = "Cartoon Theory",
        Year = year,
        ReleaseDate = date,
    };

    private static string[] AlbumOrder(params Track[] tracks) =>
        LibraryScanner.Build(tracks).Artists.Single().Albums.Select(a => a.Title).ToArray();

    [Fact]
    public void A_full_date_sorts_after_a_bare_year_in_the_same_year()
    {
        // The case from the bug report: Remixes + More is tagged "2026" and
        // Ephemeral Dance "2026-10-02"; alphabetically Ephemeral would come first.
        Assert.Equal(
            ["FEEL", "Remixes + More", "Ephemeral Dance"],
            AlbumOrder(
                Track("Ephemeral Dance", 2026, "2026-10-02"),
                Track("Remixes + More", 2026),
                Track("FEEL", 2022)));
    }

    [Fact]
    public void Dates_in_the_same_year_sort_by_month_and_day()
    {
        Assert.Equal(
            ["March", "Early October", "Late October", "Next Year"],
            AlbumOrder(
                Track("Late October", 2026, "2026-10-25"),
                Track("Next Year", 2027),
                Track("March", 2026, "2026-03"),
                Track("Early October", 2026, "2026-10-02")));
    }

    [Fact]
    public void An_album_sorts_on_its_earliest_track_date()
    {
        var album = LibraryScanner.Build(
        [
            Track("Reissue", 2026, "2026-10-02"),
            Track("Reissue", 1998, "1998-06-01"),
            Track("Reissue", 2026),
        ]).Artists.Single().Albums.Single();

        Assert.Equal("1998-06-01", album.SortDate);
        Assert.Equal(1998, album.Year);
    }

    [Fact]
    public void Undated_albums_still_go_to_the_bottom() =>
        Assert.Equal(
            ["Dated", "Undated"],
            AlbumOrder(Track("Undated", null), Track("Dated", 2026, "2026-10-02")));

    [Fact]
    public void Scanning_a_file_reads_its_full_date()
    {
        using var file = new TempAudioFile("sample.flac");
        var track = TagReader.Read(file.Path);
        TagWriter.WriteTrackTags(track, new TrackTagEdit("T", "A", "AA", "Al", 2026, 1, 9, 1, 1) { Date = "2026-10-02" }, null, null);

        Assert.Equal("2026-10-02", TagReader.Read(file.Path).ReleaseDate);
    }

    [Fact]
    public void A_year_only_file_has_no_release_date()
    {
        using var file = new TempAudioFile("sample.flac");
        var track = TagReader.Read(file.Path);
        TagWriter.WriteTrackTags(track, new TrackTagEdit("T", "A", "AA", "Al", 2026, 1, 9, 1, 1), null, null);

        Assert.Null(TagReader.Read(file.Path).ReleaseDate);
    }

    [Fact]
    public void Saving_the_dialog_updates_the_in_memory_date_so_the_album_moves_at_once()
    {
        var track = Track("Ephemeral Dance", 2026);
        var edited = track.WithAlbumTags(new AlbumTagEdit("A", "AA", "Ephemeral Dance", 2026) { Date = "2026-10-02" },
            new FileStamp(1, DateTime.UtcNow), null);

        Assert.Equal("2026-10-02", edited.ReleaseDate);
        Assert.Equal("2026-10-02", edited.Relocated(@"E:\x.flac", null).ReleaseDate);
    }

    [Fact]
    public void A_version_2_cache_is_shown_but_flagged_for_a_reread()
    {
        var json = JsonSerializer.Serialize(new { Version = 2, Folders = new[] { @"C:\music" }, Tracks = new[] { new { FilePath = @"C:\music\a.flac" } } });
        var parsed = JsonSerializer.Deserialize<LibraryCache>(json)!;

        Assert.True(parsed.NeedsReread);
        Assert.False(LibraryCache.From([@"C:\music"], []).NeedsReread);
        Assert.True(LibraryCache.From([@"C:\music"], [], version: 2).NeedsReread);
    }

    [Fact]
    public async Task A_reread_reads_every_file_even_when_it_matches_the_cache()
    {
        var temp = Path.Combine(Path.GetTempPath(), "audiofool-reread-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            File.Copy(Path.Combine(AppContext.BaseDirectory, "TestData", "sample.flac"), Path.Combine(temp, "a.flac"));
            File.Copy(Path.Combine(AppContext.BaseDirectory, "TestData", "sample.mp3"), Path.Combine(temp, "b.mp3"));

            var first = await LibraryScanner.ScanAsync([temp]);
            var known = first.Library.AllTracks.ToDictionary(t => t.FilePath, t => t, StringComparer.OrdinalIgnoreCase);

            var normal = await LibraryScanner.ScanAsync([temp], known);
            var reread = await LibraryScanner.ScanAsync([temp], known, rereadTags: true);

            Assert.Equal((0, 2), (normal.Summary.Read, normal.Summary.Reused));
            Assert.Equal((2, 0, 0), (reread.Summary.Read, reread.Summary.Reused, reread.Summary.Removed));
            Assert.Equal(2, reread.Library.AllTracks.Count);
        }
        finally
        {
            Directory.Delete(temp, recursive: true);
        }
    }
}
