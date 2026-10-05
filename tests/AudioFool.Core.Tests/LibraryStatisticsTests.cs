using AudioFool.Core.Library;
using AudioFool.Core.Models;

namespace AudioFool.Core.Tests;

public class LibraryStatisticsTests
{
    private static Track T(
        string artist, string album, string ext = "flac",
        int? bitDepth = 16, int? sampleRate = 44_100, int? bitrate = 900,
        int? year = 1997, string kind = "", long size = 1_000, int seconds = 60,
        string title = "Song", int? track = 1, int? trackCount = 10,
        int? disc = 1, int? discCount = 1, string? albumArtist = null,
        string? cover = @"C:\music\cover.jpg") =>
        new()
        {
            FilePath = $@"C:\music\{artist}\{album}\{title}{Guid.NewGuid():N}.{ext}",
            Title = title,
            Artist = artist,
            AlbumArtist = albumArtist ?? artist,
            Album = album,
            Kind = kind == "" ? AudioFormats.KindFor("x." + ext) : kind,
            BitDepth = bitDepth,
            SampleRate = sampleRate,
            Bitrate = bitrate,
            Year = year,
            FileSize = size,
            Duration = TimeSpan.FromSeconds(seconds),
            TrackNumber = track,
            TrackCount = trackCount,
            DiscNumber = disc,
            DiscCount = discCount,
            FolderArtPath = cover,
        };

    private static LibraryStatistics Stats(params Track[] tracks) =>
        LibraryStatistics.Compute(LibraryScanner.Build(tracks));

    [Fact]
    public void An_empty_library_has_no_rows()
    {
        var stats = LibraryStatistics.Compute(MusicLibrary.Empty);

        Assert.Equal(0, stats.TrackCount);
        Assert.Empty(stats.TopArtists);
        Assert.Empty(stats.FileTypes);
        Assert.Empty(stats.TagGaps);
    }

    [Fact]
    public void Totals_add_up_duration_and_size()
    {
        var stats = Stats(
            T("A", "One", size: 100, seconds: 60),
            T("A", "Two", size: 200, seconds: 120),
            T("B", "Three", size: 300, seconds: 30));

        Assert.Equal(3, stats.TrackCount);
        Assert.Equal(3, stats.AlbumCount);
        Assert.Equal(2, stats.ArtistCount);
        Assert.Equal(TimeSpan.FromSeconds(210), stats.TotalDuration);
        Assert.Equal(600, stats.TotalBytes);
    }

    [Fact]
    public void Top_artists_are_ranked_by_track_count_and_capped()
    {
        var tracks = new List<Track>();
        void Add(string artist, int count, string album = "X")
        {
            for (var i = 0; i < count; i++)
                tracks.Add(T(artist, album));
        }

        Add("Six", 6);
        Add("Two", 2);
        Add("Five", 3, "One");
        Add("Five", 2, "Two");
        Add("Four", 4);
        Add("One", 1);
        Add("Three", 3);

        var top = LibraryStatistics.Compute(LibraryScanner.Build(tracks), topArtistCount: 5).TopArtists;

        Assert.Equal(["Six", "Five", "Four", "Three", "Two"], top.Select(a => a.Name));
        Assert.Equal(2, top[1].AlbumCount);
        Assert.Equal(5 / 21.0, top[1].Share, 6);
    }

    [Fact]
    public void Top_artists_group_on_album_artist_like_the_sidebar()
    {
        // A compilation: four different track artists, one album artist.
        var stats = Stats(
            T("Guest 1", "Hits", albumArtist: "Various Artists"),
            T("Guest 2", "Hits", albumArtist: "Various Artists"),
            T("Guest 3", "Hits", albumArtist: "Various Artists"),
            T("Solo", "Mine"));

        Assert.Equal("Various Artists", stats.TopArtists[0].Name);
        Assert.Equal(3, stats.TopArtists[0].TrackCount);
    }

    [Fact]
    public void File_types_carry_share_and_size_and_sum_to_one()
    {
        var stats = Stats(
            T("A", "X", "flac", size: 30),
            T("A", "X", "flac", size: 30),
            T("A", "X", "flac", size: 30),
            T("A", "X", "mp3", bitDepth: null, size: 5));

        Assert.Equal(["FLAC", "MP3"], stats.FileTypes.Select(s => s.Label));
        Assert.Equal(0.75, stats.FileTypes[0].Share, 6);
        Assert.Equal(90, stats.FileTypes[0].Bytes);
        Assert.Equal(1.0, stats.FileTypes.Sum(s => s.Share), 6);
    }

    [Theory]
    [InlineData("flac", 16, 44_100, 900, "FLAC", QualityTier.CdQuality)]
    [InlineData("flac", 16, 48_000, 900, "FLAC", QualityTier.CdQuality)]
    [InlineData("flac", 24, 44_100, 1400, "FLAC", QualityTier.HiRes)]
    [InlineData("flac", 16, 96_000, 1400, "FLAC", QualityTier.HiRes)]
    [InlineData("dsf", 1, 2_822_400, 5644, "DSD", QualityTier.Dsd)]
    [InlineData("mp3", null, 44_100, 320, "MP3", QualityTier.LossyHigh)]
    [InlineData("mp3", null, 44_100, 256, "MP3", QualityTier.LossyHigh)]
    [InlineData("mp3", null, 44_100, 192, "MP3", QualityTier.LossyLow)]
    [InlineData("mp3", null, 44_100, null, "MP3", QualityTier.LossyLow)]
    [InlineData("m4a", 16, 44_100, 850, "ALAC", QualityTier.CdQuality)]
    [InlineData("m4a", null, 44_100, 256, "AAC", QualityTier.LossyHigh)]
    [InlineData("xm", null, 44_100, null, "XM", QualityTier.Module)]
    public void Quality_tiers(string ext, int? depth, int? rate, int? kbps, string kind, QualityTier expected)
    {
        var track = T("A", "X", ext, bitDepth: depth, sampleRate: rate, bitrate: kbps, kind: kind);

        Assert.Equal(expected, LibraryStatistics.Classify(track));
    }

    [Fact]
    public void Quality_rows_follow_tier_order_and_skip_empty_tiers()
    {
        var stats = Stats(
            T("A", "X", "dsf", bitDepth: 1, sampleRate: 2_822_400, kind: "DSD"),
            T("A", "X", "mp3", bitDepth: null, bitrate: 320),
            T("A", "X", "mp3", bitDepth: null, bitrate: 128),
            T("A", "X", "flac", bitDepth: 24),
            T("A", "X", "flac"));

        Assert.Equal(
            ["Hi-Res Lossless", "CD Quality Lossless", "Lossy - Over 256 kbps", "Lossy - Under 256 kbps", "DSD"],
            stats.Quality.Select(s => s.Label));
    }

    [Theory]
    [InlineData("flac", 24, 96_000, 2800, "FLAC", "Hi-Res Lossless")]
    [InlineData("flac", 16, 44_100, 1013, "FLAC", "CD Quality Lossless")]
    [InlineData("mp3", null, 44_100, 320, "MP3", "Lossy")]
    [InlineData("mp3", null, 44_100, 128, "MP3", "Lossy")]
    [InlineData("dsf", 1, 2_822_400, 5644, "DSD", "DSD")]
    public void Now_playing_badge_names_the_tier(string ext, int? depth, int? rate, int? kbps, string kind, string expected)
    {
        var track = T("A", "X", ext, bitDepth: depth, sampleRate: rate, bitrate: kbps, kind: kind);

        Assert.Equal(expected, LibraryStatistics.Badge(track));
    }

    [Fact]
    public void Decades_run_oldest_first_with_untagged_last()
    {
        var stats = Stats(
            T("A", "X", year: 2003),
            T("A", "X", year: null),
            T("A", "X", year: 1969),
            T("A", "X", year: 1960),
            T("A", "X", year: 2009));

        Assert.Equal(["1960s", "2000s", "Unknown"], stats.Decades.Select(s => s.Label));
        Assert.Equal([2, 2, 1], stats.Decades.Select(s => s.Count));
    }

    [Theory]
    [InlineData(1994, 1990)]
    [InlineData(2000, 2000)]
    [InlineData(1999, 1990)]
    public void DecadeOf_rounds_down(int year, int decade) =>
        Assert.Equal(decade, LibraryStatistics.DecadeOf(year));

    [Fact]
    public void Tag_gaps_count_blank_fields_most_missing_first()
    {
        var stats = Stats(
            T("A", "X", year: null, trackCount: null, cover: null),
            T("A", "X", year: null, trackCount: null),
            T("A", "X", year: null),
            T("A", "X"));

        var byField = stats.TagGaps.ToDictionary(g => g.Field);

        Assert.Equal("Year", stats.TagGaps[0].Field);
        Assert.Equal(3, byField["Year"].Missing);
        Assert.Equal(0.75, byField["Year"].Share, 6);
        Assert.Equal(2, byField["Track total"].Missing);
        Assert.Equal(1, byField["Cover image file"].Missing);
        Assert.Equal(0, byField["Title"].Missing);
    }

    [Fact]
    public void Blank_strings_count_as_missing()
    {
        var stats = Stats(T("A", "X", title: "  ", albumArtist: ""));
        var byField = stats.TagGaps.ToDictionary(g => g.Field);

        Assert.Equal(1, byField["Title"].Missing);
        Assert.Equal(1, byField["Album Artist"].Missing);
    }

    /// <summary>
    /// The promise behind a clickable row: the tracks it shows are the tracks it
    /// counted. Checked for every row of every section over a mixed library.
    /// </summary>
    [Fact]
    public void Every_row_filter_selects_exactly_the_tracks_the_row_counted()
    {
        Track[] tracks =
        [
            T("A", "X", "flac", bitDepth: 24, year: 1994),
            T("A", "X", "flac", year: null, trackCount: null, cover: null),
            T("B", "Y", "mp3", bitDepth: null, bitrate: 128, year: 2003, disc: null),
            T("B", "Y", "mp3", bitDepth: null, bitrate: 320, year: 2009, title: ""),
            T("C", "Z", "dsf", bitDepth: 1, sampleRate: 2_822_400, kind: "DSD", year: 1961),
            T("C", "Z", "m4a", kind: "ALAC", albumArtist: ""),
            T("D", "W", "xm", bitDepth: null, kind: "", year: null),
            // One album failing all three album checks.
            T("E", "V", track: 1, trackCount: 13, disc: 1, discCount: 1),
            T("E", "V", track: 2, trackCount: 9, disc: 2, discCount: null),
            T("E", "V", track: 3, trackCount: 13, disc: null, discCount: 1),
        ];
        var stats = Stats(tracks);

        var rows = stats.FileTypes.Concat(stats.Quality).Concat(stats.Decades)
            .Select(s => (s.Label, s.Count, s.Filter))
            .Concat(stats.TagGaps.Select(g => (Label: g.Field, Count: g.Missing, g.Filter)))
            .Concat(stats.AlbumGaps.Select(g => (Label: g.Check, Count: g.Tracks, g.Filter)))
            .ToList();

        Assert.NotEmpty(rows);
        Assert.All(stats.AlbumGaps, g => Assert.True(g.Albums > 0, $"{g.Check} found no album"));
        foreach (var (label, count, filter) in rows)
        {
            Assert.NotNull(filter);
            var shown = filter.Apply(tracks).Count;
            Assert.True(count == shown, $"{label}: counted {count}, filter shows {shown}");
        }
    }

    // ------------------------------------------------- albums that disagree

    [Fact]
    public void Track_total_varies_when_tracks_on_one_disc_disagree()
    {
        // The case that started it: tracks 1-9 "of 9" or blank, 10-13 "of 13".
        var stats = Stats(
            T("Andy Timmons", "Ear X-Tacy", track: 1, trackCount: 9),
            T("Andy Timmons", "Ear X-Tacy", track: 10, trackCount: 13),
            T("Andy Timmons", "Pawn Kings", track: 1, trackCount: null),
            T("Andy Timmons", "Pawn Kings", track: 10, trackCount: 13),
            T("Andy Timmons", "Resolution", track: 1, trackCount: 11),
            T("Andy Timmons", "Resolution", track: 2, trackCount: 11));

        var gap = stats.AlbumGaps.Single(g => g.Check == "Track total varies");
        Assert.Equal(2, gap.Albums);
        Assert.Equal(4, gap.Tracks);
    }

    [Fact]
    public void Each_disc_having_its_own_track_total_is_fine()
    {
        var stats = Stats(
            T("Au5", "Au5", track: 1, trackCount: 15, disc: 1, discCount: 2),
            T("Au5", "Au5", track: 1, trackCount: 16, disc: 2, discCount: 2));

        Assert.All(stats.AlbumGaps, g => Assert.Equal(0, g.Albums));
    }

    [Fact]
    public void Disc_total_varies_counts_a_missing_total()
    {
        var stats = Stats(
            T("A", "X", disc: 1, discCount: 1),
            T("A", "X", disc: 1, discCount: null));

        Assert.Equal(1, stats.AlbumGaps.Single(g => g.Check == "Disc total varies").Albums);
    }

    [Theory]
    [InlineData(1, 1, 2, 1, true)]       // disc 2 of 1 (the Wipeout soundtracks)
    [InlineData(1, 2, null, 2, true)]    // one track has no disc #
    [InlineData(1, 2, 2, 2, false)]      // an ordinary two-disc set
    [InlineData(null, null, null, null, false)] // no disc tags at all
    public void Disc_number_that_cannot_be_right(int? disc1, int? count1, int? disc2, int? count2, bool fails)
    {
        var stats = Stats(
            T("A", "X", track: 1, disc: disc1, discCount: count1),
            T("A", "X", track: 2, disc: disc2, discCount: count2));

        Assert.Equal(fails ? 1 : 0, stats.AlbumGaps.Single(g => g.Check == "Disc # doesn't fit").Albums);
    }

    [Fact]
    public void An_album_fixed_while_filtered_leaves_the_filter()
    {
        var bad = T("A", "X", track: 1, trackCount: 9);
        var good = T("A", "X", track: 10, trackCount: 13);
        var filter = Stats(bad, good).AlbumGaps.Single(g => g.Check == "Track total varies").Filter!;

        Assert.Equal(2, filter.Apply([bad, good]).Count);

        // What a save does: a new Track with the corrected total, same file.
        var fixedTrack = new Track
        {
            FilePath = bad.FilePath, Artist = bad.Artist, AlbumArtist = bad.AlbumArtist,
            Album = bad.Album, TrackNumber = 1, TrackCount = 13, DiscNumber = 1, DiscCount = 1,
        };
        Assert.Empty(filter.Apply([fixedTrack, good]));
    }

    [Fact]
    public void Filters_are_named_for_the_chip()
    {
        var stats = Stats(T("A", "X", year: null), T("A", "X", year: 1994));

        Assert.Equal("Missing Year", stats.TagGaps.Single(g => g.Field == "Year").Filter!.Description);
        Assert.Equal("From the 1990s", stats.Decades[0].Filter!.Description);
        Assert.Equal("FLAC", stats.FileTypes[0].Filter!.Description);
    }

    [Fact]
    public void Fully_tagged_needs_only_the_essential_tags()
    {
        var stats = Stats(
            T("A", "X"),
            // Not essential: none of these stop a track counting as tagged.
            T("A", "X", cover: null, disc: null, discCount: null, trackCount: null, albumArtist: ""),
            // Each essential tag missing on its own.
            T("A", "X", title: ""),
            T("A", "", albumArtist: "A"),
            T("A", "X", year: null),
            T("A", "X", track: null));

        Assert.Equal(2, stats.FullyTaggedCount);
    }
}
