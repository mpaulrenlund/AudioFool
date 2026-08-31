namespace AudioFool.Core.Library;

/// <summary>
/// Every tag field for a single-track edit. All seven are authoritative - the
/// caller always pre-fills every field with the track's current value, so there
/// is no "null means leave alone" case to model.
/// </summary>
public sealed record TrackTagEdit(
    string Title,
    string Artist,
    string AlbumArtist,
    string Album,
    int? Year,
    int? TrackNumber,
    int? DiscNumber);

/// <summary>
/// The fields shared by a whole-album batch edit. Title, track number and disc
/// number are deliberately absent - they differ per track and are left alone.
/// </summary>
public sealed record AlbumTagEdit(
    string Artist,
    string AlbumArtist,
    string Album,
    int? Year);

/// <summary>An image to embed in a track's tags and/or write as a folder cover file.</summary>
public sealed record ArtPayload(byte[] Bytes, string MimeType);
