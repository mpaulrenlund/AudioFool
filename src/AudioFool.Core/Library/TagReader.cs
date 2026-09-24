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
                ReleaseDate = file is null ? null : NullIfEmpty(ReadDate(file)),
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

    /// <summary>
    /// The tags the cache does not hold - publisher, composer, conductor, genre
    /// and comment - read fresh for the tag dialog. Null when the file cannot be
    /// opened, which the dialog must not mistake for "every field is empty".
    /// </summary>
    public static TagDetails? ReadDetails(string path)
    {
        try
        {
            using var file = TagLib.File.Create(path);
            var tag = file.Tag;
            return new TagDetails(
                Publisher: Clean(tag.Publisher),
                Composer: TagDetails.Join(tag.Composers),
                Conductor: Clean(tag.Conductor),
                Genre: TagDetails.Join(tag.Genres),
                Comment: Clean(tag.Comment))
            {
                Date = ReadDate(file),
            };
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

    /// <summary>
    /// The release date from each format's own date field, where TagLib's
    /// <c>Year</c> would give only its first four digits. "" when no field names
    /// more than a year - the Year box then shows the cached year as before.
    /// </summary>
    private static string? NullIfEmpty(string value) => value.Length == 0 ? null : value;

    /// <summary>MP4's release-date atom, "©day". TagLib's own constant for it is internal.</summary>
    internal static readonly TagLib.ReadOnlyByteVector Mp4DateAtom = new([0xA9, (byte)'d', (byte)'a', (byte)'y']);

    private static string ReadDate(TagLib.File file)
    {
        IEnumerable<string?> raw =
        [
            (file.GetTag(TagLib.TagTypes.Xiph, false) as TagLib.Ogg.XiphComment)?.GetFirstField("DATE"),
            // Only v2.4 stores a date whole. From v2.3, TagLib merges TYER and TDAT
            // reading TDAT month-first where the spec says day-first, so a date
            // from any other tagger would come back with day and month swapped.
            (file.GetTag(TagLib.TagTypes.Id3v2, false) as TagLib.Id3v2.Tag) is { Version: >= 4 } id3
                ? TagLib.Id3v2.TextInformationFrame.Get(id3, "TDRC", false)?.ToString()
                : null,
            (file.GetTag(TagLib.TagTypes.Apple, false) as TagLib.Mpeg4.AppleTag)?.GetText(TagReader.Mp4DateAtom).FirstOrDefault(),
            (file.GetTag(TagLib.TagTypes.Ape, false) as TagLib.Ape.Tag)?.GetItem("Year")?.ToString(),
            (file.GetTag(TagLib.TagTypes.Asf, false) as TagLib.Asf.Tag)?.GetDescriptorString("WM/Year"),
        ];

        return raw.Select(ReleaseDate.FromTag).FirstOrDefault(d => d is not null && ReleaseDate.HasMonth(d)) ?? "";
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
