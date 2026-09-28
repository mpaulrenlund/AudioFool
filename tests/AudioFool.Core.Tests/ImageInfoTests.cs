using AudioFool.Core.Art;

namespace AudioFool.Core.Tests;

public class ImageInfoTests
{
    private static readonly string TestData = Path.Combine(AppContext.BaseDirectory, "TestData");

    /// <summary>A PNG's signature and IHDR chunk, which is all the header reader looks at.</summary>
    private static byte[] PngHead(int width, int height)
    {
        byte[] head =
        [
            0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A,
            0x00, 0x00, 0x00, 0x0D, (byte)'I', (byte)'H', (byte)'D', (byte)'R',
            (byte)(width >> 24), (byte)(width >> 16), (byte)(width >> 8), (byte)width,
            (byte)(height >> 24), (byte)(height >> 16), (byte)(height >> 8), (byte)height,
            0x08, 0x06, 0x00, 0x00, 0x00,
        ];
        return head;
    }

    [Fact]
    public void Reads_real_jpeg_fixture()
    {
        var info = ImageInfo.Read(File.ReadAllBytes(Path.Combine(TestData, "cover.jpg")));

        Assert.Equal(new ImageInfo(ImageFormat.Jpeg, 8, 8), info);
        Assert.Equal("8 × 8", info!.SizeText);
        Assert.Equal("JPG", info.FormatText);
    }

    [Fact]
    public void Reads_png_size_from_ihdr()
    {
        var info = ImageInfo.Read(PngHead(3000, 2000));

        Assert.Equal(new ImageInfo(ImageFormat.Png, 3000, 2000), info);
        Assert.Equal("3000 × 2000", info!.SizeText);
        Assert.Equal("PNG", info.FormatText);
    }

    [Fact]
    public void Truncated_png_is_not_read()
    {
        Assert.Null(ImageInfo.Read(PngHead(1400, 1400).AsSpan(0, 20)));
    }

    [Fact]
    public void Png_with_zero_size_is_not_read()
    {
        Assert.Null(ImageInfo.Read(PngHead(0, 1400)));
    }

    [Theory]
    [InlineData(new byte[] { })]
    [InlineData(new byte[] { 0x47, 0x49, 0x46, 0x38, 0x39, 0x61, 0x10, 0x00, 0x10, 0x00 })] // GIF89a
    [InlineData(new byte[] { 0x42, 0x4D, 0x00, 0x00 })]                                     // BMP
    public void Other_formats_are_not_read(byte[] data)
    {
        Assert.Null(ImageInfo.Read(data));
    }
}
