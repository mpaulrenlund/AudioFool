using AudioFool.Core.Library;

namespace AudioFool.Core.Art;

public enum ExtractOutcome
{
    /// <summary>cover.jpg was written.</summary>
    Saved,

    /// <summary>The folder already has a cover.jpg with at least as many pixels.</summary>
    KeptExisting,

    /// <summary>None of the folder's tracks carries a picture.</summary>
    NoArt,

    /// <summary>The tracks carry pictures, but none is a JPEG or PNG (a GIF, say).</summary>
    Unsupported,

    /// <summary>Writing, or converting a PNG, failed.</summary>
    Failed,
}

/// <summary>What happened in one folder. <see cref="Info"/> is the picture that was chosen.</summary>
public sealed record FolderExtract(
    string Directory,
    ExtractOutcome Outcome,
    string? CoverPath,
    ImageInfo? Info,
    long Bytes,
    bool Converted,
    ImageInfo? ExistingInfo,
    string? Error);

/// <summary>
/// Saves the art embedded in an album's files as <c>cover.jpg</c> beside them.
/// <para>
/// Each folder is handled on its own, so a multi-disc set kept as "Disc 1/" and
/// "Disc 2/" gets a cover in each. Within a folder the picture with the most pixels
/// wins (the larger file breaks a tie), which is the highest quality on offer.
/// A JPEG is written as the exact bytes found in the tag: nothing is decoded or
/// re-encoded, so nothing is compressed. A PNG is the one exception, since the
/// file is to be a JPEG: it goes through <c>pngToJpeg</c>, which the caller
/// supplies (Core has no image codec) and which should use the best quality it has.
/// </para>
/// <para>
/// An existing cover.jpg is replaced only by a picture with more pixels, so a
/// better folder cover is never swapped for a smaller one. Other cover files
/// (folder.jpg, cover.png) are left alone; cover.jpg ranks first in
/// <see cref="AudioFormats.FolderArtNames"/>, so it is the one the app then uses.
/// </para>
/// </summary>
public static class EmbeddedArtExtractor
{
    public const string FileName = "cover.jpg";

    public static IReadOnlyList<FolderExtract> ExtractToFolders(
        IEnumerable<string> trackPaths, Func<byte[], byte[]?> pngToJpeg)
    {
        var results = new List<FolderExtract>();
        var byDirectory = trackPaths
            .Where(p => Path.GetDirectoryName(p) is { Length: > 0 })
            .GroupBy(p => Path.GetDirectoryName(p)!, StringComparer.OrdinalIgnoreCase);

        foreach (var folder in byDirectory)
            results.Add(ExtractOne(folder.Key, folder, pngToJpeg));

        return results;
    }

    private static FolderExtract ExtractOne(string directory, IEnumerable<string> tracks, Func<byte[], byte[]?> pngToJpeg)
    {
        byte[]? best = null;
        ImageInfo? bestInfo = null;
        var sawUnsupported = false;
        foreach (var track in tracks)
        {
            if (TagReader.ReadEmbeddedArt(track) is not { } bytes)
                continue;

            if (ImageInfo.Read(bytes) is not { } info)
            {
                sawUnsupported = true;
                continue;
            }

            if (bestInfo is null || Better(info, bytes.Length, bestInfo, best!.Length))
                (best, bestInfo) = (bytes, info);
        }

        var target = Path.Combine(directory, FileName);
        if (best is null || bestInfo is null)
        {
            return new FolderExtract(directory, sawUnsupported ? ExtractOutcome.Unsupported : ExtractOutcome.NoArt,
                                     null, null, 0, false, null, null);
        }

        try
        {
            ImageInfo? existing = File.Exists(target) ? ImageInfo.Read(File.ReadAllBytes(target)) : null;
            if (existing is not null && Pixels(existing) >= Pixels(bestInfo))
                return new FolderExtract(directory, ExtractOutcome.KeptExisting, target, bestInfo, best.Length, false, existing, null);

            var converted = bestInfo.Format == ImageFormat.Png;
            var jpeg = converted ? pngToJpeg(best) : best;
            if (jpeg is null || jpeg.Length == 0)
                return Failed(directory, bestInfo, existing, "couldn't convert the PNG to JPEG");

            File.WriteAllBytes(target, jpeg);
            return new FolderExtract(directory, ExtractOutcome.Saved, target, bestInfo, jpeg.Length, converted, existing, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return Failed(directory, bestInfo, null, ex.Message);
        }
    }

    private static FolderExtract Failed(string directory, ImageInfo info, ImageInfo? existing, string error) =>
        new(directory, ExtractOutcome.Failed, null, info, 0, false, existing, error);

    private static long Pixels(ImageInfo info) => (long)info.Width * info.Height;

    private static bool Better(ImageInfo info, int bytes, ImageInfo bestInfo, int bestBytes) =>
        Pixels(info) > Pixels(bestInfo) || (Pixels(info) == Pixels(bestInfo) && bytes > bestBytes);

    /// <summary>One or two sentences for the dialog's message line.</summary>
    public static string Describe(IReadOnlyList<FolderExtract> results)
    {
        if (results.Count == 0)
            return "No tracks to read.";

        var saved = results.Where(r => r.Outcome == ExtractOutcome.Saved).ToList();
        var kept = results.Where(r => r.Outcome == ExtractOutcome.KeptExisting).ToList();
        var none = results.Count(r => r.Outcome == ExtractOutcome.NoArt);
        var unsupported = results.Count(r => r.Outcome == ExtractOutcome.Unsupported);
        var failed = results.Where(r => r.Outcome == ExtractOutcome.Failed).ToList();

        if (saved.Count == 0 && kept.Count == 0 && failed.Count == 0)
        {
            if (unsupported > 0)
                return "The embedded art isn't a JPEG or PNG, so it can't be saved as cover.jpg.";

            return results.Count == 1
                ? "No embedded art found in these tracks."
                : "No embedded art found in any of the album's folders.";
        }

        var parts = new List<string>();
        if (saved.Count > 0)
        {
            var biggest = saved.OrderByDescending(r => Pixels(r.Info!)).First();
            var where = saved.Count == 1 ? "in the folder" : $"in {saved.Count} of {results.Count} folders";
            parts.Add($"Saved {FileName} {where} ({biggest.Info!.SizeText}, {FormatBytes(biggest.Bytes)}).");
            var converted = saved.Count(r => r.Converted);
            if (converted > 0)
                parts.Add(converted == 1
                    ? "The embedded art was a PNG, so it was converted to JPEG at maximum quality."
                    : $"{converted} were PNG, converted to JPEG at maximum quality.");
        }

        if (kept.Count > 0)
        {
            var existing = kept[0].ExistingInfo!;
            parts.Add(kept.Count == 1 && results.Count == 1
                ? $"Kept the existing {FileName} ({existing.SizeText}): the embedded art ({kept[0].Info!.SizeText}) is no larger."
                : $"Kept the existing {FileName} in {kept.Count} folder(s): the embedded art is no larger.");
        }

        if (none > 0 && results.Count > 1)
            parts.Add($"No embedded art in {none} of {results.Count} folders.");
        if (unsupported > 0)
            parts.Add($"{unsupported} folder(s) have art that isn't a JPEG or PNG.");

        foreach (var f in failed)
            parts.Add($"Couldn't write {FileName} in {Path.GetFileName(f.Directory)}: {f.Error}.");

        return string.Join("  ", parts);
    }

    private static string FormatBytes(long bytes) =>
        // Rounded up to the next KB, as Explorer shows a size.
        bytes >= 1024 * 1024 ? $"{bytes / 1048576.0:0.#} MB" : $"{Math.Max(1, (bytes + 1023) / 1024)} KB";
}
