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
    [
        0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A,
        0, 0, 0, 13, (byte)'I', (byte)'H', (byte)'D', (byte)'R',
        0, 0, 1, 0, 0, 0, 1, 0, 8, 2, 0, 0, 0,
    ];

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

    [Fact]
    public void The_bytes_are_kept_as_they_are() =>
        Assert.Same(Jpeg, ArtPayload.FromImage(Jpeg)!.Bytes);
}
