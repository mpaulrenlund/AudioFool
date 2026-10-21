namespace AudioFool.Core.Library;

/// <summary>
/// A file as a save would leave it, without the file itself being touched: reads
/// come from the file, writes are kept here, in 4 KB pages. TagLib makes a save
/// against it, and what it wrote then says how the real save can be made safely
/// (<see cref="TagWriter"/>): a save that kept the file's size changed only the
/// pages kept here, which can be written into the file as they are; one that
/// didn't moved the audio along the file, and is made on a copy instead.
/// <para>
/// Moving the audio rewrites everything after the tag, so a save that writes more
/// than <c>limit</c> bytes stops with <see cref="TooMuchRewrittenException"/>:
/// it can only be one that moves the audio, and holding the file in memory to
/// find out would be no use.
/// </para>
/// </summary>
public sealed class DryRunStream : Stream
{
    private const int PageSize = 4096;

    private readonly FileStream _file;
    private readonly long _fileLength;
    private readonly long _limit;
    private readonly Dictionary<long, byte[]> _pages = [];
    private long _length;
    private long _position;

    // Below this the file's own bytes still show through: lowered by a
    // SetLength that cuts the file shorter, so they don't come back if it grows.
    private long _fileVisible;

    public DryRunStream(string path, long limit)
    {
        _file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        _fileLength = _file.Length;
        _length = _fileLength;
        _fileVisible = _fileLength;
        _limit = limit;
    }

    /// <summary>Thrown by a write that would keep more than the limit: a save that moves the audio.</summary>
    /// Not an IOException, so nothing mistakes it for a failure to reach the file.
    public sealed class TooMuchRewrittenException() : Exception("The save rewrites more of the file than a tag.");

    /// <summary>
    /// True when the save left the file as long as it was and never cut it shorter,
    /// so writing <see cref="Changes"/> into it gives exactly what the save made.
    /// </summary>
    public bool KeptSize => _length == _fileLength && _fileVisible == _fileLength;

    /// <summary>The pages the save wrote, in order: their place in the file and their bytes.</summary>
    public IReadOnlyList<(long Offset, byte[] Bytes)> Changes() =>
        [.. _pages.OrderBy(p => p.Key)
            .Select(p => (Offset: p.Key * PageSize, Page: p.Value))
            .Where(p => p.Offset < _length)
            .Select(p => (p.Offset, p.Page.AsSpan(0, (int)Math.Min(PageSize, _length - p.Offset)).ToArray()))];

    public override bool CanRead => true;
    public override bool CanSeek => true;
    public override bool CanWrite => true;
    public override long Length => _length;

    public override long Position
    {
        get => _position;
        set => _position = value >= 0 ? value : throw new ArgumentOutOfRangeException(nameof(value));
    }

    public override long Seek(long offset, SeekOrigin origin) => Position = origin switch
    {
        SeekOrigin.Begin => offset,
        SeekOrigin.Current => _position + offset,
        SeekOrigin.End => _length + offset,
        _ => throw new ArgumentOutOfRangeException(nameof(origin)),
    };

    public override int Read(byte[] buffer, int offset, int count)
    {
        var total = 0;
        while (count > 0 && _position < _length)
        {
            var page = _position / PageSize;
            var within = (int)(_position % PageSize);
            var n = (int)Math.Min(Math.Min(count, PageSize - within), _length - _position);

            if (_pages.TryGetValue(page, out var kept))
                Array.Copy(kept, within, buffer, offset, n);
            else
                ReadFile(_position, buffer, offset, n);

            _position += n;
            offset += n;
            count -= n;
            total += n;
        }

        return total;
    }

    /// <summary>The file's bytes where they still show through, zeros past that.</summary>
    private void ReadFile(long at, byte[] buffer, int offset, int count)
    {
        var fromFile = (int)Math.Clamp(_fileVisible - at, 0, count);
        if (fromFile > 0)
        {
            _file.Position = at;
            _file.ReadExactly(buffer, offset, fromFile);
        }

        Array.Clear(buffer, offset + fromFile, count - fromFile);
    }

    public override void Write(byte[] buffer, int offset, int count)
    {
        while (count > 0)
        {
            var page = _position / PageSize;
            var within = (int)(_position % PageSize);
            var n = Math.Min(count, PageSize - within);

            if (!_pages.TryGetValue(page, out var kept))
            {
                if ((long)(_pages.Count + 1) * PageSize > _limit)
                    throw new TooMuchRewrittenException();

                kept = new byte[PageSize];
                ReadFile(page * PageSize, kept, 0, PageSize);
                _pages[page] = kept;
            }

            Array.Copy(buffer, offset, kept, within, n);
            _position += n;
            offset += n;
            count -= n;
            _length = Math.Max(_length, _position);
        }
    }

    public override void SetLength(long value)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(value);

        if (value < _length)
        {
            // What lies past the new end is gone: if the file grows again, it
            // grows with zeros, as a real file would.
            _fileVisible = Math.Min(_fileVisible, value);
            foreach (var (page, kept) in _pages)
            {
                var start = page * PageSize;
                if (start + PageSize > value)
                    Array.Clear(kept, (int)Math.Max(0, value - start), (int)Math.Min(PageSize, start + PageSize - Math.Max(start, value)));
            }
        }

        _length = value;
    }

    public override void Flush()
    {
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            _file.Dispose();
        base.Dispose(disposing);
    }
}
