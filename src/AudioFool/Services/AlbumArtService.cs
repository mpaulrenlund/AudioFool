using System.IO;
using System.Windows.Media.Imaging;
using AudioFool.Core.Library;
using AudioFool.Core.Models;

namespace AudioFool.Services;

/// <summary>
/// Loads and caches album art. Embedded pictures win; a cover file sitting in
/// the album folder is the fallback.
/// <para>
/// Everything is decoded off the UI thread at the size it will actually be shown
/// (via <c>DecodePixelWidth</c>) and then frozen, so a wall of 300 album
/// thumbnails costs thumbnails' worth of memory rather than full-size JPEGs'.
/// </para>
/// <para>
/// The cache is bounded by decoded byte count and evicted least-recently-used
/// first. An unbounded cache looks fine on a small library and quietly grows to
/// hundreds of megabytes once you've browsed a few thousand albums.
/// </para>
/// </summary>
public sealed class AlbumArtService
{
    /// <summary>Roughly 170 header covers or 17,000 thumbnails.</summary>
    private const long MaxCachedBytes = 64L * 1024 * 1024;

    private sealed class Entry
    {
        public required Task<BitmapSource?> Art { get; init; }
        public required LinkedListNode<string> Node { get; set; }
        public long Bytes { get; set; }
    }

    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _cache = [];
    private readonly LinkedList<string> _recency = new();   // front = most recent
    private long _cachedBytes;

    /// <summary>Art for an album, decoded to roughly <paramref name="decodeWidth"/> pixels wide.</summary>
    public Task<BitmapSource?> GetAlbumArtAsync(Album album, int decodeWidth) =>
        GetOrLoad($"{album.ArtistName}␟{album.Title}␟{decodeWidth}",
                  () => LoadForAlbum(album, decodeWidth));

    /// <summary>
    /// Art for a single track. Falls back to its album folder's cover so that a
    /// compilation with art on only some files still shows something.
    /// </summary>
    public Task<BitmapSource?> GetTrackArtAsync(Track track, int decodeWidth) =>
        GetOrLoad($"track␟{track.FilePath}␟{decodeWidth}",
                  () => DecodeBytes(TagReader.ReadEmbeddedArt(track.FilePath), decodeWidth)
                        ?? DecodeFile(track.FolderArtPath, decodeWidth));

    /// <summary>
    /// Ceiling for the full-size viewer. Plenty of detail on any current display,
    /// and it stops a 4000px scan costing 64 MB to look at.
    /// </summary>
    private const int MaxViewerWidth = 2000;

    /// <summary>Album art at close to native resolution, for the art viewer.</summary>
    public Task<BitmapSource?> GetFullAlbumArtAsync(Album album) =>
        GetOrLoad($"full␟{album.ArtistName}␟{album.Title}", () =>
        {
            foreach (var track in album.Tracks.Take(5))
            {
                var art = DecodeBytesFull(TagReader.ReadEmbeddedArt(track.FilePath));
                if (art is not null)
                    return art;
            }

            return DecodeFileFull(album.FolderArtPath);
        });

    /// <summary>Track art at close to native resolution, for the art viewer.</summary>
    public Task<BitmapSource?> GetFullTrackArtAsync(Track track) =>
        GetOrLoad($"fulltrack␟{track.FilePath}", () =>
            DecodeBytesFull(TagReader.ReadEmbeddedArt(track.FilePath))
            ?? DecodeFileFull(track.FolderArtPath));

    private static BitmapSource? DecodeBytesFull(byte[]? data)
    {
        if (data is null || data.Length == 0)
            return null;

        try
        {
            using var stream = new MemoryStream(data);
            return DecodeFull(stream);
        }
        catch (Exception ex) when (ex is NotSupportedException or ArgumentException or FileFormatException)
        {
            return null;
        }
    }

    private static BitmapSource? DecodeFileFull(string? path)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
            return null;

        try
        {
            using var stream = File.OpenRead(path);
            return DecodeFull(stream);
        }
        catch (Exception ex) when (ex is IOException
                                     or UnauthorizedAccessException
                                     or NotSupportedException
                                     or ArgumentException
                                     or FileFormatException)
        {
            return null;
        }
    }

    /// <summary>
    /// Decodes at native size, unless the image is bigger than the viewer needs.
    /// The size is read from the header first - <c>DelayCreation</c> plus a cache
    /// option of <c>None</c> means no pixels are decoded just to measure it.
    /// </summary>
    private static BitmapSource DecodeFull(Stream stream)
    {
        var frame = BitmapFrame.Create(stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
        var nativeWidth = frame.PixelWidth;

        stream.Position = 0;
        return Decode(stream, nativeWidth > MaxViewerWidth ? MaxViewerWidth : 0);
    }

    /// <summary>
    /// Returns the cached load, or starts one. The <see cref="Task"/> itself is
    /// cached rather than the result, so two rows asking for the same cover while
    /// it is still decoding share a single decode.
    /// </summary>
    private Task<BitmapSource?> GetOrLoad(string key, Func<BitmapSource?> load)
    {
        lock (_gate)
        {
            if (_cache.TryGetValue(key, out var existing))
            {
                _recency.Remove(existing.Node);
                _recency.AddFirst(existing.Node);
                return existing.Art;
            }

            var task = Task.Run(load);
            var entry = new Entry { Art = task, Node = _recency.AddFirst(key) };
            _cache[key] = entry;

            // Size is only known once the decode finishes; charge it then and
            // trim if we've gone over budget.
            _ = task.ContinueWith(
                t => Charge(key, EstimateBytes(t.IsCompletedSuccessfully ? t.Result : null)),
                TaskScheduler.Default);

            return task;
        }
    }

    private void Charge(string key, long bytes)
    {
        lock (_gate)
        {
            if (!_cache.TryGetValue(key, out var entry))
                return;   // already evicted

            _cachedBytes += bytes - entry.Bytes;
            entry.Bytes = bytes;

            while (_cachedBytes > MaxCachedBytes && _recency.Last is { } oldest)
            {
                if (_cache.Remove(oldest.Value, out var evicted))
                    _cachedBytes -= evicted.Bytes;

                _recency.RemoveLast();
            }
        }
    }

    private static long EstimateBytes(BitmapSource? image) =>
        image is null ? 0 : (long)image.PixelWidth * image.PixelHeight * 4;

    private static BitmapSource? LoadForAlbum(Album album, int decodeWidth)
    {
        // Try the first few tracks: plenty of albums only tag art on track one,
        // and plenty of others miss exactly that one.
        foreach (var track in album.Tracks.Take(5))
        {
            var art = DecodeBytes(TagReader.ReadEmbeddedArt(track.FilePath), decodeWidth);
            if (art is not null)
                return art;
        }

        return DecodeFile(album.FolderArtPath, decodeWidth);
    }

    private static BitmapSource? DecodeBytes(byte[]? data, int decodeWidth)
    {
        if (data is null || data.Length == 0)
            return null;

        try
        {
            using var stream = new MemoryStream(data);
            return Decode(stream, decodeWidth);
        }
        catch (Exception ex) when (ex is NotSupportedException or ArgumentException or FileFormatException)
        {
            return null;   // Malformed embedded image; treat as "no art".
        }
    }

    private static BitmapSource? DecodeFile(string? path, int decodeWidth)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
            return null;

        try
        {
            using var stream = File.OpenRead(path);
            return Decode(stream, decodeWidth);
        }
        catch (Exception ex) when (ex is IOException
                                     or UnauthorizedAccessException
                                     or NotSupportedException
                                     or ArgumentException
                                     or FileFormatException)
        {
            return null;
        }
    }

    private static BitmapSource Decode(Stream stream, int decodeWidth)
    {
        var bitmap = new BitmapImage();
        bitmap.BeginInit();

        // OnLoad so we can dispose the stream immediately after EndInit.
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;

        if (decodeWidth > 0)
            bitmap.DecodePixelWidth = decodeWidth;

        bitmap.StreamSource = stream;
        bitmap.EndInit();

        // Frozen so it can be handed to the UI thread from this worker thread.
        bitmap.Freeze();
        return bitmap;
    }
}
