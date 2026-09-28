namespace AudioFool.Core.Art;

public enum ImageFormat
{
    Jpeg,
    Png,
}

/// <summary>
/// The format and pixel size of a cover, read from its header bytes. Like
/// <see cref="JpegSize"/>, the format is judged by the bytes, never by a file
/// name: an embedded picture has none, and a MIME type in a tag can be wrong.
/// </summary>
public sealed record ImageInfo(ImageFormat Format, int Width, int Height)
{
    private static ReadOnlySpan<byte> PngSignature => [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    /// <summary>Null for anything that is not a readable JPEG or PNG.</summary>
    public static ImageInfo? Read(ReadOnlySpan<byte> data)
    {
        if (JpegSize.TryRead(data, out var width, out var height) == JpegProbe.Found)
            return new ImageInfo(ImageFormat.Jpeg, width, height);

        // PNG: the signature, then IHDR first, whose data starts with width and
        // height as big-endian 32-bit integers.
        if (data.Length >= 24 && data[..8].SequenceEqual(PngSignature)
            && data[12] == (byte)'I' && data[13] == (byte)'H' && data[14] == (byte)'D' && data[15] == (byte)'R')
        {
            width = (data[16] << 24) | (data[17] << 16) | (data[18] << 8) | data[19];
            height = (data[20] << 24) | (data[21] << 16) | (data[22] << 8) | data[23];
            if (width > 0 && height > 0)
                return new ImageInfo(ImageFormat.Png, width, height);
        }

        return null;
    }

    /// <summary>"1400 × 1400".</summary>
    public string SizeText => $"{Width} × {Height}";

    /// <summary>"JPG" or "PNG", as the folder cover file would be named.</summary>
    public string FormatText => Format == ImageFormat.Png ? "PNG" : "JPG";
}
