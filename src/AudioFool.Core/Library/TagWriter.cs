using AudioFool.Core.Models;

namespace AudioFool.Core.Library;

/// <summary>
/// Result of writing to one audio file's own tags/art. Never thrown across this
/// boundary - a failed write becomes a message, never an exception, so a caller
/// looping over an album's tracks can always continue past one bad file.
/// </summary>
public sealed record TagWriteResult
{
    public required bool Success { get; init; }
    public string? ErrorMessage { get; init; }
    public Track? UpdatedTrack { get; init; }

    /// <summary>
    /// A saved file whose new cover couldn't be made its only picture, and why: the
    /// tags and cover were written, but an older copy or the room it took is still there.
    /// </summary>
    public string? CoverWarning { get; init; }

    public static TagWriteResult Ok(Track updated) => new() { Success = true, UpdatedTrack = updated };
    public static TagWriteResult Fail(string message) => new() { Success = false, ErrorMessage = message };
}

/// <summary>Result of writing (or overwriting) a folder-level cover file.</summary>
public sealed record FolderArtWriteResult(bool Success, string? ErrorMessage, string? FolderArtPath)
{
    /// <summary>A written cover whose old file of the other format couldn't go to the Recycle Bin, and is still there.</summary>
    public string? Warning { get; init; }
}

/// <summary>
/// Writes tag edits and album art back to audio files. The write-side counterpart
/// to <see cref="TagReader"/>, sharing the same "never throw across this boundary"
/// contract - a failure becomes a <see cref="TagWriteResult"/>, not an exception.
/// Find and Replace's writes are in TagWriter.Replace.cs.
/// </summary>
public static partial class TagWriter
{
    /// <summary>Applies a single-track edit, optionally replacing the embedded art.</summary>
    /// <param name="holder">
    /// Playback, which may have the file open. A save that would resize such a
    /// file is made while playback lets go of it; see <see cref="SaveTags"/>.
    /// </param>
    public static TagWriteResult WriteTrackTags(Track track, TrackTagEdit edit, ArtPayload? art, string? folderArtPath,
                                                IFileHolder? holder = null)
    {
        var save = SaveTags(track.FilePath, file =>
        {
            var tag = file.Tag;
            tag.Title = edit.Title;
            tag.Performers = [edit.Artist];
            tag.AlbumArtists = [edit.AlbumArtist];
            tag.Album = edit.Album;
            WriteDate(file, edit.Date, edit.Year);
            WriteNumbers(file, new NumberEdit(edit.TrackNumber), new NumberEdit(edit.TrackCount),
                         new NumberEdit(edit.DiscNumber), new NumberEdit(edit.DiscCount));
            ApplyDetails(tag, edit.Details);
        }, art, holder);

        if (!save.Success)
            return TagWriteResult.Fail(save.ErrorMessage!);

        var warning = TidyCover(track.FilePath, art, holder);
        return TagWriteResult.Ok(track.WithTags(edit, FileStamp.For(track.FilePath), folderArtPath)) with { CoverWarning = warning };
    }

    /// <summary>
    /// Applies one track's share of a whole-album batch edit. Title and track
    /// number carry over from <paramref name="track"/> untouched - they are not
    /// part of <see cref="AlbumTagEdit"/> at all - and so does every field the
    /// edit leaves null. An empty Artist, Album Artist or Album clears it.
    /// </summary>
    public static TagWriteResult WriteAlbumTrackTags(Track track, AlbumTagEdit edit, ArtPayload? art, string? folderArtPath,
                                                     IFileHolder? holder = null)
    {
        var save = SaveTags(track.FilePath, file =>
        {
            var tag = file.Tag;
            if (edit.Artist is { } artist)
                tag.Performers = artist.Length == 0 ? [] : [artist];
            if (edit.AlbumArtist is { } albumArtist)
                tag.AlbumArtists = albumArtist.Length == 0 ? [] : [albumArtist];
            if (edit.Album is { } album)
                tag.Album = NullIfEmpty(album);
            if (edit.Date is { } date)
                WriteDate(file, date.Date, date.Year);
            // Setting a number to the value it already has respells it without
            // the zero; WriteNumbers undoes TagLib's own padding of the track.
            var strip = edit.RemoveLeadingZeros;
            WriteNumbers(file,
                strip ? Existing(tag.Track) : null,
                edit.TrackCount ?? (strip ? Existing(tag.TrackCount) : null),
                edit.DiscNumber ?? (strip ? Existing(tag.Disc) : null),
                edit.DiscCount ?? (strip ? Existing(tag.DiscCount) : null));
            ApplyDetails(tag, edit.Details);
        }, art, holder);

        if (!save.Success)
            return TagWriteResult.Fail(save.ErrorMessage!);

        var warning = TidyCover(track.FilePath, art, holder);
        return TagWriteResult.Ok(track.WithAlbumTags(edit, FileStamp.For(track.FilePath), folderArtPath)) with { CoverWarning = warning };
    }

    /// <summary>
    /// After a save that wrote a new cover: makes it the file's only picture and
    /// cuts the room the old one left (<see cref="Art.CoverCleaner.KeepOnly"/>), so
    /// a cover change never leaves two copies behind (the user's call). Null when
    /// that worked or wasn't needed; otherwise why not. The save itself stands.
    /// </summary>
    private static string? TidyCover(string path, ArtPayload? art, IFileHolder? holder)
    {
        if (art is null)
            return null;

        var tidy = Art.CoverCleaner.KeepOnly(path, art.Bytes, holder);
        return tidy.Outcome == Art.CoverCleanOutcome.Failed ? tidy.Error : null;
    }

    /// <summary>
    /// Applies one track's share of an edit to hand-picked tracks. Only the
    /// fields the edit sets are written; everything else stays as the file has it.
    /// Never touches the art.
    /// </summary>
    public static TagWriteResult WriteSelectedTrackTags(Track track, TracksTagEdit edit, IFileHolder? holder = null)
    {
        var save = SaveTags(track.FilePath, file =>
        {
            var tag = file.Tag;
            if (edit.Title is { } title)
                tag.Title = NullIfEmpty(title);
            if (edit.Artist is { } artist)
                tag.Performers = artist.Length == 0 ? [] : [artist];
            if (edit.AlbumArtist is { } albumArtist)
                tag.AlbumArtists = albumArtist.Length == 0 ? [] : [albumArtist];
            if (edit.Album is { } album)
                tag.Album = NullIfEmpty(album);
            if (edit.Date is { } date)
                WriteDate(file, date.Date, date.Year);

            WriteNumbers(file, edit.TrackNumber, edit.TrackCount, edit.DiscNumber, edit.DiscCount);
            ApplyDetails(tag, edit.Details);
        }, art: null, holder);

        if (!save.Success)
            return TagWriteResult.Fail(save.ErrorMessage!);

        return TagWriteResult.Ok(track.WithSelectedTags(edit, FileStamp.For(track.FilePath)));
    }

    /// <summary>
    /// Writes/overwrites the folder cover file for one album directory. Independent
    /// of the audio files - never touches TagLib.
    /// <para>
    /// The picture is written to a working file beside the cover and renamed over
    /// it once it is on the disk (<see cref="FileSwap.WriteWhole"/>), so a write
    /// that stops partway (the app closing, the drive pulled) leaves the old cover
    /// whole, never a half-written one.
    /// </para>
    /// <para>
    /// When an existing cover has a different extension than the new image, the new
    /// one is created under the correct extension and only then the old one goes,
    /// through <paramref name="recycle"/> (the Recycle Bin), so the bytes are never
    /// saved under the wrong format and the folder is never left without a cover.
    /// When the bin can't take it (a drive without one), the old file is kept and
    /// <see cref="FolderArtWriteResult.Warning"/> says so; the result's path is then
    /// whichever of the two the library's lookup picks.
    /// </para>
    /// </summary>
    public static FolderArtWriteResult WriteFolderArt(string directory, ArtPayload art, string? existingFolderArtPath,
                                                      Func<string, bool> recycle)
    {
        try
        {
            var extension = art.MimeType.Equals("image/png", StringComparison.OrdinalIgnoreCase) ? ".png" : ".jpg";
            var targetPath = Path.Combine(directory, "cover" + extension);

            var reusingExisting = existingFolderArtPath is not null
                && string.Equals(Path.GetExtension(existingFolderArtPath), extension, StringComparison.OrdinalIgnoreCase);

            if (reusingExisting)
                targetPath = existingFolderArtPath!;

            FileSwap.WriteWhole(targetPath, art.Bytes);

            if (reusingExisting || existingFolderArtPath is null || !File.Exists(existingFolderArtPath)
                || string.Equals(existingFolderArtPath, targetPath, StringComparison.OrdinalIgnoreCase)
                || recycle(existingFolderArtPath))
                return new FolderArtWriteResult(true, null, targetPath);

            var shown = TagReader.FindFolderArt(Path.Combine(directory, "_")) ?? targetPath;
            return new FolderArtWriteResult(true, null, shown)
            {
                Warning = $"the old {Path.GetFileName(existingFolderArtPath)} couldn't go to the Recycle Bin, so it's still there",
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            return new FolderArtWriteResult(false, ex.Message, existingFolderArtPath);
        }
    }

    /// <summary>
    /// Writes the fields a <see cref="TagDetailsEdit"/> sets and leaves the null
    /// ones alone. An empty value is written as null, which TagLib takes as
    /// "remove the frame" rather than leaving an empty one behind.
    /// </summary>
    private static void ApplyDetails(TagLib.Tag tag, TagDetailsEdit details)
    {
        if (details.Publisher is { } publisher)
            tag.Publisher = NullIfEmpty(publisher);
        if (details.Composer is { } composer)
            tag.Composers = TagDetails.Split(composer);
        if (details.Conductor is { } conductor)
            tag.Conductor = NullIfEmpty(conductor);
        if (details.Genre is { } genre)
            tag.Genres = TagDetails.Split(genre);
        if (details.Comment is { } comment)
            tag.Comment = NullIfEmpty(comment);
    }

    /// <summary>
    /// Sets the track and disc numbers and totals the caller passes; null keeps
    /// one. TagLib spells track 1 as "01" - in Xiph TRACKNUMBER when the number
    /// is set, in ID3v2 TRCK when the number or the total is - and has no switch
    /// for it, so both are respelled plainly afterwards. Otherwise fixing "01"
    /// to "1" in the dialog would write "01" straight back. Disc numbers TagLib
    /// already writes plainly.
    /// </summary>
    private static void WriteNumbers(TagLib.File file, NumberEdit? track, NumberEdit? trackCount,
                                     NumberEdit? disc, NumberEdit? discCount)
    {
        var tag = file.Tag;
        if (track is { } t)
            tag.Track = (uint)(t.Value ?? 0);
        if (trackCount is { } tc)
            tag.TrackCount = (uint)(tc.Value ?? 0);
        if (disc is { } d)
            tag.Disc = (uint)(d.Value ?? 0);
        if (discCount is { } dc)
            tag.DiscCount = (uint)(dc.Value ?? 0);

        if (tag.Track == 0 || (track is null && trackCount is null))
            return;

        if (track is not null && file.GetTag(TagLib.TagTypes.Xiph, false) is TagLib.Ogg.XiphComment xiph)
            xiph.SetField("TRACKNUMBER", tag.Track.ToString());
        if (file.GetTag(TagLib.TagTypes.Id3v2, false) is TagLib.Id3v2.Tag id3)
            id3.SetTextFrame("TRCK", tag.TrackCount > 0 ? $"{tag.Track}/{tag.TrackCount}" : tag.Track.ToString());
    }

    /// <summary>The file's current number as an edit that rewrites it; null (keep) when it has none.</summary>
    private static NumberEdit? Existing(uint value) => value > 0 ? new NumberEdit((int)value) : null;

    private static string? NullIfEmpty(string value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <summary>
    /// Writes the release date. TagLib's own <c>Year</c> holds a whole number, so
    /// it is set first - that fills every tag the file has - and a date naming a
    /// month or day is then written over it into each format's own date field.
    /// Without that second step, saving a file that had "2014-05-01" would quietly
    /// cut it back to "2014".
    /// </summary>
    private static void WriteDate(TagLib.File file, string? date, int? year)
    {
        file.Tag.Year = (uint)(year ?? 0);

        if (date is null || !ReleaseDate.HasMonth(date))
            return;

        if (file.GetTag(TagLib.TagTypes.Xiph, false) is TagLib.Ogg.XiphComment xiph)
            xiph.SetField("DATE", date);
        if (file.GetTag(TagLib.TagTypes.Id3v2, false) is TagLib.Id3v2.Tag id3)
        {
            // ID3v2.3 has no full-date frame: it splits a date into TYER and a
            // DDMM TDAT, and TagLib renders TDAT month-first, so every other
            // player would read 2 October as 10 February. v2.4's TDRC holds the
            // date as written. Only files given a full date are upgraded.
            if (id3.Version < 4)
                id3.Version = 4;
            id3.SetTextFrame("TDRC", date);
        }
        if (file.GetTag(TagLib.TagTypes.Apple, false) is TagLib.Mpeg4.AppleTag apple)
            apple.SetText(TagReader.Mp4DateAtom, date);
        if (file.GetTag(TagLib.TagTypes.Ape, false) is TagLib.Ape.Tag ape)
            ape.SetValue("Year", date);
        if (file.GetTag(TagLib.TagTypes.Asf, false) is TagLib.Asf.Tag asf)
            asf.SetDescriptorString(date, "WM/Year");
    }

    /// <summary>
    /// Opens the file, applies the edit and saves it, so that nothing that stops
    /// the save partway - the app closing, the laptop sleeping, the drive pulled -
    /// can leave the song damaged.
    /// <para>
    /// The save is first made against a stand-in for the file
    /// (<see cref="DryRunStream"/>), which says what it would write:
    /// </para>
    /// <list type="bullet">
    /// <item>Most edits fit the room the tag has, and change a few kilobytes at
    /// the start (or end) of the file. Those bytes are written into the file as
    /// they are. Stopped partway, only the tag can suffer; the audio's bytes are
    /// never written. A stream decoding the file hears nothing (measured bit for
    /// bit on FLAC, MP3 and DSF), so playback carries on.</item>
    /// <item>An edit that doesn't fit (a new cover, a field longer than the room)
    /// moves the audio along the file, which TagLib does in place, rewriting the
    /// whole song: stopped partway, the song is left half moved. That save is made
    /// on a copy beside the song instead, checked, and swapped in
    /// (<see cref="FileSwap"/>). A stream reading the file would jump and lose its
    /// end, so playback lets go of it for the save (<paramref name="holder"/>),
    /// which costs the playing track a short gap.</item>
    /// </list>
    /// <para>
    /// Either way the holder keeps the file from being opened until the save is
    /// done. Until session 60 the second kind was made in place, and for a file
    /// playback held, the whole save was first tried on a copy in %TEMP% to see
    /// which kind it was.
    /// </para>
    /// </summary>
    private static (bool Success, string? ErrorMessage) SaveTags(string path, Action<TagLib.File> applyFields,
                                                                 ArtPayload? art, IFileHolder? holder)
    {
        if (holder is null)
            return Commit(path, applyFields, art, Rehearse(path, applyFields, art));

        SavePlan? plan = null;
        return holder.Saving(path,
            needsRelease: () => (plan = Rehearse(path, applyFields, art)).MovesAudio,
            save: () => Commit(path, applyFields, art, plan ?? Rehearse(path, applyFields, art)));
    }

    /// <summary>
    /// What a save would do: fail (<see cref="Error"/>), change only
    /// <see cref="InPlace"/>'s bytes, or move the audio (neither).
    /// <see cref="Stamp"/> is the file as it was, so a change made since is noticed.
    /// </summary>
    private sealed record SavePlan(string? Error, IReadOnlyList<(long Offset, byte[] Bytes)>? InPlace, FileStamp Stamp)
    {
        public bool MovesAudio => Error is null && InPlace is null;
    }

    /// <summary>
    /// The most a save may write and still be taken for one that keeps the audio
    /// where it is: far more than any tag, cover included; far less than a song.
    /// </summary>
    private const long MaxInPlaceBytes = 32L << 20;

    /// <summary>Makes the save against a <see cref="DryRunStream"/>; the file is only read.</summary>
    private static SavePlan Rehearse(string path, Action<TagLib.File> applyFields, ArtPayload? art)
    {
        var stamp = FileStamp.For(path);
        try
        {
            using var stand = new DryRunStream(path, MaxInPlaceBytes);
            try
            {
                Apply(new StreamFile(path, stand), mimeType: null, applyFields, art);
            }
            catch (DryRunStream.TooMuchRewrittenException)
            {
                return new SavePlan(null, null, stamp);
            }

            return new SavePlan(null, stand.KeptSize ? stand.Changes() : null, stamp);
        }
        catch (Exception ex)
        {
            return new SavePlan(Describe(path, ex), null, stamp);
        }
    }

    private static (bool Success, string? ErrorMessage) Commit(string path, Action<TagLib.File> applyFields,
                                                               ArtPayload? art, SavePlan plan)
    {
        if (plan.Error is not null)
            return (false, plan.Error);

        if (FileStamp.For(path) != plan.Stamp)
            return (false, "the file changed while it was being saved - try again");

        try
        {
            if (plan.InPlace is { } changes)
                WriteInPlace(path, changes);
            else
                SaveOnCopy(path, applyFields, art);

            return (true, null);
        }
        catch (Exception ex)
        {
            return (false, Describe(path, ex));
        }
    }

    /// <summary>
    /// Writes the bytes a save that keeps the size changed, shared, since playback
    /// may be reading the file, and makes sure they are on the disk.
    /// </summary>
    private static void WriteInPlace(string path, IReadOnlyList<(long Offset, byte[] Bytes)> changes)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite);
        foreach (var (offset, bytes) in changes)
        {
            stream.Position = offset;
            stream.Write(bytes);
        }

        stream.Flush(flushToDisk: true);
    }

    /// <summary>
    /// Makes the save on a copy beside the song (same drive, so the swap is one
    /// step), checks the copy plays the same audio, and swaps it in. Throws on any
    /// failure, with the song untouched and the copy gone.
    /// </summary>
    private static void SaveOnCopy(string path, Action<TagLib.File> applyFields, ArtPayload? art)
    {
        var copy = Path.Combine(Path.GetDirectoryName(path)!, $"{FileSwap.Prefix}save-{Guid.NewGuid():N}{FileSwap.WorkingSuffix}");
        try
        {
            File.Copy(path, copy);

            // The copy ends in ".part", so a scan never takes it for a song; TagLib,
            // which goes by extension, is told the format instead.
            Apply(new StreamFile(copy, null), Art.CoverCleaner.MimeTypeFor(path), applyFields, art);

            using (var written = new FileStream(copy, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                written.Flush(flushToDisk: true);

            if (Art.CoverCleaner.SameAudio(path, copy) is { } problem)
                throw new CopyCheckFailed(problem);

            FileSwap.Replace(copy, path);
        }
        finally
        {
            try { File.Delete(copy); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>The saved copy didn't check out; the message says why, and the song is untouched.</summary>
    private sealed class CopyCheckFailed(string reason) : IOException(reason);

    /// <summary>Opens <paramref name="target"/> with TagLib, applies the edit and the art, and saves.</summary>
    private static void Apply(StreamFile target, string? mimeType, Action<TagLib.File> applyFields, ArtPayload? art)
    {
        using var file = mimeType is null
            ? TagLib.File.Create(target)
            : TagLib.File.Create(target, mimeType, TagLib.ReadStyle.Average);
        applyFields(file);

        if (art is not null)
        {
            file.Tag.Pictures =
            [
                new TagLib.Picture(new TagLib.ByteVector(art.Bytes))
                {
                    Type = TagLib.PictureType.FrontCover,
                    MimeType = art.MimeType,
                    Description = "Cover",
                },
            ];
        }

        file.Save();
    }

    /// <summary>
    /// TagLib's file access. With a <paramref name="stream"/>, every read and write
    /// goes to it (a <see cref="DryRunStream"/>), and it is left open between
    /// TagLib's read and write phases. Without one, the file itself, opened
    /// shared: TagLib's own access opens for writing with
    /// <see cref="FileShare.None"/>.
    /// </summary>
    private sealed class StreamFile(string path, Stream? stream) : TagLib.File.IFileAbstraction
    {
        public string Name => path;

        public Stream ReadStream => stream ??
            new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

        public Stream WriteStream => stream ??
            new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);

        public void CloseStream(Stream closing)
        {
            if (!ReferenceEquals(closing, stream))
                closing.Dispose();
        }
    }

    private static string Describe(string path, Exception ex) => ex switch
    {
        TagLib.UnsupportedFormatException or TagLib.CorruptFileException or IOException
            or UnauthorizedAccessException or NotSupportedException => DescribeFailure(path, ex),
        _ => $"Unexpected error: {ex.Message}",
    };

    private static string DescribeFailure(string path, Exception ex) => ex switch
    {
        _ when !File.Exists(path) => "the file no longer exists",
        UnauthorizedAccessException => "permission denied - check the file isn't read-only",
        TagLib.UnsupportedFormatException => "unsupported file format",
        TagLib.CorruptFileException => "the file appears to be corrupt",
        CopyCheckFailed => ex.Message,
        IOException => "the file may be open in another program",
        _ => ex.Message,
    };
}
