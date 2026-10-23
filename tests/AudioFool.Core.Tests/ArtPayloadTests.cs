using AudioFool.Core.Library;

namespace AudioFool.Core.Tests;

/// <summary>
/// "Choose Image…" judges a picked file by its bytes. Until session 69 it went by
/// the extension, so a WebP renamed .jpg was embedded, labelled JPEG, as the only cover.
/// </summary>
public class ArtPayloadTests
{
    private static readonly byte[] Jpeg =
        File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "TestData", "cover.jpg"));

    private static readonly byte[] Png =
        File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "TestData", "noise-64.png"));

    /// <summary>The start of a real WebP: "RIFF", a size, "WEBP", a VP8 chunk.</summary>
    private static readonly byte[] WebP =
        [.. "RIFF"u8, 0x24, 0, 0, 0, .. "WEBPVP8 "u8, 0x18, 0, 0, 0, 0x30, 0x01, 0, 0x9D, 0x01, 0x2A, 1, 0, 1, 0];

    [Fact]
    public void A_jpeg_is_a_jpeg() =>
        Assert.Equal("image/jpeg", ArtPayload.FromImage(Jpeg)!.MimeType);

    [Fact]
    public void A_png_is_a_png_whatever_its_name() =>
        Assert.Equal("image/png", ArtPayload.FromImage(Png)!.MimeType);

    [Fact]
    public void A_webp_is_refused() =>
        Assert.Null(ArtPayload.FromImage(WebP));

    [Fact]
    public void Nothing_and_noise_are_refused()
    {
        Assert.Null(ArtPayload.FromImage([]));
        Assert.Null(ArtPayload.FromImage([1, 2, 3, 4, 5, 6, 7, 8]));
    }

    // Session 70: it would be saved as the only cover.
    [Theory]
    [InlineData("cover.jpg")]
    [InlineData("noise-64.png")]
    public void A_cut_off_picture_is_refused(string fixture)
    {
        var whole = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "TestData", fixture));

        Assert.Null(ArtPayload.FromImage(whole.AsSpan(0, whole.Length / 2).ToArray()));
    }

    [Fact]
    public void The_bytes_are_kept_as_they_are() =>
        Assert.Same(Jpeg, ArtPayload.FromImage(Jpeg)!.Bytes);
}
