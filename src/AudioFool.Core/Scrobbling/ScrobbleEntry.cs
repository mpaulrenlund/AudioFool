using AudioFool.Core.Models;

namespace AudioFool.Core.Scrobbling;

/// <summary>
/// One play as Last.fm is told about it. <see cref="Timestamp"/> is when the play
/// started, in Unix seconds UTC, which is what Last.fm asks for. Also the format
/// of the offline queue file, so renaming a member loses queued scrobbles.
/// </summary>
public sealed record ScrobbleEntry
{
    public required string Artist { get; init; }
    public required string Track { get; init; }
    public string? Album { get; init; }
    public string? AlbumArtist { get; init; }
    public int? TrackNumber { get; init; }
    public int? DurationSeconds { get; init; }
    public long Timestamp { get; init; }

    /// <summary>
    /// The track as Last.fm should see it, or null when it has no title or no
    /// artist tag. The file name is deliberately not used as a title: "01 -
    /// Untitled" on a profile is worse than no scrobble. Artist is the track
    /// artist, falling back to the album artist; the album artist goes in its
    /// own field, which is how Last.fm files compilations and features.
    /// </summary>
    public static ScrobbleEntry? From(Track track, TimeSpan duration, DateTimeOffset startedAt)
    {
        var title = track.Title.Trim();
        var artist = string.IsNullOrWhiteSpace(track.Artist) ? track.AlbumArtist.Trim() : track.Artist.Trim();
        if (title.Length == 0 || artist.Length == 0)
            return null;

        return new ScrobbleEntry
        {
            Artist = artist,
            Track = title,
            Album = NullIfBlank(track.Album),
            AlbumArtist = NullIfBlank(track.AlbumArtist),
            TrackNumber = track.TrackNumber is > 0 ? track.TrackNumber : null,
            DurationSeconds = duration > TimeSpan.Zero ? (int)Math.Round(duration.TotalSeconds) : null,
            Timestamp = startedAt.ToUnixTimeSeconds(),
        };
    }

    private static string? NullIfBlank(string value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
