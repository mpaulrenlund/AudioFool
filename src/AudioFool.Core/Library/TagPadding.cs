namespace AudioFool.Core.Library;

/// <summary>
/// Where a file's tags end and its audio begins, and how much empty room the tags
/// hold, for FLAC and MP3 (ID3v2), read from the bytes alone.
/// </summary>
/// <param name="AudioStart">The first byte after the tags: everything from here on is the music (and any trailing tag).</param>
/// <param name="Padding">The empty room inside the tags, in bytes, headers included.</param>
public sealed record TagLayout(long AudioStart, long Padding);

/// <summary>
/// Cuts the empty room in a file's tags down to <see cref="Reserve"/>.
/// <para>
/// TagLib never makes a file smaller: removing a 2 MB cover leaves 2 MB of padding
/// in its place (measured on FLAC and MP3), which a later save can grow into
/// without rewriting the file. This rewrites the file into a new one with the tags
/// byte for byte as they were, less the padding, and the audio copied byte for
/// byte after them. It never touches the original; the caller checks the copy
/// and swaps it in.
/// </para>
/// <para>
/// Only layouts it fully understands are rewritten. A FLAC with an ID3 tag in
/// front, or an ID3v2 tag that is unsynchronised, has an extended header or a
/// footer, or whose frames don't walk cleanly to zeros, gets null from
/// <see cref="Read"/> and is left alone.
/// </para>
/// </summary>
public static class TagPadding
{
    /// <summary>Room left for later tag edits, so a small one doesn't rewrite the whole file (the user's call: 5 KB).</summary>
    public const int Reserve = 5 * 1024;

    private const int FlacPadding = 1;

    public static TagLayout? Read(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return Read(stream);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public static TagLayout? Read(Stream stream)
    {
        var head = new byte[10];
        stream.Position = 0;
        if (stream.Read(head, 0, 4) < 4)
            return null;

        if (head[0] == 'f' && head[1] == 'L' && head[2] == 'a' && head[3] == 'C')
            return FlacBlocks(stream) is { } blocks
                ? new TagLayout(blocks.AudioStart, blocks.Blocks.Where(b => b.Type == FlacPadding).Sum(b => 4L + b.Length))
                : null;

        if (head[0] == 'I' && head[1] == 'D' && head[2] == '3' && stream.Read(head, 4, 6) == 6)
            return Id3v2(stream, head) is { } tag ? new TagLayout(tag.End, tag.End - tag.FramesEnd) : null;

        return null;
    }

    /// <summary>
    /// Writes <paramref name="source"/> to <paramref name="target"/> with the padding
    /// cut to <see cref="Reserve"/>. False, writing nothing, when the layout isn't
    /// understood or there is nothing to gain.
    /// </summary>
    public static bool TryShrink(string source, string target)
    {
        using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read);
        var head = new byte[10];
        if (input.Read(head, 0, 4) < 4)
            return false;

        if (head[0] == 'f' && head[1] == 'L' && head[2] == 'a' && head[3] == 'C')
        {
            if (FlacBlocks(input) is not { } flac)
                return false;

            var padding = flac.Blocks.Where(b => b.Type == FlacPadding).Sum(b => 4L + b.Length);
            if (padding <= 4 + Reserve)
                return false;

            using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write);
            output.Write("fLaC"u8);
            foreach (var block in flac.Blocks.Where(b => b.Type != FlacPadding))
            {
                // Every kept block is followed by the padding, so none is the last.
                output.Write([(byte)block.Type, (byte)(block.Length >> 16), (byte)(block.Length >> 8), (byte)block.Length]);
                Copy(input, block.Offset + 4, block.Length, output);
            }

            output.Write([0x80 | FlacPadding, (Reserve >> 16) & 0xFF, (Reserve >> 8) & 0xFF, Reserve & 0xFF]);
            output.Write(new byte[Reserve]);
            Copy(input, flac.AudioStart, input.Length - flac.AudioStart, output);
            return true;
        }

        if (head[0] == 'I' && head[1] == 'D' && head[2] == '3' && input.Read(head, 4, 6) == 6)
        {
            if (Id3v2(input, head) is not { } tag || tag.End - tag.FramesEnd <= Reserve)
                return false;

            var size = tag.FramesEnd - 10 + Reserve;
            using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write);
            output.Write(head.AsSpan(0, 6));
            output.Write([(byte)((size >> 21) & 0x7F), (byte)((size >> 14) & 0x7F), (byte)((size >> 7) & 0x7F), (byte)(size & 0x7F)]);
            Copy(input, 10, tag.FramesEnd - 10, output);
            output.Write(new byte[Reserve]);
            Copy(input, tag.End, input.Length - tag.End, output);
            return true;
        }

        return false;
    }

    private sealed record FlacBlock(int Type, long Offset, int Length);

    private sealed record FlacLayout(IReadOnlyList<FlacBlock> Blocks, long AudioStart);

    /// <summary>The metadata blocks after "fLaC"; null if they run past the end or there's no STREAMINFO first.</summary>
    private static FlacLayout? FlacBlocks(Stream stream)
    {
        var blocks = new List<FlacBlock>();
        var header = new byte[4];
        long position = 4;
        while (true)
        {
            stream.Position = position;
            if (stream.Read(header, 0, 4) < 4)
                return null;

            var type = header[0] & 0x7F;
            var length = (header[1] << 16) | (header[2] << 8) | header[3];
            if (type == 127 || (blocks.Count == 0 && type != 0) || position + 4 + length > stream.Length)
                return null;

            blocks.Add(new FlacBlock(type, position, length));
            position += 4 + length;
            if ((header[0] & 0x80) != 0)
                return new FlacLayout(blocks, position);
        }
    }

    private sealed record Id3Layout(long FramesEnd, long End);

    /// <summary>
    /// Walks an ID3v2.2–2.4 tag's frames to where the padding starts, and checks
    /// the padding is all zeros. <paramref name="head"/> is the 10-byte header.
    /// </summary>
    private static Id3Layout? Id3v2(Stream stream, byte[] head)
    {
        var version = head[3];
        var flags = head[5];
        // Unsynchronisation, an extended header, a footer: not walked here.
        if (version is < 2 or > 4 || (flags & 0xF0) != 0)
            return null;
        if ((head[6] | head[7] | head[8] | head[9]) >= 0x80)
            return null;

        long size = (head[6] << 21) | (head[7] << 14) | (head[8] << 7) | head[9];
        var end = 10 + size;
        if (end > stream.Length)
            return null;

        var tag = new byte[size];
        stream.Position = 10;
        stream.ReadExactly(tag);

        var idLength = version == 2 ? 3 : 4;
        var headerLength = version == 2 ? 6 : 10;
        var pos = 0;
        while (pos + headerLength <= size && tag[pos] != 0)
        {
            for (var i = 0; i < idLength; i++)
            {
                if (!(char.IsAsciiLetterUpper((char)tag[pos + i]) || char.IsAsciiDigit((char)tag[pos + i])))
                    return null;
            }

            long frameSize = version switch
            {
                2 => (tag[pos + 3] << 16) | (tag[pos + 4] << 8) | tag[pos + 5],
                3 => ((long)tag[pos + 4] << 24) | ((long)tag[pos + 5] << 16) | ((long)tag[pos + 6] << 8) | tag[pos + 7],
                _ => (tag[pos + 4] & 0x80) != 0 || (tag[pos + 5] & 0x80) != 0 || (tag[pos + 6] & 0x80) != 0 || (tag[pos + 7] & 0x80) != 0
                    ? -1
                    : (tag[pos + 4] << 21) | (tag[pos + 5] << 14) | (tag[pos + 6] << 7) | tag[pos + 7],
            };
            if (frameSize <= 0 || pos + headerLength + frameSize > size)
                return null;

            pos += headerLength + (int)frameSize;
        }

        for (var i = pos; i < size; i++)
        {
            if (tag[i] != 0)
                return null;
        }

        return new Id3Layout(10 + pos, end);
    }

    private static void Copy(Stream input, long offset, long count, Stream output)
    {
        input.Position = offset;
        var buffer = new byte[1 << 20];
        while (count > 0)
        {
            var read = input.Read(buffer, 0, (int)Math.Min(buffer.Length, count));
            if (read == 0)
                throw new EndOfStreamException();
            output.Write(buffer, 0, read);
            count -= read;
        }
    }
}
