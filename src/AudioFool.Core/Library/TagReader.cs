using AudioFool.Core.Models;

namespace AudioFool.Core.Library;

/// <summary>
/// Turns a file on disk into a <see cref="Track"/>. Never throws for a bad file -
/// a file we can't parse still shows up in the library using whatever we could
/// glean from its path, so nothing silently disappears from the user's view.
/// </summary>
public static class TagReader
{
    public static Track Read(string path) => Read(path, null);

    /// <summary>
    /// Reads one file. Pass <paramref name="stamp"/> when the caller has already
    /// stat'ed the file, to avoid hitting the filesystem for it twice.
    /// </summary>
    public static Track Read(string path, FileStamp? stamp)
    {
        var kind = AudioFormats.KindFor(path);
        var folderArt = FindFolderArt(path);
        var fileStamp = stamp ?? FileStamp.For(path);

        TagLib.File? file = null;
        try
        {
            file = TagLib.File.Create(path);
        }
        catch (Exception ex) when (ex is TagLib.UnsupportedFormatException
                                     or TagLib.CorruptFileException
                                     or IOException
                                     or UnauthorizedAccessException
                                     or NotSupportedException)
        {
            // Fall through to the header-only / filename-only path below.
        }

        try
        {
            var tag = file?.Tag;
            var props = file?.Properties;

            var isAlac = props is not null && LooksLikeAppleLossless(props);
            if (isAlac && kind == "AAC")
                kind = "ALAC";

            var lossy = AudioFormats.IsLossy(path) && !isAlac;

            var duration = props?.Duration ?? TimeSpan.Zero;
            var sampleRate = Positive(props?.AudioSampleRate);
            var bitrate = Positive(props?.AudioBitrate);
            var bitDepth = lossy ? null : Positive(props?.BitsPerSample);

            // DSD: fill any gaps TagLib left from the DSF header itself.
            if (kind == "DSD" && (sampleRate is null || duration == TimeSpan.Zero))
            {
                var dsf = DsfHeaderReader.TryRead(path);
                if (dsf is { } d)
                {
                    sampleRate ??= d.SampleRate;
                    bitDepth ??= d.BitDepth;
                    bitrate ??= d.BitrateKbps;
                    if (duration == TimeSpan.Zero)
                        duration = d.Duration;
                }
            }

            return new Track
            {
                FilePath = path,
                FileSize = fileStamp.Length,
                ModifiedUtc = fileStamp.ModifiedUtc,
                Title = Clean(tag?.Title),
                Artist = Clean(tag?.FirstPerformer ?? tag?.FirstAlbumArtist),
                AlbumArtist = Clean(tag?.FirstAlbumArtist),
                Album = Clean(tag?.Album),
                TrackNumber = Positive(tag?.Track),
                TrackCount = Positive(tag?.TrackCount),
                DiscNumber = Positive(tag?.Disc),
                DiscCount = Positive(tag?.DiscCount),
                Year = ValidYear(tag?.Year),
                Duration = duration,
                Kind = kind,
                Bitrate = bitrate,
                BitDepth = bitDepth,
                SampleRate = sampleRate,
                FolderArtPath = folderArt,
            };
        }
        finally
        {
            file?.Dispose();
        }
    }

    /// <summary>Embedded cover art bytes, or null when the file carries none.</summary>
    public static byte[]? ReadEmbeddedArt(string path)
    {
        try
        {
            using var file = TagLib.File.Create(path);
            var pictures = file.Tag.Pictures;
            if (pictures.Length == 0)
                return null;

            // Prefer an explicit front cover if the file distinguishes them.
            var picture =
                Array.Find(pictures, p => p.Type == TagLib.PictureType.FrontCover)
                ?? pictures[0];

            var data = picture.Data?.Data;
            return data is { Length: > 0 } ? data : null;
        }
        catch (Exception ex) when (ex is TagLib.UnsupportedFormatException
                                     or TagLib.CorruptFileException
                                     or IOException
                                     or UnauthorizedAccessException
                                     or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>Looks for cover.jpg / folder.jpg / etc. in the file's own directory.</summary>
    public static string? FindFolderArt(string audioFilePath)
    {
        try
        {
            var directory = Path.GetDirectoryName(audioFilePath);
            if (string.IsNullOrEmpty(directory))
                return null;

            foreach (var name in AudioFormats.FolderArtNames)
            {
                var candidate = Path.Combine(directory, name);
                if (File.Exists(candidate))
                    return candidate;
            }

            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    private static bool LooksLikeAppleLossless(TagLib.Properties props)
    {
        try
        {
            foreach (var codec in props.Codecs)
            {
                var description = codec?.Description;
                if (description is null)
                    continue;

                if (description.Contains("Apple Lossless", StringComparison.OrdinalIgnoreCase)
                    || description.Contains("alac", StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }
        catch (Exception ex) when (ex is NotImplementedException or NullReferenceException)
        {
            // Some codec implementations throw on Description; not worth failing the read.
        }

        return false;
    }

    private static string Clean(string? value) => value?.Trim() ?? "";

    private static int? Positive(int? value) => value is > 0 ? value : null;

    private static int? Positive(uint? value) => value is > 0 ? (int)value.Value : null;

    /// <summary>Rejects the 0 and obvious-garbage years that tags are full of.</summary>
    private static int? ValidYear(uint? year) =>
        year is > 1000 and < 2200 ? (int)year.Value : null;
}
