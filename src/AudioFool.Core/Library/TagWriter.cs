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
        var save = SaveTags(track.FilePath, tag =>
        {
            tag.Title = edit.Title;
            tag.Performers = [edit.Artist];
            tag.AlbumArtists = [edit.AlbumArtist];
            tag.Album = edit.Album;
            tag.Year = (uint)(edit.Year ?? 0);
            tag.Track = (uint)(edit.TrackNumber ?? 0);
            tag.Disc = (uint)(edit.DiscNumber ?? 0);
        }, art);

        if (!save.Success)
            return TagWriteResult.Fail(save.ErrorMessage!);

        return TagWriteResult.Ok(track.WithTags(edit, FileStamp.For(track.FilePath), folderArtPath));
    }

    /// <summary>
    /// Applies one track's share of a whole-album batch edit. Title, track number
    /// and disc number carry over from <paramref name="track"/> untouched - they
    /// are not part of <see cref="AlbumTagEdit"/> at all.
    /// </summary>
    public static TagWriteResult WriteAlbumTrackTags(Track track, AlbumTagEdit edit, ArtPayload? art, string? folderArtPath)
    {
        var save = SaveTags(track.FilePath, tag =>
        {
            tag.Performers = [edit.Artist];
            tag.AlbumArtists = [edit.AlbumArtist];
            tag.Album = edit.Album;
            tag.Year = (uint)(edit.Year ?? 0);
        }, art);

        if (!save.Success)
            return TagWriteResult.Fail(save.ErrorMessage!);

        return TagWriteResult.Ok(track.WithAlbumTags(edit, FileStamp.For(track.FilePath), folderArtPath));
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

    private static (bool Success, string? ErrorMessage) SaveTags(string path, Action<TagLib.Tag> applyFields, ArtPayload? art)
    {
        TagLib.File? file = null;
        try
        {
            file = TagLib.File.Create(path);
            applyFields(file.Tag);

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
