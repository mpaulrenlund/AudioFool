namespace AudioFool.Core.Library;

/// <summary>
/// Where a file's tags end and its audio begins, how much empty room the tags
/// hold, and how many pictures they carry, for FLAC, MP3 and DSF, read from the bytes alone.
/// </summary>
/// <param name="AudioStart">The first byte after the tags: everything from here on is the music (and any trailing tag). In a DSF, the first byte after its 28-byte header.</param>
/// <param name="Padding">The empty room inside the tags, in bytes, headers included.</param>
/// <param name="Pictures">FLAC PICTURE blocks and ID3v2 APIC/PIC frames, in every tag at the front.</param>
/// <param name="Id3Tags">How many ID3v2 tags sit back to back at the front.</param>
/// <param name="HasSpareRoom">Some tag holds more padding than <see cref="TagPadding.Reserve"/>, so a rewrite would free space.</param>
/// <param name="AudioEnd">Where the music ends: a DSF's tag, which comes after it. Null for FLAC and MP3.</param>
public sealed record TagLayout(long AudioStart, long Padding, int Pictures, int Id3Tags, bool HasSpareRoom, long? AudioEnd = null);

/// <summary>
/// Rewrites a file with its tags' padding cut to <see cref="Reserve"/>, and can
/// drop its extra pictures in the same pass, without TagLib.
/// <para>
/// TagLib never makes a file smaller: removing a 2 MB cover leaves 2 MB of padding
/// in its place, which a later save can grow into without rewriting the file. And
/// it can't be trusted to remove pictures everywhere: some MP3s carry two ID3v2
/// tags back to back and TagLib rewrites only the first (Buckethead's, measured),
/// and a FLAC whose padding would grow past 16 MB gets a padding block that
/// doesn't fit (Shpongle's). So this works on the bytes: every block and frame it
/// keeps is copied as it was, the ones it drops are left out, and the audio is
/// copied byte for byte after them. It never touches the original; the caller
/// checks the copy and swaps it in.
/// </para>
/// <para>
/// Only layouts it fully understands are rewritten: ID3v2 tags (one or several)
/// before the audio or before a FLAC's "fLaC", then the FLAC's metadata blocks;
/// or a DSF, whose one ID3v2 tag sits after the music, where its "DSD " header
/// points. A DSF without a tag isn't rewritten.
/// An ID3v2 tag that is unsynchronised, has an extended header or a footer, or
/// whose frames don't walk cleanly to zeros, makes <see cref="Read"/> return null.
/// A picture frame that is compressed or encrypted can't be compared, so a file
/// holding one isn't rewritten.
/// </para>
/// </summary>
public static class TagPadding
{
    /// <summary>Room left for later tag edits, so a small one doesn't rewrite the whole file (the user's call: 5 KB).</summary>
    public const int Reserve = 5 * 1024;

    private const int FlacPadding = 1;
    private const int FlacPicture = 6;

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

    public static TagLayout? Read(Stream stream) =>
        Parse(stream) is { } layout
            ? new TagLayout(layout.AudioStart, layout.Padding,
                layout.Id3.Sum(t => t.Frames.Count(f => f.IsPicture)) + (layout.Flac?.Count(b => b.Type == FlacPicture) ?? 0),
                layout.Id3.Count,
                layout.Id3.Any(t => t.End - t.FramesEnd > Reserve)
                || (layout.Flac?.Where(b => b.Type == FlacPadding).Sum(b => 4L + b.Length) ?? 0) > 4 + Reserve
                || layout.DsfTrailer > 0,
                layout.DsfTag)
            : null;

    /// <summary>
    /// Writes <paramref name="source"/> to <paramref name="target"/> with the
    /// padding cut to <see cref="Reserve"/>. With <paramref name="keepOnlyPicture"/>,
    /// every picture goes (PICTURE blocks, APIC and PIC frames, in every tag) but
    /// the first holding exactly those image bytes. False, writing nothing, when the
    /// layout isn't understood, a picture can't be read, the picture to keep isn't
    /// there, or there is nothing to gain.
    /// </summary>
    public static bool TryShrink(string source, string target, byte[]? keepOnlyPicture = null)
    {
        using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (Parse(input) is not { } layout)
            return false;

        var droppedBlocks = new HashSet<FlacBlock>();
        var droppedFrames = new HashSet<Id3Frame>();
        if (keepOnlyPicture is not null)
        {
            var kept = false;
            foreach (var tag in layout.Id3)
            {
                foreach (var frame in tag.Frames.Where(f => f.IsPicture))
                {
                    if (FrameImage(input, tag, frame) is not { } image)
                        return false;
                    if (!kept && image.AsSpan().SequenceEqual(keepOnlyPicture))
                        kept = true;
                    else
                        droppedFrames.Add(frame);
                }
            }

            foreach (var block in layout.Flac?.Where(b => b.Type == FlacPicture) ?? [])
            {
                if (!kept && BlockImage(input, block).AsSpan().SequenceEqual(keepOnlyPicture))
                    kept = true;
                else
                    droppedBlocks.Add(block);
            }

            if (!kept)
                return false;
        }

        var flacPadding = layout.Flac?.Where(b => b.Type == FlacPadding).Sum(b => 4L + b.Length) ?? 0;
        var gains = droppedBlocks.Count > 0 || droppedFrames.Count > 0
                    || layout.Id3.Any(t => t.End - t.FramesEnd > Reserve)
                    || (layout.Flac is not null && flacPadding > 4 + Reserve)
                    || layout.DsfTrailer > 0;
        if (!gains)
            return false;

        using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write);
        if (layout.DsfTag is { } tagStart)
        {
            // The header, the fmt and data chunks as they were, then the tag, and
            // nothing after it. The tag stays where the header points; only the
            // file size changes, and is written right.
            Copy(input, 0, tagStart, output);
            WriteId3(input, layout, droppedFrames, output);
            output.Position = 12;
            output.Write(BitConverter.GetBytes((ulong)output.Length));
            return true;
        }

        WriteId3(input, layout, droppedFrames, output);

        if (layout.Flac is { } flac)
        {
            output.Write("fLaC"u8);
            foreach (var block in flac.Where(b => b.Type != FlacPadding && !droppedBlocks.Contains(b)))
            {
                // Every kept block is followed by the padding, so none is the last.
                output.Write([(byte)block.Type, (byte)(block.Length >> 16), (byte)(block.Length >> 8), (byte)block.Length]);
                Copy(input, block.Offset + 4, block.Length, output);
            }

            output.Write([0x80 | FlacPadding, (Reserve >> 16) & 0xFF, (Reserve >> 8) & 0xFF, Reserve & 0xFF]);
            output.Write(new byte[Reserve]);
        }

        Copy(input, layout.AudioStart, input.Length - layout.AudioStart, output);
        return true;
    }

    /// <summary>Each ID3v2 tag, less its dropped frames, with its padding cut to the reserve.</summary>
    private static void WriteId3(Stream input, Layout layout, HashSet<Id3Frame> droppedFrames, Stream output)
    {
        foreach (var tag in layout.Id3)
        {
            var drops = tag.Frames.Where(droppedFrames.Contains).ToList();
            var padding = tag.End - tag.FramesEnd;
            if (drops.Count == 0 && padding <= Reserve)
            {
                Copy(input, tag.Start, tag.End - tag.Start, output);
                continue;
            }

            // Never grows: the room freed by dropped frames counts toward the reserve.
            var newPadding = Math.Min(padding + drops.Sum(f => f.Length), Reserve);
            var size = tag.Frames.Where(f => !droppedFrames.Contains(f)).Sum(f => f.Length) + newPadding;
            var head = new byte[6];
            input.Position = tag.Start;
            input.ReadExactly(head);
            output.Write(head);
            output.Write([(byte)((size >> 21) & 0x7F), (byte)((size >> 14) & 0x7F), (byte)((size >> 7) & 0x7F), (byte)(size & 0x7F)]);
            foreach (var frame in tag.Frames.Where(f => !droppedFrames.Contains(f)))
                Copy(input, frame.Start, frame.Length, output);
            output.Write(new byte[newPadding]);
        }
    }

    private sealed record FlacBlock(int Type, long Offset, int Length);

    /// <param name="Start">Where the frame's header begins.</param>
    /// <param name="Length">Header and body.</param>
    /// <param name="HeaderLength">6 for ID3v2.2, 10 otherwise.</param>
    /// <param name="Flags">The format flags (the second flag byte; 0 for ID3v2.2).</param>
    private sealed record Id3Frame(string Id, long Start, int Length, int HeaderLength, int Flags)
    {
        public bool IsPicture => Id is "APIC" or "PIC";
    }

    private sealed record Id3Layout(long Start, int Version, long FramesEnd, long End, IReadOnlyList<Id3Frame> Frames);

    /// <summary>The ID3v2 tags at the front, then a FLAC's metadata blocks if "fLaC" follows them; or a DSF's one tag, at <paramref name="DsfTag"/>.</summary>
    /// <param name="DsfTrailer">Bytes after a DSF's tag, past the file its header describes.</param>
    private sealed record Layout(IReadOnlyList<Id3Layout> Id3, IReadOnlyList<FlacBlock>? Flac, long AudioStart, long? DsfTag = null, long DsfTrailer = 0)
    {
        public long Padding =>
            Id3.Sum(t => t.End - t.FramesEnd) + (Flac?.Where(b => b.Type == FlacPadding).Sum(b => 4L + b.Length) ?? 0) + DsfTrailer;
    }

    private static Layout? Parse(Stream stream)
    {
        if (IsDsf(stream))
            return Dsf(stream);

        var id3 = new List<Id3Layout>();
        long position = 0;
        var head = new byte[10];
        while (true)
        {
            stream.Position = position;
            if (stream.Read(head, 0, 4) < 4)
                return null;

            if (head[0] == 'I' && head[1] == 'D' && head[2] == '3')
            {
                if (stream.Read(head, 4, 6) < 6 || Id3v2(stream, position, head) is not { } tag)
                    return null;
                id3.Add(tag);
                position = tag.End;
                continue;
            }

            if (head[0] == 'f' && head[1] == 'L' && head[2] == 'a' && head[3] == 'C')
                return FlacBlocks(stream, position + 4) is { } flac ? new Layout(id3, flac.Blocks, flac.AudioStart) : null;

            // Anything else after the tags is audio; with no tag at all, not a layout this knows.
            return id3.Count > 0 ? new Layout(id3, null, position) : null;
        }
    }

    private static bool IsDsf(Stream stream)
    {
        var id = new byte[4];
        stream.Position = 0;
        return stream.Read(id, 0, 4) == 4 && id.AsSpan().SequenceEqual("DSD "u8);
    }

    /// <summary>
    /// A DSF's header: "DSD ", a chunk size of 28, the file size, then where the
    /// ID3v2 tag starts. The file size must be where the tag ends, or one more,
    /// which is what TagLib writes. Bytes after the tag, beyond the file the header
    /// describes, are counted as spare room: the library's Dark Side of the Moon
    /// DSFs carry 1.38 MB each, left behind by a tagger that shrank the tag but not
    /// the file. Null when the header doesn't check out or there's no tag.
    /// </summary>
    private static Layout? Dsf(Stream stream)
    {
        var header = new byte[28];
        stream.Position = 0;
        if (stream.Read(header, 0, 28) < 28)
            return null;

        var chunkSize = BitConverter.ToUInt64(header, 4);
        var fileSize = BitConverter.ToUInt64(header, 12);
        var pointer = BitConverter.ToUInt64(header, 20);
        if (chunkSize != 28 || pointer < 28 || pointer + 10 > (ulong)stream.Length)
            return null;

        var head = new byte[10];
        stream.Position = (long)pointer;
        stream.ReadExactly(head);
        if (!(head[0] == 'I' && head[1] == 'D' && head[2] == '3')
            || Id3v2(stream, (long)pointer, head) is not { } tag
            || (fileSize != (ulong)tag.End && fileSize != (ulong)tag.End + 1))
            return null;

        return new Layout([tag], null, 28, (long)pointer, stream.Length - tag.End);
    }

    /// <summary>The metadata blocks from <paramref name="start"/>; null if they run past the end or there's no STREAMINFO first.</summary>
    private static (List<FlacBlock> Blocks, long AudioStart)? FlacBlocks(Stream stream, long start)
    {
        var blocks = new List<FlacBlock>();
        var header = new byte[4];
        var position = start;
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
                return (blocks, position);
        }
    }

    /// <summary>The image bytes inside a PICTURE block, after its type, MIME type, description and sizes.</summary>
    private static byte[] BlockImage(Stream stream, FlacBlock block)
    {
        var data = new byte[block.Length];
        stream.Position = block.Offset + 4;
        stream.ReadExactly(data);

        int Int(int at) => (data[at] << 24) | (data[at + 1] << 16) | (data[at + 2] << 8) | data[at + 3];
        var pos = 4;                     // picture type
        pos += 4 + Int(pos);             // MIME type
        pos += 4 + Int(pos);             // description
        pos += 16;                       // width, height, depth, colours
        var length = Int(pos);
        pos += 4;
        return pos + length <= data.Length && length >= 0 ? data[pos..(pos + length)] : [];
    }

    /// <summary>
    /// The image bytes inside an APIC (or v2.2 PIC) frame, after its text encoding,
    /// MIME type (or 3-letter format), picture type and description. Null for a
    /// frame that is compressed, encrypted, unsynchronised or otherwise not plain.
    /// </summary>
    private static byte[]? FrameImage(Stream stream, Id3Layout tag, Id3Frame frame)
    {
        if (frame.Flags != 0)
            return null;

        var body = new byte[frame.Length - frame.HeaderLength];
        stream.Position = frame.Start + frame.HeaderLength;
        stream.ReadExactly(body);
        if (body.Length < 4)
            return null;

        var encoding = body[0];
        int pos;
        if (frame.Id == "PIC")
        {
            pos = 1 + 3;
        }
        else
        {
            var mimeEnd = Array.IndexOf(body, (byte)0, 1);
            if (mimeEnd < 0)
                return null;
            pos = mimeEnd + 1;
        }

        pos++; // picture type
        if (encoding is 1 or 2)
        {
            // UTF-16: a 16-bit null on an even boundary from the description's start.
            var start = pos;
            while (pos + 1 < body.Length && !(body[pos] == 0 && body[pos + 1] == 0 && (pos - start) % 2 == 0))
                pos++;
            pos += 2;
        }
        else
        {
            var end = Array.IndexOf(body, (byte)0, pos);
            if (end < 0)
                return null;
            pos = end + 1;
        }

        return pos <= body.Length ? body[pos..] : null;
    }

    /// <summary>
    /// Walks an ID3v2.2–2.4 tag's frames to where the padding starts, and checks
    /// the padding is all zeros. <paramref name="head"/> is the 10-byte header at <paramref name="start"/>.
    /// </summary>
    private static Id3Layout? Id3v2(Stream stream, long start, byte[] head)
    {
        var version = head[3];
        var flags = head[5];
        // Unsynchronisation, an extended header, a footer: not walked here.
        if (version is < 2 or > 4 || (flags & 0xF0) != 0)
            return null;
        if ((head[6] | head[7] | head[8] | head[9]) >= 0x80)
            return null;

        long size = (head[6] << 21) | (head[7] << 14) | (head[8] << 7) | head[9];
        var end = start + 10 + size;
        if (end > stream.Length)
            return null;

        var tag = new byte[size];
        stream.Position = start + 10;
        stream.ReadExactly(tag);

        var idLength = version == 2 ? 3 : 4;
        var headerLength = version == 2 ? 6 : 10;
        var frames = new List<Id3Frame>();
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

            var id = System.Text.Encoding.ASCII.GetString(tag, pos, idLength);
            frames.Add(new Id3Frame(id, start + 10 + pos, headerLength + (int)frameSize, headerLength,
                version == 2 ? 0 : tag[pos + 9]));
            pos += headerLength + (int)frameSize;
        }

        for (var i = pos; i < size; i++)
        {
            if (tag[i] != 0)
                return null;
        }

        return new Id3Layout(start, version, start + 10 + pos, end, frames);
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
