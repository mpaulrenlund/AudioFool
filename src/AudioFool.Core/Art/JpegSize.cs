namespace AudioFool.Core.Art;

public enum JpegProbe
{
    /// <summary>The bytes are not a JPEG, or are a corrupt one.</summary>
    NotJpeg,

    /// <summary>A JPEG so far, but the frame header lies beyond the bytes given.</summary>
    NeedMore,

    /// <summary>A JPEG whose pixel size was read from its frame header.</summary>
    Found,
}

/// <summary>
/// Reads a JPEG's pixel size from the start of the file, so a candidate cover can
/// be measured from its first few kilobytes instead of downloading megabytes.
/// Judges the format by the bytes, never by the URL or content type - a store can
/// serve anything under a ".jpg" name.
/// </summary>
public static class JpegSize
{
    public static JpegProbe TryRead(ReadOnlySpan<byte> data, out int width, out int height)
    {
        width = 0;
        height = 0;

        if (data.Length < 2)
            return JpegProbe.NeedMore;
        if (data[0] != 0xFF || data[1] != 0xD8)
            return JpegProbe.NotJpeg;

        var i = 2;
        while (true)
        {
            if (i + 1 >= data.Length)
                return JpegProbe.NeedMore;
            if (data[i] != 0xFF)
                return JpegProbe.NotJpeg;

            var marker = data[i + 1];
            if (marker == 0xFF)
            {
                i++; // fill byte
                continue;
            }

            i += 2;

            // Markers that carry no length field.
            if (marker is 0x01 or 0xD8 or (>= 0xD0 and <= 0xD7))
                continue;

            // Scan data or end of image before any frame header: not a usable JPEG.
            if (marker is 0xDA or 0xD9)
                return JpegProbe.NotJpeg;

            if (i + 2 > data.Length)
                return JpegProbe.NeedMore;

            var length = (data[i] << 8) | data[i + 1];
            if (length < 2)
                return JpegProbe.NotJpeg;

            // SOF0-SOF15, except DHT (C4), JPG (C8) and DAC (CC), which share the range.
            if (marker is >= 0xC0 and <= 0xCF and not 0xC4 and not 0xC8 and not 0xCC)
            {
                if (i + 7 > data.Length)
                    return JpegProbe.NeedMore;

                height = (data[i + 3] << 8) | data[i + 4];
                width = (data[i + 5] << 8) | data[i + 6];
                return width > 0 && height > 0 ? JpegProbe.Found : JpegProbe.NotJpeg;
            }

            i += length;
        }
    }
}
