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

    public static TagWriteResult Ok(Track updated) => new() { Success = true, UpdatedTrack = updated };
    public static TagWriteResult Fail(string message) => new() { Success = false, ErrorMessage = message };
}

/// <summary>Result of writing (or overwriting) a folder-level cover file.</summary>
public sealed record FolderArtWriteResult(bool Success, string? ErrorMessage, string? FolderArtPath);

/// <summary>
/// Writes tag edits and album art back to audio files. The write-side counterpart
/// to <see cref="TagReader"/>, sharing the same "never throw across this boundary"
/// contract - a failure becomes a <see cref="TagWriteResult"/>, not an exception.
/// </summary>
public static class TagWriter
{
    /// <summary>Applies a single-track edit, optionally replacing the embedded art.</summary>
    public static TagWriteResult WriteTrackTags(Track track, TrackTagEdit edit, ArtPayload? art, string? folderArtPath)
    {
        var save = SaveTags(track.FilePath, file =>
        {
            var tag = file.Tag;
            tag.Title = edit.Title;
            tag.Performers = [edit.Artist];
            tag.AlbumArtists = [edit.AlbumArtist];
            tag.Album = edit.Album;
            WriteDate(file, edit.Date, edit.Year);
            tag.Track = (uint)(edit.TrackNumber ?? 0);
            tag.TrackCount = (uint)(edit.TrackCount ?? 0);
            tag.Disc = (uint)(edit.DiscNumber ?? 0);
            tag.DiscCount = (uint)(edit.DiscCount ?? 0);
            ApplyDetails(tag, edit.Details);
        }, art);

        if (!save.Success)
            return TagWriteResult.Fail(save.ErrorMessage!);

        return TagWriteResult.Ok(track.WithTags(edit, FileStamp.For(track.FilePath), folderArtPath));
    }

    /// <summary>
    /// Applies one track's share of a whole-album batch edit. Title and track
    /// number carry over from <paramref name="track"/> untouched - they are not
    /// part of <see cref="AlbumTagEdit"/> at all - and so does every optional
    /// field the edit leaves null.
    /// </summary>
    public static TagWriteResult WriteAlbumTrackTags(Track track, AlbumTagEdit edit, ArtPayload? art, string? folderArtPath)
    {
        var save = SaveTags(track.FilePath, file =>
        {
            var tag = file.Tag;
            tag.Performers = [edit.Artist];
            tag.AlbumArtists = [edit.AlbumArtist];
            tag.Album = edit.Album;
            WriteDate(file, edit.Date, edit.Year);

            if (edit.TrackCount is { } trackCount)
                tag.TrackCount = (uint)(trackCount.Value ?? 0);
            if (edit.DiscNumber is { } discNumber)
                tag.Disc = (uint)(discNumber.Value ?? 0);
            if (edit.DiscCount is { } discCount)
                tag.DiscCount = (uint)(discCount.Value ?? 0);

            ApplyDetails(tag, edit.Details);
        }, art);

        if (!save.Success)
            return TagWriteResult.Fail(save.ErrorMessage!);

        return TagWriteResult.Ok(track.WithAlbumTags(edit, FileStamp.For(track.FilePath), folderArtPath));
    }

    /// <summary>
    /// Applies one track's share of an edit to hand-picked tracks. Only the
    /// fields the edit sets are written; everything else stays as the file has it.
    /// Never touches the art.
    /// </summary>
    public static TagWriteResult WriteSelectedTrackTags(Track track, TracksTagEdit edit)
    {
        var save = SaveTags(track.FilePath, file =>
        {
            var tag = file.Tag;
            if (edit.Title is { } title)
                tag.Title = NullIfEmpty(title);
            if (edit.TrackNumber is { } trackNumber)
                tag.Track = (uint)(trackNumber.Value ?? 0);
            if (edit.Artist is { } artist)
                tag.Performers = artist.Length == 0 ? [] : [artist];
            if (edit.AlbumArtist is { } albumArtist)
                tag.AlbumArtists = albumArtist.Length == 0 ? [] : [albumArtist];
            if (edit.Album is { } album)
                tag.Album = NullIfEmpty(album);
            if (edit.Date is { } date)
                WriteDate(file, date.Date, date.Year);

            if (edit.TrackCount is { } trackCount)
                tag.TrackCount = (uint)(trackCount.Value ?? 0);
            if (edit.DiscNumber is { } discNumber)
                tag.Disc = (uint)(discNumber.Value ?? 0);
            if (edit.DiscCount is { } discCount)
                tag.DiscCount = (uint)(discCount.Value ?? 0);

            ApplyDetails(tag, edit.Details);
        }, art: null);

        if (!save.Success)
            return TagWriteResult.Fail(save.ErrorMessage!);

        return TagWriteResult.Ok(track.WithSelectedTags(edit, FileStamp.For(track.FilePath)));
    }

    /// <summary>
    /// Writes/overwrites the folder cover file for one album directory. Independent
    /// of the audio files - never touches TagLib.
    /// <para>
    /// When an existing cover has a different extension than the new image, the old
    /// file is deleted and the new one created under the correct extension, so two
    /// competing covers are never left behind and the bytes are never saved under
    /// the wrong format.
    /// </para>
    /// </summary>
    public static FolderArtWriteResult WriteFolderArt(string directory, ArtPayload art, string? existingFolderArtPath)
    {
        try
        {
            var extension = art.MimeType.Equals("image/png", StringComparison.OrdinalIgnoreCase) ? ".png" : ".jpg";
            var targetPath = Path.Combine(directory, "cover" + extension);

            var reusingExisting = existingFolderArtPath is not null
                && string.Equals(Path.GetExtension(existingFolderArtPath), extension, StringComparison.OrdinalIgnoreCase);

            if (reusingExisting)
                targetPath = existingFolderArtPath!;
            else if (existingFolderArtPath is not null && File.Exists(existingFolderArtPath)
                     && !string.Equals(existingFolderArtPath, targetPath, StringComparison.OrdinalIgnoreCase))
                File.Delete(existingFolderArtPath);

            File.WriteAllBytes(targetPath, art.Bytes);
            return new FolderArtWriteResult(true, null, targetPath);
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

    private static (bool Success, string? ErrorMessage) SaveTags(string path, Action<TagLib.File> applyFields, ArtPayload? art)
    {
        TagLib.File? file = null;
        try
        {
            file = TagLib.File.Create(path);
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
            return (true, null);
        }
        catch (Exception ex) when (ex is TagLib.UnsupportedFormatException
                                     or TagLib.CorruptFileException
                                     or IOException
                                     or UnauthorizedAccessException
                                     or NotSupportedException)
        {
            return (false, DescribeFailure(path, ex));
        }
        catch (Exception ex)
        {
            return (false, $"Unexpected error: {ex.Message}");
        }
        finally
        {
            file?.Dispose();
        }
    }

    private static string DescribeFailure(string path, Exception ex) => ex switch
    {
        _ when !File.Exists(path) => "the file no longer exists",
        UnauthorizedAccessException => "permission denied - check the file isn't read-only",
        TagLib.UnsupportedFormatException => "unsupported file format",
        TagLib.CorruptFileException => "the file appears to be corrupt",
        IOException => "the file may be open in another program",
        _ => ex.Message,
    };
}
