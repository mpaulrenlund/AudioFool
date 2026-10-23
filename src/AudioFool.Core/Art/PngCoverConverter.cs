using AudioFool.Core.Library;

namespace AudioFool.Core.Art;

public enum PngFileOutcome
{
    /// <summary>Re-encoded as a JPEG of the same name; the PNG went to the Recycle Bin.</summary>
    Converted,

    /// <summary>A JPEG of the same name with at least as many pixels was already there; only the PNG went.</summary>
    Duplicate,

    /// <summary>The JPEG was written, but the PNG couldn't go to the Recycle Bin (no bin on that drive), so it was kept.</summary>
    Kept,

    /// <summary>Nothing changed.</summary>
    Failed,
}

/// <param name="Saved">Bytes the folder lost: the PNG, less any JPEG written (negative when the JPEG is bigger).</param>
public sealed record PngFileResult(string Path, PngFileOutcome Outcome, long Saved, string? Error);

/// <summary>
/// Turns PNG covers into JPEGs: the picture embedded in a song (through
/// <see cref="CoverCleaner.ReplaceCover"/>'s checked rewrite), and .png files in a
/// folder. The encoder is passed in (the app's <c>CoverJpeg</c>), so this stays
/// free of any imaging library.
/// </summary>
public static class PngCoverConverter
{
    /// <summary>
    /// A cut-off PNG is left as it is (session 70): decoded, its missing part is
    /// black, and the JPEG made from it would keep that black for good.
    /// </summary>
    public const string CutOffError = "the PNG is cut off";

    /// <summary>
    /// The song's cover, converted, as its only picture. A song whose best cover
    /// is already a JPEG and carries a PNG only as an extra has the extra removed
    /// instead (<see cref="CoverCleaner.Clean"/>). NothingToDo when it has no PNG.
    /// </summary>
    public static CoverCleanResult ConvertEmbedded(string path, Func<byte[], byte[]?> toJpeg, IFileHolder? holder = null)
    {
        if (!CoverCleaner.CanClean(path))
            return new CoverCleanResult(path, CoverCleanOutcome.Failed, 0, false, "only FLAC and MP3 files can be rewritten");

        TagLib.IPicture best;
        try
        {
            using var file = TagLib.File.Create(path);
            var pictures = file.Tag.Pictures;
            if (!pictures.Any(IsPng) || TagReader.BestCover(pictures) is not { } found)
                return new CoverCleanResult(path, CoverCleanOutcome.NothingToDo, 0, false, null);
            best = found;
        }
        catch (Exception ex) when (ex is TagLib.UnsupportedFormatException or TagLib.CorruptFileException
                                     or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return new CoverCleanResult(path, CoverCleanOutcome.Failed, 0, false, ex.Message);
        }

        if (!IsPng(best))
            return CoverCleaner.Clean(path, holder);

        var png = best.Data.Data;
        if (CutOffImage.Is(png))
            return new CoverCleanResult(path, CoverCleanOutcome.Failed, 0, false, CutOffError);
        if (Encode(png, toJpeg) is not { } jpeg)
            return new CoverCleanResult(path, CoverCleanOutcome.Failed, 0, false, "the PNG couldn't be converted");

        return CoverCleaner.ReplaceCover(path, best, jpeg, "image/jpeg", holder);
    }

    /// <summary>
    /// Every .png in <paramref name="folder"/>, as a JPEG of the same name
    /// (cover.png → cover.jpg). Where a .jpg or .jpeg of that name is already there,
    /// whichever has more pixels is kept as the JPEG. Replaced files go through
    /// <paramref name="recycle"/> (the Recycle Bin), which returns false when it
    /// can't take one; the PNG is then kept, and an old JPEG is never overwritten.
    /// </summary>
    public static List<PngFileResult> ConvertFolder(string folder, Func<byte[], byte[]?> toJpeg, Func<string, bool> recycle)
    {
        var results = new List<PngFileResult>();
        List<string> pngs;
        try
        {
            pngs = Directory.EnumerateFiles(folder, "*.png")
                .Where(p => Path.GetExtension(p).Equals(".png", StringComparison.OrdinalIgnoreCase))
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return results;
        }

        foreach (var png in pngs)
            results.Add(ConvertFile(png, toJpeg, recycle));
        return results;
    }

    private static PngFileResult ConvertFile(string png, Func<byte[], byte[]?> toJpeg, Func<string, bool> recycle)
    {
        var temp = Path.Combine(Path.GetDirectoryName(png)!, CoverCleaner.TempPrefix + Guid.NewGuid().ToString("N") + ".part");
        try
        {
            var bytes = File.ReadAllBytes(png);
            if (ImageInfo.Read(bytes) is not { Format: ImageFormat.Png } pngInfo)
                return new PngFileResult(png, PngFileOutcome.Failed, 0, "not a readable PNG");
            if (CutOffImage.Is(bytes))
                return new PngFileResult(png, PngFileOutcome.Failed, 0, CutOffError);

            var existing = new[] { ".jpg", ".jpeg" }
                .Select(e => Path.ChangeExtension(png, e))
                .FirstOrDefault(File.Exists);

            // A cut-off JPEG beside it is no duplicate: it gives way, as a smaller one does.
            var existingBytes = existing is null ? null : File.ReadAllBytes(existing);
            if (existingBytes is not null && ImageInfo.Read(existingBytes) is { Format: ImageFormat.Jpeg } jpegInfo
                && Pixels(jpegInfo) >= Pixels(pngInfo) && !CutOffImage.Is(existingBytes))
            {
                return recycle(png)
                    ? new PngFileResult(png, PngFileOutcome.Duplicate, bytes.Length, null)
                    : new PngFileResult(png, PngFileOutcome.Failed, 0, "it couldn't go to the Recycle Bin");
            }

            if (Encode(bytes, toJpeg) is not { } jpeg)
                return new PngFileResult(png, PngFileOutcome.Failed, 0, "the PNG couldn't be converted");

            // A smaller JPEG of the same name gives way, through the bin; if it can't
            // go, nothing is written.
            var target = existing ?? Path.ChangeExtension(png, ".jpg");
            var oldBytes = existing is null ? 0 : new FileInfo(existing).Length;
            File.WriteAllBytes(temp, jpeg);
            if (existing is not null && !recycle(existing))
                return new PngFileResult(png, PngFileOutcome.Failed, 0, $"{Path.GetFileName(existing)} couldn't go to the Recycle Bin");

            File.Move(temp, target);
            var saved = bytes.Length + oldBytes - jpeg.Length;
            return recycle(png)
                ? new PngFileResult(png, PngFileOutcome.Converted, saved, null)
                : new PngFileResult(png, PngFileOutcome.Kept, oldBytes - jpeg.Length, "it couldn't go to the Recycle Bin");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new PngFileResult(png, PngFileOutcome.Failed, 0, ex.Message);
        }
        finally
        {
            try { File.Delete(temp); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>The JPEG, checked to be one, of the same pixel size; null otherwise.</summary>
    private static byte[]? Encode(byte[] png, Func<byte[], byte[]?> toJpeg)
    {
        var jpeg = toJpeg(png);
        return jpeg is not null
               && ImageInfo.Read(jpeg) is { Format: ImageFormat.Jpeg } after
               && ImageInfo.Read(png) is { } before
               && (after.Width, after.Height) == (before.Width, before.Height)
            ? jpeg
            : null;
    }

    private static bool IsPng(TagLib.IPicture picture) =>
        picture.Data?.Data is { } data && ImageInfo.Read(data)?.Format == ImageFormat.Png;

    private static long Pixels(ImageInfo info) => (long)info.Width * info.Height;
}
