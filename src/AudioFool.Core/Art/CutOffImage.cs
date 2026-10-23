namespace AudioFool.Core.Art;

/// <summary>
/// Tells a JPEG or PNG whose data stops before its end from a whole one (session 70).
/// Windows' decoder draws a cut-off JPEG without an error: the rows it has, then
/// flat grey (#808080). The header still gives the full size, so nothing else
/// notices: the user found two albums showing only the top of their cover.
/// <para>
/// A JPEG is walked block by block to the end-of-image marker after its last
/// scan, so a thumbnail tucked inside an APP1 block (with an end marker of its
/// own) is skipped over, not taken for the end. A PNG is walked chunk by chunk to
/// IEND. Bytes after the end are fine. Anything that isn't a JPEG or PNG, or is
/// laid out in a way the walk doesn't follow, is not called cut off: only data
/// that runs out before the end is.
/// </para>
/// </summary>
public static class CutOffImage
{
    private static ReadOnlySpan<byte> PngSignature => [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    public static bool Is(ReadOnlySpan<byte> data)
    {
        if (data.Length >= 2 && data[0] == 0xFF && data[1] == 0xD8)
            return JpegRunsOut(data);
        if (data.Length >= 8 && data[..8].SequenceEqual(PngSignature))
            return PngRunsOut(data);
        return false;
    }

    /// <summary>
    /// An album's pictures in the order to try them: each whole one as it comes,
    /// then the cut-off ones in their order, so another track's cover or the
    /// folder's file is shown over a cut-off one, and a cut-off one only when
    /// nothing else will do. Lazy: nothing past the first whole one shown is read.
    /// </summary>
    public static IEnumerable<byte[]> WholeFirst(IEnumerable<byte[]?> pictures)
    {
        List<byte[]>? cutOff = null;
        foreach (var picture in pictures)
        {
            if (picture is not { Length: > 0 })
                continue;
            if (Is(picture))
                (cutOff ??= []).Add(picture);
            else
                yield return picture;
        }

        foreach (var picture in cutOff ?? [])
            yield return picture;
    }

    /// <summary>
    /// For a cover file read from its end alone: true when its last bytes are a
    /// JPEG's end marker or a PNG's IEND chunk (trailing zeros allowed), which a
    /// cut-off picture can't end with. A JPEG's scan data never holds FF D9: an FF
    /// in it is always followed by 00 or a restart number.
    /// </summary>
    public static bool EndsWhole(ReadOnlySpan<byte> tail)
    {
        tail = tail.TrimEnd((byte)0);
        return tail.EndsWith((ReadOnlySpan<byte>)[0xFF, 0xD9])
               || tail.EndsWith((ReadOnlySpan<byte>)[(byte)'I', (byte)'E', (byte)'N', (byte)'D', 0xAE, 0x42, 0x60, 0x82]);
    }

    /// <summary>How much of a file's end <see cref="EndsWhole"/> looks at.</summary>
    public const int TailLength = 4096;

    /// <summary>
    /// A cover file on disk: its end first, and only when that isn't a clean end,
    /// the whole file. False when it can't be read: that's not "cut off".
    /// </summary>
    public static bool IsFile(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1);
            var tail = new byte[(int)Math.Min(TailLength, stream.Length)];
            stream.Seek(-tail.Length, SeekOrigin.End);
            stream.ReadExactly(tail);
            if (EndsWhole(tail))
                return false;

            var all = new byte[stream.Length];
            stream.Seek(0, SeekOrigin.Begin);
            stream.ReadExactly(all);
            return Is(all);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool JpegRunsOut(ReadOnlySpan<byte> data)
    {
        var i = 2;
        while (true)
        {
            if (i + 1 >= data.Length)
                return true;
            if (data[i] != 0xFF)
                return false;   // not laid out as expected: not ours to judge

            var marker = data[i + 1];
            if (marker == 0xFF)
            {
                i++;   // fill byte
                continue;
            }

            i += 2;
            if (marker == 0xD9)
                return false;
            if (marker is 0x01 or 0xD8 or (>= 0xD0 and <= 0xD7))
                continue;

            if (i + 2 > data.Length)
                return true;
            var length = (data[i] << 8) | data[i + 1];
            if (length < 2)
                return false;
            if (i + length > data.Length)
                return true;
            i += length;

            if (marker != 0xDA)
                continue;

            // Scan data: runs to the next marker. An FF in it is followed by 00
            // (a stuffed byte) or a restart number; anything else is a marker.
            while (true)
            {
                var next = data[i..].IndexOf((byte)0xFF);
                if (next < 0)
                    return true;
                i += next;
                if (i + 1 >= data.Length)
                    return true;

                var after = data[i + 1];
                if (after is 0x00 or (>= 0xD0 and <= 0xD7))
                    i += 2;
                else if (after == 0xFF)
                    i++;
                else
                    break;
            }
        }
    }

    private static bool PngRunsOut(ReadOnlySpan<byte> data)
    {
        // Chunks: a 4-byte length, a 4-byte type, the data, a 4-byte CRC.
        long i = 8;
        while (true)
        {
            if (i + 8 > data.Length)
                return true;

            var at = (int)i;
            long length = ((uint)data[at] << 24) | ((uint)data[at + 1] << 16) | ((uint)data[at + 2] << 8) | data[at + 3];
            if (data[at + 4] == 'I' && data[at + 5] == 'E' && data[at + 6] == 'N' && data[at + 7] == 'D')
                return false;

            i += 12 + length;
            if (i > data.Length)
                return true;
        }
    }
}
