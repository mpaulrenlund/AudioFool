using AudioFool.Core.Library;

namespace AudioFool.Core.Tests;

/// <summary>
/// Files often carry the cover two or three times, at different sizes; the one
/// shown must be the best, whatever order the copies are stored in.
/// </summary>
public class BestCoverTests
{
    // A PNG header is enough for ImageInfo to measure; the filler stands in for
    // the image data, so two covers of one size can differ in bytes.
    private static byte[] Png(int width, int height, int filler = 0)
    {
        byte[] head =
        [
            0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A,
            0, 0, 0, 13, (byte)'I', (byte)'H', (byte)'D', (byte)'R',
            (byte)(width >> 24), (byte)(width >> 16), (byte)(width >> 8), (byte)width,
            (byte)(height >> 24), (byte)(height >> 16), (byte)(height >> 8), (byte)height,
        ];
        return [.. head, .. new byte[filler]];
    }

    // A JPEG up to its frame header; the APP0 segment's padding is the filler.
    private static byte[] Jpeg(int width, int height, int filler = 0)
    {
        var app0Length = filler + 2;
        return
        [
            0xFF, 0xD8, 0xFF, 0xE0, (byte)(app0Length >> 8), (byte)app0Length, .. new byte[filler],
            0xFF, 0xC0, 0x00, 0x11, 0x08,
            (byte)(height >> 8), (byte)height, (byte)(width >> 8), (byte)width, 0x03,
        ];
    }

    private static TagLib.IPicture Picture(byte[] data, TagLib.PictureType type = TagLib.PictureType.FrontCover) =>
        new TagLib.Picture(new TagLib.ByteVector(data)) { Type = type, MimeType = "image/png" };

    [Fact]
    public void The_largest_cover_wins_wherever_it_is_stored()
    {
        var small = Picture(Png(500, 500));
        var large = Picture(Png(2000, 2000));

        Assert.Same(large, TagReader.BestCover([small, large]));
        Assert.Same(large, TagReader.BestCover([large, small]));
    }

    [Fact]
    public void Of_two_covers_the_same_size_the_bigger_file_wins()
    {
        var lean = Picture(Png(800, 800, filler: 100));
        var rich = Picture(Png(800, 800, filler: 900));

        Assert.Same(rich, TagReader.BestCover([lean, rich, lean]));
    }

    // The user's call: a JPEG of the same picture beats a PNG several times its size.
    [Fact]
    public void Of_two_covers_the_same_size_a_jpeg_beats_a_bigger_png()
    {
        var png = Picture(Png(800, 800, filler: 9000));
        var jpeg = Picture(Jpeg(800, 800, filler: 1000));

        Assert.Same(jpeg, TagReader.BestCover([png, jpeg]));
        Assert.Same(jpeg, TagReader.BestCover([jpeg, png]));
    }

    [Fact]
    public void A_larger_png_still_beats_a_smaller_jpeg()
    {
        var jpeg = Picture(Jpeg(800, 800));
        var png = Picture(Png(1200, 1200));

        Assert.Same(png, TagReader.BestCover([jpeg, png]));
    }

    [Fact]
    public void Of_two_jpegs_the_same_size_the_bigger_file_wins()
    {
        var lean = Picture(Jpeg(500, 500, filler: 100));
        var rich = Picture(Jpeg(500, 500, filler: 400));

        Assert.Same(rich, TagReader.BestCover([lean, rich]));
    }

    [Fact]
    public void Identical_copies_give_the_first()
    {
        var first = Picture(Png(1200, 1200));
        var copy = Picture(Png(1200, 1200));

        Assert.Same(first, TagReader.BestCover([first, copy]));
    }

    [Fact]
    public void A_front_cover_beats_a_larger_picture_of_another_kind()
    {
        var front = Picture(Png(500, 500));
        var back = Picture(Png(3000, 3000), TagLib.PictureType.BackCover);

        Assert.Same(front, TagReader.BestCover([back, front]));
    }

    [Fact]
    public void Without_a_front_cover_the_largest_picture_wins()
    {
        var other = Picture(Png(500, 500), TagLib.PictureType.Other);
        var back = Picture(Png(1000, 1000), TagLib.PictureType.BackCover);

        Assert.Same(back, TagReader.BestCover([other, back]));
    }

    [Fact]
    public void A_readable_cover_beats_a_bigger_one_that_cannot_be_measured()
    {
        var unreadable = Picture(new byte[50_000]);
        var readable = Picture(Png(600, 600));

        Assert.Same(readable, TagReader.BestCover([unreadable, readable]));
    }

    [Fact]
    public void No_pictures_or_only_empty_ones_give_null()
    {
        Assert.Null(TagReader.BestCover([]));
        Assert.Null(TagReader.BestCover([Picture([])]));
    }

    [Theory]
    [InlineData("sample.flac")]
    [InlineData("sample.mp3")]
    public void ReadEmbeddedArt_returns_the_largest_of_several_covers(string fixture)
    {
        using var file = new TempAudioFile(fixture);
        var small = Png(500, 500, filler: 64);
        var large = Png(1500, 1500, filler: 64);

        // Set up the duplicate covers directly; TagWriter only ever writes one.
        using (var tagged = TagLib.File.Create(file.Path))
        {
            tagged.Tag.Pictures = [Picture(small), Picture(large), Picture(small)];
            tagged.Save();
        }

        Assert.Equal(large, TagReader.ReadEmbeddedArt(file.Path));
    }
}
