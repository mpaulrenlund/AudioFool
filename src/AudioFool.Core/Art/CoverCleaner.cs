using System.Security.Cryptography;
using AudioFool.Core.Library;

namespace AudioFool.Core.Art;

/// <summary>What the library check found in one file's pictures.</summary>
/// <param name="Pictures">How many pictures the file carries.</param>
/// <param name="Freeable">
/// What keeping only the best one would free: the other pictures, and the tag's
/// padding beyond <see cref="TagPadding.Reserve"/>. 0 for a file with one picture or none.
/// </param>
public sealed record CoverFinding(int Pictures, long Freeable)
{
    [System.Text.Json.Serialization.JsonIgnore]
    public bool HasExtras => Pictures > 1;
}

public enum CoverCleanOutcome
{
    /// <summary>The file now carries only its best cover.</summary>
    Cleaned,

    /// <summary>The file has one picture or none; nothing was written.</summary>
    NothingToDo,

    /// <summary>Something failed or didn't check out; the original is untouched.</summary>
    Failed,
}

/// <param name="Shrunk">False when the extra covers went but the padding couldn't be cut (a layout <see cref="TagPadding"/> leaves alone).</param>
public sealed record CoverCleanResult(string Path, CoverCleanOutcome Outcome, long BytesFreed, bool Shrunk, string? Error);

/// <summary>
/// Keeps only the best embedded cover (<see cref="TagReader.BestCover"/>) and cuts
/// the room the others leave behind, for FLAC and MP3.
/// <para>
/// The original file is never edited. A copy is made beside it, the extra
/// pictures are removed from the copy and its padding cut, and the result is then
/// checked against the original: the audio must match byte for byte, every tag
/// must read back the same, and the one picture left must be the best one. Only
/// then does it replace the original, through <see cref="File.Replace(string, string, string?)"/>,
/// which keeps the original's creation date (Recently Added sorts by it; checked
/// on the library's NTFS drive). Any failure leaves the original as it was.
/// </para>
/// </summary>
public static class CoverCleaner
{
    /// <summary>The working copies beside the original start with this; not an audio extension, so a scan ignores a leftover.</summary>
    public const string TempPrefix = ".audiofool-cover-";

    /// <summary>The file's pictures, for the library check. Null when it can't be read.</summary>
    public static CoverFinding? Survey(string path)
    {
        try
        {
            using var file = TagLib.File.Create(path);
            var pictures = file.Tag.Pictures;
            if (pictures.Length <= 1 || TagReader.BestCover(pictures) is not { } best)
                return new CoverFinding(pictures.Length, 0);

            // The extra covers become padding, which is then cut to the reserve.
            var extra = pictures.Where(p => !ReferenceEquals(p, best)).Sum(p => (long)(p.Data?.Count ?? 0));
            var padding = TagPadding.Read(path)?.Padding ?? 0;
            return new CoverFinding(pictures.Length, Math.Max(0, extra + padding - TagPadding.Reserve));
        }
        catch (Exception ex) when (ex is TagLib.UnsupportedFormatException or TagLib.CorruptFileException
                                     or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return null;
        }
    }

    /// <param name="holder">Playback, which may hold the file open; it lets go for the swap and picks up where it was.</param>
    public static CoverCleanResult Clean(string path, IFileHolder? holder = null)
    {
        TagLib.IPicture best;
        try
        {
            using var file = TagLib.File.Create(path);
            var pictures = file.Tag.Pictures;
            if (pictures.Length <= 1 || TagReader.BestCover(pictures) is not { } found)
                return new CoverCleanResult(path, CoverCleanOutcome.NothingToDo, 0, false, null);
            best = found;
        }
        catch (Exception ex) when (ex is TagLib.UnsupportedFormatException or TagLib.CorruptFileException
                                     or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return Failed(path, Describe(path, ex));
        }

        // The whole file is replaced, so a stream reading it must always let go.
        return holder is null
            ? CleanCore(path, best)
            : holder.Saving(path, needsRelease: () => true, save: () => CleanCore(path, best));
    }

    private static CoverCleanResult CleanCore(string path, TagLib.IPicture best)
    {
        var directory = Path.GetDirectoryName(path)!;
        var stem = Path.Combine(directory, TempPrefix + Guid.NewGuid().ToString("N"));
        var copy = stem + ".part";
        var shrunk = stem + ".shrunk.part";
        try
        {
            var before = new FileInfo(path).Length;
            File.Copy(path, copy);

            using (var file = OpenAs(copy, path))
            {
                file.Tag.Pictures =
                [
                    new TagLib.Picture(new TagLib.ByteVector(best.Data.Data))
                    {
                        Type = best.Type,
                        MimeType = best.MimeType,
                        Description = best.Description,
                    },
                ];
                file.Save();
            }

            var didShrink = TagPadding.TryShrink(copy, shrunk);
            var result = didShrink ? shrunk : copy;

            if (Verify(path, result, best.Data.Data) is { } problem)
                return Failed(path, problem);

            File.Replace(result, path, null);
            return new CoverCleanResult(path, CoverCleanOutcome.Cleaned, before - new FileInfo(path).Length, didShrink, null);
        }
        catch (Exception ex) when (ex is TagLib.UnsupportedFormatException or TagLib.CorruptFileException
                                     or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return Failed(path, Describe(path, ex));
        }
        finally
        {
            TryDelete(copy);
            TryDelete(shrunk);
        }
    }

    /// <summary>Null when <paramref name="result"/> is the original less its extra covers; otherwise why not.</summary>
    public static string? Verify(string original, string result, byte[] bestCover)
    {
        if (TagPadding.Read(original) is not { } before || TagPadding.Read(result) is not { } after)
            return "couldn't find where the audio starts, so it couldn't be checked";

        if (!AudioHash(original, before.AudioStart).AsSpan().SequenceEqual(AudioHash(result, after.AudioStart)))
            return "the audio didn't come out identical";

        using var a = TagLib.File.Create(original);
        using var b = OpenAs(result, original);
        if (b.Tag.Pictures is not [var kept] || !kept.Data.Data.AsSpan().SequenceEqual(bestCover))
            return "the cover left wasn't the best one";
        if (!TagsMatch(a, b))
            return "the tags didn't read back the same";

        return null;
    }

    /// <summary>Every text field and frame other than the pictures, in each tag the file has.</summary>
    private static bool TagsMatch(TagLib.File a, TagLib.File b)
    {
        if (a.TagTypes != b.TagTypes)
            return false;

        if (a.GetTag(TagLib.TagTypes.Xiph, false) is TagLib.Ogg.XiphComment xa)
        {
            if (b.GetTag(TagLib.TagTypes.Xiph, false) is not TagLib.Ogg.XiphComment xb)
                return false;
            var names = xa.ToList();
            if (!names.Order().SequenceEqual(xb.ToList().Order()))
                return false;
            if (names.Any(n => !xa.GetField(n).SequenceEqual(xb.GetField(n))))
                return false;
        }

        if (a.GetTag(TagLib.TagTypes.Id3v2, false) is TagLib.Id3v2.Tag ia)
        {
            if (b.GetTag(TagLib.TagTypes.Id3v2, false) is not TagLib.Id3v2.Tag ib || ia.Version != ib.Version)
                return false;
            var fa = ia.GetFrames().Where(f => f is not TagLib.Id3v2.AttachmentFrame).Select(f => f.Render(ia.Version)).ToList();
            var fb = ib.GetFrames().Where(f => f is not TagLib.Id3v2.AttachmentFrame).Select(f => f.Render(ib.Version)).ToList();
            if (fa.Count != fb.Count || fa.Zip(fb).Any(p => !p.First.Data.AsSpan().SequenceEqual(p.Second.Data)))
                return false;
        }

        // Left out of the audio comparison, since TagLib renders it afresh on every save.
        if (a.GetTag(TagLib.TagTypes.Id3v1, false) is TagLib.Id3v1.Tag va
            && (b.GetTag(TagLib.TagTypes.Id3v1, false) is not TagLib.Id3v1.Tag vb
                || !va.Render().Data.AsSpan().SequenceEqual(vb.Render().Data)))
            return false;

        var ta = a.Tag;
        var tb = b.Tag;
        return ta.Title == tb.Title && ta.Album == tb.Album && ta.Year == tb.Year
               && ta.Track == tb.Track && ta.TrackCount == tb.TrackCount && ta.Disc == tb.Disc
               && ta.Performers.SequenceEqual(tb.Performers) && ta.AlbumArtists.SequenceEqual(tb.AlbumArtists)
               && a.Properties.Duration == b.Properties.Duration;
    }

    /// <summary>
    /// Opens a working copy as the format of <paramref name="formatOf"/>. The copies
    /// end in ".part", not an audio extension, so a scan never takes one for a
    /// song; TagLib, which goes by extension, is told the format instead.
    /// </summary>
    private static TagLib.File OpenAs(string path, string formatOf) =>
        TagLib.File.Create(path, "taglib/" + Path.GetExtension(formatOf).TrimStart('.').ToLowerInvariant(),
                           TagLib.ReadStyle.Average);

    /// <summary>From where the tags end to the end of the file, less an ID3v1 tag there (compared as a tag).</summary>
    private static byte[] AudioHash(string path, long start)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var end = stream.Length;
        if (end - start >= 128)
        {
            var tag = new byte[3];
            stream.Position = end - 128;
            stream.ReadExactly(tag);
            if (tag is [(byte)'T', (byte)'A', (byte)'G'])
                end -= 128;
        }

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[1 << 20];
        stream.Position = start;
        for (var left = end - start; left > 0;)
        {
            var read = stream.Read(buffer, 0, (int)Math.Min(buffer.Length, left));
            if (read == 0)
                throw new EndOfStreamException();
            hash.AppendData(buffer, 0, read);
            left -= read;
        }

        return hash.GetHashAndReset();
    }

    private static CoverCleanResult Failed(string path, string error) =>
        new(path, CoverCleanOutcome.Failed, 0, false, error);

    private static string Describe(string path, Exception ex) => ex switch
    {
        _ when !File.Exists(path) => "the file no longer exists",
        UnauthorizedAccessException => "permission denied - check the file isn't read-only",
        TagLib.UnsupportedFormatException => "unsupported file format",
        TagLib.CorruptFileException => "the file appears to be corrupt",
        IOException => ex.Message,
        _ => ex.Message,
    };

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
