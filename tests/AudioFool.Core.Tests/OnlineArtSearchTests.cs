using AudioFool.Core.Art;

namespace AudioFool.Core.Tests;

public class OnlineArtSearchTests
{
    private static readonly string TestData = Path.Combine(AppContext.BaseDirectory, "TestData");

    /// <summary>A minimal JPEG head: SOI, an APP0 segment, then a frame header of the given kind and size.</summary>
    private static byte[] JpegHead(byte sof, int width, int height, int app0Padding = 14)
    {
        var bytes = new List<byte> { 0xFF, 0xD8, 0xFF, 0xE0 };
        var app0Length = app0Padding + 2;
        bytes.Add((byte)(app0Length >> 8));
        bytes.Add((byte)app0Length);
        bytes.AddRange(new byte[app0Padding]);
        bytes.AddRange([0xFF, sof, 0x00, 0x11, 0x08,
            (byte)(height >> 8), (byte)height, (byte)(width >> 8), (byte)width, 0x03]);
        return [.. bytes];
    }

    [Fact]
    public void Reads_size_of_real_jpeg_fixture()
    {
        var bytes = File.ReadAllBytes(Path.Combine(TestData, "cover.jpg"));

        Assert.Equal(JpegProbe.Found, JpegSize.TryRead(bytes, out var w, out var h));
        Assert.Equal((8, 8), (w, h));
    }

    [Theory]
    [InlineData(0xC0)] // baseline
    [InlineData(0xC2)] // progressive
    public void Reads_size_from_frame_header(byte sof)
    {
        Assert.Equal(JpegProbe.Found, JpegSize.TryRead(JpegHead(sof, 3000, 2000), out var w, out var h));
        Assert.Equal((3000, 2000), (w, h));
    }

    [Fact]
    public void Skips_huffman_table_which_shares_the_frame_marker_range()
    {
        // DHT (C4) sits between SOF markers; reading it as a frame would give garbage.
        byte[] dht = [0xFF, 0xC4, 0x00, 0x06, 0x0B, 0xB8, 0x0B, 0xB8];
        var head = JpegHead(0xC0, 1400, 1400);
        byte[] bytes = [.. head[..2], .. dht, .. head[2..]];

        Assert.Equal(JpegProbe.Found, JpegSize.TryRead(bytes, out var w, out var h));
        Assert.Equal((1400, 1400), (w, h));
    }

    [Fact]
    public void Truncated_before_the_frame_header_asks_for_more()
    {
        var head = JpegHead(0xC0, 3000, 3000, app0Padding: 4000);
        Assert.Equal(JpegProbe.NeedMore, JpegSize.TryRead(head.AsSpan(0, 1024), out _, out _));
    }

    [Fact]
    public void Png_is_not_a_jpeg()
    {
        byte[] png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 0x0D];
        Assert.Equal(JpegProbe.NotJpeg, JpegSize.TryRead(png, out _, out _));
    }

    [Theory]
    [InlineData(600, 600, OnlineArtSearch.Verdict.Accept)]
    [InlineData(1000, 1000, OnlineArtSearch.Verdict.Accept)]
    [InlineData(3000, 3000, OnlineArtSearch.Verdict.Accept)]
    [InlineData(599, 1000, OnlineArtSearch.Verdict.TooSmall)]
    [InlineData(1500, 500, OnlineArtSearch.Verdict.TooSmall)]
    public void Keeps_only_covers_at_least_600_on_both_sides(int w, int h, OnlineArtSearch.Verdict expected) =>
        Assert.Equal(expected, OnlineArtSearch.Classify(JpegProbe.Found, w, h));

    [Theory]
    [InlineData(JpegProbe.NotJpeg)]
    [InlineData(JpegProbe.NeedMore)]
    public void An_unmeasurable_file_is_rejected_as_not_jpeg(JpegProbe probe) =>
        Assert.Equal(OnlineArtSearch.Verdict.NotJpeg, OnlineArtSearch.Classify(probe, 3000, 3000));

    [Theory]
    [InlineData("https://is1-ssl.mzstatic.com/image/thumb/Music/v4/a/b/13UAEIM19273.rgb.jpg/100x100bb.jpg",
                "https://is1-ssl.mzstatic.com/image/thumb/Music/v4/a/b/13UAEIM19273.rgb.jpg/10000x10000bb.jpg")]
    [InlineData("https://is1-ssl.mzstatic.com/image/thumb/Music/v4/a/b/191404145081.png/100x100bb.jpg",
                "https://is1-ssl.mzstatic.com/image/thumb/Music/v4/a/b/191404145081.png/10000x10000bb.jpg")]
    public void Itunes_url_is_rewritten_to_a_jpeg_rendition(string thumb, string expected) =>
        Assert.Equal(expected, OnlineArtSearch.ItunesArtworkUrl(thumb, 10000));

    [Fact]
    public void Itunes_url_without_a_size_suffix_is_skipped() =>
        Assert.Null(OnlineArtSearch.ItunesArtworkUrl("https://example.com/cover", 10000));

    [Fact]
    public void Parses_itunes_results()
    {
        const string json = """
            {"resultCount":2,"results":[
              {"artistName":"Elton John","collectionName":"Goodbye Yellow Brick Road",
               "artworkUrl100":"https://is1-ssl.mzstatic.com/image/thumb/x/13U.jpg/100x100bb.jpg"},
              {"artistName":"No Art","collectionName":"Missing"}]}
            """;

        var found = Assert.Single(OnlineArtSearch.ParseItunes(json));
        Assert.Equal("iTunes", found.Source);
        Assert.Equal("Elton John – Goodbye Yellow Brick Road", found.Title);
        Assert.EndsWith("/10000x10000bb.jpg", found.FullUrl);
        Assert.EndsWith("/600x600bb.jpg", found.PreviewUrl);
    }

    [Fact]
    public void Parses_close_musicbrainz_matches_only()
    {
        const string json = """
            {"release-groups":[
              {"id":"80df0f1b","score":100,"title":"Goodbye Yellow Brick Road",
               "artist-credit":[{"name":"Elton John","joinphrase":""}]},
              {"id":"weak","score":42,"title":"Something Else"}]}
            """;

        var group = Assert.Single(OnlineArtSearch.ParseMusicBrainz(json));
        Assert.Equal(new OnlineArtSearch.ReleaseGroup("80df0f1b", "Elton John", "Goodbye Yellow Brick Road"), group);
    }

    private static readonly OnlineArtSearch.ReleaseGroup Group = new("80df0f1b", "Elton John", "Goodbye Yellow Brick Road");

    private static ArtCandidate Found(string artist, string album) => new("iTunes", artist, album, "full", "preview");

    [Theory]
    [InlineData("Rush", "Moving Pictures (Remastered)", 3)]
    [InlineData("Rush", "Moving Pictures: Live 2011", 3)]
    [InlineData("Rush", "Permanent Waves", 2)]
    [InlineData("Peter Gabriel", "Moving Pictures", 1)]
    [InlineData("Fleetwood Mac", "Rumours", 0)]
    public void Relevance_scores_artist_above_album(string artist, string album, int expected) =>
        Assert.Equal(expected, OnlineArtSearch.Relevance(Found(artist, album), "Rush", "Moving Pictures"));

    [Fact]
    public void Relevance_ignores_punctuation_accents_case_and_a_leading_the()
    {
        // The library has a curly apostrophe; MusicBrainz and iTunes may not.
        Assert.Equal(3, OnlineArtSearch.Relevance(
            Found("BADBADNOTGOOD", "Can't Leave the Night / Sustain"), "BadBadNotGood", "Can’t Leave the Night / Sustain"));
        Assert.Equal(3, OnlineArtSearch.Relevance(Found("Beatles", "Abbey Road"), "The Beatles", "Abbey Road"));
        Assert.Equal(3, OnlineArtSearch.Relevance(Found("Bjork", "Homogenic"), "Björk", "Homogenic"));
        Assert.Equal(3, OnlineArtSearch.Relevance(Found("AC-DC", "Back in Black"), "AC/DC", "Back In Black"));
    }

    [Fact]
    public void Covers_must_be_by_the_artist_when_one_is_known()
    {
        Assert.Equal(2, OnlineArtSearch.MinimumRelevance("Rush"));
        Assert.Equal(1, OnlineArtSearch.MinimumRelevance(""));
    }

    [Fact]
    public void Parses_cover_art_archive_front_covers_over_https()
    {
        const string json = """
            {"images":[
              {"front":true,"image":"http://coverartarchive.org/release/r/1.jpg",
               "thumbnails":{"500":"http://coverartarchive.org/release/r/1-500.jpg","large":"x"}},
              {"front":false,"image":"http://coverartarchive.org/release/r/back.jpg"}]}
            """;

        var found = Assert.Single(OnlineArtSearch.ParseCoverArtArchive(json, Group));
        Assert.Equal("Elton John – Goodbye Yellow Brick Road", found.Title);
        Assert.Equal("https://coverartarchive.org/release/r/1.jpg", found.FullUrl);
        Assert.Equal("https://coverartarchive.org/release/r/1-500.jpg", found.PreviewUrl);
    }

    [Fact]
    public void Parses_fanart_album_covers_with_preview_path()
    {
        const string json = """
            {"name":"Elton John","albums":{"80df0f1b":{
              "albumcover":[{"id":"1","url":"http://assets.fanart.tv/fanart/music/a/albumcover/gybr-1.jpg","likes":"2"}],
              "cdart":[{"id":"2","url":"http://assets.fanart.tv/fanart/music/a/cdart/disc.png"}]}}}
            """;

        var found = Assert.Single(OnlineArtSearch.ParseFanart(json, Group));
        Assert.Equal("fanart.tv", found.Source);
        Assert.Equal("https://assets.fanart.tv/fanart/music/a/albumcover/gybr-1.jpg", found.FullUrl);
        Assert.Equal("https://assets.fanart.tv/preview/music/a/albumcover/gybr-1.jpg", found.PreviewUrl);
    }

    [Fact]
    public void Escapes_lucene_specials_in_album_titles() =>
        Assert.Equal(@"Can't Leave the Night \/ Sustain \(Live\)",
            OnlineArtSearch.LuceneEscape("Can't Leave the Night / Sustain (Live)"));
}
