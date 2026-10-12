using System.IO;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AudioFool.Core.Art;
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
/// <para>
/// The Albums list's thumbnails and the album header also keep a small copy on
/// the local drive (<see cref="ArtDiskCache"/>, session 47), so after a restart
/// they come from there rather than from the music drive.
/// </para>
/// </summary>
public sealed class AlbumArtService
{
    /// <summary>The Albums list's thumbnail, decoded wider than its 56 px tile for sharpness.</summary>
    public const int ListThumbnailWidth = 96;

    /// <summary>The album header's cover.</summary>
    public const int HeaderWidth = 320;

    private readonly ArtDiskCache? _disk;

    /// <param name="disk">Where the small copies live; null keeps none (ThemeLab).</param>
    public AlbumArtService(ArtDiskCache? disk = null) => _disk = disk;

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

    /// <summary>
    /// Art for an album, decoded to roughly <paramref name="decodeWidth"/> pixels wide.
    /// At <see cref="ListThumbnailWidth"/> and <see cref="HeaderWidth"/> a saved
    /// copy is used, or made.
    /// </summary>
    public Task<BitmapSource?> GetAlbumArtAsync(Album album, int decodeWidth) =>
        GetOrLoad($"{album.ArtistName}␟{album.Title}␟{decodeWidth}",
                  () => KeepsCopy(decodeWidth) ? LoadWithCopy(album, decodeWidth) : LoadForAlbum(album, decodeWidth));

    private bool KeepsCopy(int width) => _disk is not null && width is ListThumbnailWidth or HeaderWidth;

    /// <summary>
    /// Makes the saved list thumbnail for an album that has none yet, for the
    /// background prefill. Leaves the in-memory cache alone, so filling in a whole
    /// library doesn't push out what is on screen. True when a copy was made.
    /// </summary>
    public bool EnsureSavedThumbnail(Album album)
    {
        if (_disk is null || _disk.Contains(ArtDiskCache.KeyFor(album, ListThumbnailWidth)))
            return false;

        return LoadFromSource(album, ListThumbnailWidth, save: true).Saved;
    }

    /// <summary>Deletes saved copies untouched for 90 days. Returns how many went.</summary>
    public int PruneSavedCopies() => _disk?.Prune(DateTime.UtcNow) ?? 0;

    private BitmapSource? LoadWithCopy(Album album, int width)
    {
        if (_disk!.TryRead(ArtDiskCache.KeyFor(album, width), out var saved))
        {
            if (saved is null)
                return null;   // looked before: no cover

            if (DecodeBytes(saved, 0) is { } copy)
                return copy;
        }

        return LoadFromSource(album, width, save: true).Art;
    }

    /// <summary>
    /// Reads the cover from the tracks or the folder, as <see cref="LoadForAlbum"/>
    /// does, and with <paramref name="save"/> keeps a copy. "No cover" is kept only
    /// when every file looked in was read: an unplugged music drive or a file
    /// another program has open must not be remembered as an album without art.
    /// </summary>
    private (BitmapSource? Art, bool Saved) LoadFromSource(Album album, int width, bool save)
    {
        var certain = album.Tracks.Count > 0;
        BitmapSource? art = null;
        foreach (var track in album.Tracks.Take(5))
        {
            if (!TagReader.TryReadEmbeddedArt(track.FilePath, out var embedded))
                certain = false;
            if ((art = DecodeBytes(embedded, width)) is not null)
                break;
        }

        if (art is null && album.FolderArtPath is { } folderArt)
        {
            art = DecodeFile(folderArt, width);
            certain &= art is not null;
        }

        if (!save || _disk is null)
            return (art, false);

        var key = ArtDiskCache.KeyFor(album, width);
        if (art is not null)
        {
            if (Encode(art) is not { } bytes)
                return (art, false);
            _disk.Write(key, bytes);
            return (art, true);
        }

        if (!certain)
            return (null, false);

        _disk.Write(key, null);
        return (null, true);
    }

    /// <summary>JPEG at quality 90; PNG when the picture has transparency, which JPEG would lose.</summary>
    private static byte[]? Encode(BitmapSource art)
    {
        try
        {
            var format = art.Format;
            var hasAlpha = format == PixelFormats.Bgra32 || format == PixelFormats.Pbgra32
                           || format == PixelFormats.Rgba64 || format == PixelFormats.Prgba64
                           || format == PixelFormats.Rgba128Float || format == PixelFormats.Prgba128Float;
            BitmapEncoder encoder = hasAlpha
                ? new PngBitmapEncoder()
                : new JpegBitmapEncoder { QualityLevel = 90 };
            encoder.Frames.Add(BitmapFrame.Create(art));
            using var stream = new MemoryStream();
            encoder.Save(stream);
            return stream.ToArray();
        }
        catch (Exception ex) when (ex is NotSupportedException or InvalidOperationException or ArgumentException)
        {
            return null;
        }
    }

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

    /// <summary>
    /// Drops every cached image belonging to this album - its own thumbnail/header/
    /// full-viewer entries and every one of its tracks' now-playing/full-viewer
    /// entries - so the next request after a tag/art save decodes what's actually on
    /// disk instead of serving a frozen bitmap from before the edit.
    /// <para>
    /// Pass the pre-edit <paramref name="album"/>: its artist/title/track paths are
    /// what's actually cached right now. If the edit also renamed the album, the new
    /// identity was never cached, so it decodes fresh automatically; the old-identity
    /// entries this removes would otherwise just sit there as harmless LRU clutter.
    /// </para>
    /// </summary>
    public void InvalidateAlbum(Album album)
    {
        lock (_gate)
        {
            var albumPrefix = $"{album.ArtistName}␟{album.Title}␟";
            var albumFullKey = $"full␟{album.ArtistName}␟{album.Title}";
            var trackPaths = album.Tracks.Select(t => t.FilePath).ToHashSet();

            bool ShouldRemove(string key)
            {
                if (key.StartsWith(albumPrefix, StringComparison.Ordinal) || key == albumFullKey)
                    return true;

                foreach (var path in trackPaths)
                {
                    if (key == $"track␟{path}" || key.StartsWith($"track␟{path}␟", StringComparison.Ordinal)
                        || key == $"fulltrack␟{path}")
                        return true;
                }

                return false;
            }

            foreach (var key in _cache.Keys.Where(ShouldRemove).ToList())
            {
                if (!_cache.Remove(key, out var entry))
                    continue;

                _cachedBytes -= entry.Bytes;
                _recency.Remove(entry.Node);
            }
        }

        // The edit gives the album new saved-copy names anyway (its files'
        // write times change); this just doesn't leave the old ones for 90 days.
        _disk?.Remove(ArtDiskCache.KeyFor(album, ListThumbnailWidth));
        _disk?.Remove(ArtDiskCache.KeyFor(album, HeaderWidth));
    }

    /// <summary>
    /// The source file's format and native size for each full-size bitmap handed
    /// out. The viewer needs both, and neither survives the decode: the bitmap is
    /// capped at <see cref="MaxViewerWidth"/> and has no format of its own. Weak,
    /// so an entry goes when the cache evicts its bitmap.
    /// </summary>
    private static readonly ConditionalWeakTable<BitmapSource, ImageInfo> FullArtInfo = new();

    /// <summary>What a bitmap from <see cref="GetFullAlbumArtAsync"/> or <see cref="GetFullTrackArtAsync"/> was decoded from.</summary>
    public static ImageInfo? InfoFor(BitmapSource art) =>
        FullArtInfo.TryGetValue(art, out var info) ? info : null;

    private static BitmapSource? DecodeBytesFull(byte[]? data)
    {
        if (data is null || data.Length == 0)
            return null;

        try
        {
            using var stream = new MemoryStream(data);
            var art = DecodeFull(stream);
            if (ImageInfo.Read(data) is { } info)
                FullArtInfo.AddOrUpdate(art, info);
            return art;
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
            // Read whole, so the header can be measured as well as decoded.
            return DecodeBytesFull(File.ReadAllBytes(path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
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
