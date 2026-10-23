using AudioFool.Core.Art;

namespace AudioFool.Core.Tests;

/// <summary>
/// A picture whose data stops early decodes without an error (grey below the cut
/// for a JPEG, black for a PNG, checked in session 70), so its end must be looked for.
/// </summary>
public sealed class CutOffImageTests : IDisposable
{
    private static readonly string TestData = Path.Combine(AppContext.BaseDirectory, "TestData");

    private readonly string _folder = Path.Combine(Path.GetTempPath(), $"AudioFool-cutoff-{Guid.NewGuid():N}");

    public CutOffImageTests() => Directory.CreateDirectory(_folder);

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    private static byte[] Fixture(string name) => File.ReadAllBytes(Path.Combine(TestData, name));

    public static TheoryData<string> Pictures => ["cover.jpg", "noise-64.jpg", "noise-64.png"];

    [Theory]
    [MemberData(nameof(Pictures))]
    public void A_whole_picture_is_not_cut_off(string name)
    {
        Assert.False(CutOffImage.Is(Fixture(name)));
    }

    [Theory]
    [MemberData(nameof(Pictures))]
    public void A_picture_cut_anywhere_is_cut_off(string name)
    {
        var whole = Fixture(name);

        // Every cut from just after the signature to one byte short of the end. A
        // PNG that lacks only IEND's checksum has all of its picture, so it counts as whole.
        var end = name.EndsWith(".png") ? whole.Length - 4 : whole.Length;
        for (var length = 8; length < end; length++)
            Assert.True(CutOffImage.Is(whole.AsSpan(0, length)), $"{name} cut to {length} of {whole.Length} bytes");
    }

    [Theory]
    [MemberData(nameof(Pictures))]
    public void Bytes_after_the_end_are_fine(string name)
    {
        byte[] padded = [.. Fixture(name), .. new byte[500], 0x12, 0x34];

        Assert.False(CutOffImage.Is(padded));
    }

    [Fact]
    public void A_cut_off_picture_padded_with_zeros_is_still_cut_off()
    {
        var whole = Fixture("noise-64.jpg");
        byte[] padded = [.. whole.AsSpan(0, whole.Length / 3), .. new byte[whole.Length]];

        Assert.True(CutOffImage.Is(padded));
    }

    [Fact]
    public void A_thumbnail_inside_the_jpeg_is_not_taken_for_its_end()
    {
        // An APP1 block, as EXIF has, holding a whole JPEG with its own end marker,
        // put after SOI; then the big picture is cut off.
        var thumb = Fixture("cover.jpg");
        var big = Fixture("noise-64.jpg");
        var app1Length = thumb.Length + 2;
        byte[] withThumb = [0xFF, 0xD8, 0xFF, 0xE1, (byte)(app1Length >> 8), (byte)app1Length, .. thumb, .. big.AsSpan(2)];

        Assert.False(CutOffImage.Is(withThumb));
        Assert.True(CutOffImage.Is(withThumb.AsSpan(0, withThumb.Length - big.Length / 2)));
    }

    [Fact]
    public void A_progressive_jpeg_needs_the_end_after_its_last_scan()
    {
        // Laid out as a progressive JPEG is: several scans, with tables between,
        // scan data holding stuffed FF 00 bytes and restart markers.
        byte[] Segment(byte marker, int payload) =>
            [0xFF, marker, (byte)((payload + 2) >> 8), (byte)(payload + 2), .. new byte[payload]];
        byte[] scanData = [0x12, 0xFF, 0x00, 0x34, 0xFF, 0xD3, 0x56, 0xFF, 0xFF, 0x00, 0x78];

        byte[] jpeg =
        [
            0xFF, 0xD8,
            .. Segment(0xDB, 65),           // DQT
            0xFF, 0xC2, 0x00, 0x11, 0x08, 0x00, 0x40, 0x00, 0x40, 0x03, .. new byte[9],   // SOF2
            .. Segment(0xC4, 30),           // DHT
            .. Segment(0xDA, 10), .. scanData,
            .. Segment(0xC4, 30),
            .. Segment(0xDA, 8), .. scanData,
            .. Segment(0xDA, 8), .. scanData,
            0xFF, 0xD9,
        ];

        Assert.False(CutOffImage.Is(jpeg));
        Assert.Equal(ImageFormat.Jpeg, ImageInfo.Read(jpeg)!.Format);
        for (var length = 4; length < jpeg.Length; length++)
            Assert.True(CutOffImage.Is(jpeg.AsSpan(0, length)), $"cut to {length} of {jpeg.Length}");
    }

    [Theory]
    [InlineData(new byte[] { })]
    [InlineData(new byte[] { 0x47, 0x49, 0x46, 0x38, 0x39, 0x61, 0x10, 0x00 })]   // GIF89a
    [InlineData(new byte[] { 0x52, 0x49, 0x46, 0x46, 0x00, 0x00, 0x00, 0x00 })]   // RIFF (WebP)
    public void Other_formats_are_not_called_cut_off(byte[] data)
    {
        Assert.False(CutOffImage.Is(data));
    }

    [Fact]
    public void A_jpeg_laid_out_wrongly_is_not_called_cut_off()
    {
        // Junk where a marker should be: damaged in some other way, not ours to judge.
        Assert.False(CutOffImage.Is([0xFF, 0xD8, 0x12, 0x34, 0x56, 0x78]));
    }

    [Fact]
    public void An_albums_whole_pictures_come_before_its_cut_off_ones()
    {
        var whole = Fixture("cover.jpg");
        var other = Fixture("noise-64.png");
        var cut = Fixture("noise-64.jpg").AsSpan(0, 1000).ToArray();

        Assert.Equal([whole, other, cut], CutOffImage.WholeFirst([cut, null, whole, [], other]));
        Assert.Equal([cut], CutOffImage.WholeFirst([null, cut]));
        Assert.Empty(CutOffImage.WholeFirst([null, []]));
    }

    [Fact]
    public void Nothing_past_the_first_whole_picture_taken_is_read()
    {
        var read = 0;
        IEnumerable<byte[]?> Pictures()
        {
            read++;
            yield return Fixture("noise-64.jpg").AsSpan(0, 1000).ToArray();
            read++;
            yield return Fixture("cover.jpg");
            read++;
            yield return Fixture("noise-64.png");
        }

        Assert.Equal(Fixture("cover.jpg"), CutOffImage.WholeFirst(Pictures()).First());
        Assert.Equal(2, read);
    }

    [Theory]
    [MemberData(nameof(Pictures))]
    public void A_cover_file_is_judged_from_its_end_or_read_whole(string name)
    {
        var whole = Fixture(name);
        var good = Path.Combine(_folder, "good" + Path.GetExtension(name));
        var cut = Path.Combine(_folder, "cut" + Path.GetExtension(name));
        var trailing = Path.Combine(_folder, "trailing" + Path.GetExtension(name));
        File.WriteAllBytes(good, whole);
        File.WriteAllBytes(cut, whole.AsSpan(0, whole.Length * 2 / 3).ToArray());
        File.WriteAllBytes(trailing, [.. whole, 0x01, 0x02, 0x03]);   // not a clean end: read whole

        Assert.False(CutOffImage.IsFile(good));
        Assert.True(CutOffImage.IsFile(cut));
        Assert.False(CutOffImage.IsFile(trailing));
    }

    [Fact]
    public void A_missing_or_empty_cover_file_is_not_called_cut_off()
    {
        var empty = Path.Combine(_folder, "empty.jpg");
        File.WriteAllBytes(empty, []);

        Assert.False(CutOffImage.IsFile(empty));
        Assert.False(CutOffImage.IsFile(Path.Combine(_folder, "missing.jpg")));
    }
}
