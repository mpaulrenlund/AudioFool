using System.Globalization;

namespace AudioFool.Core.Playlists;

/// <summary>The words the Playlists panel and the playlist header use.</summary>
public static class PlaylistText
{
    /// <summary>
    /// "Modified 2026-10-07", in local time: the album header's ISO date format
    /// for release dates.
    /// </summary>
    public static string Modified(DateTime modifiedUtc) =>
        "Modified " + modifiedUtc.ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// <summary>"1 track" / "12 tracks", for the Playlists panel's rows.</summary>
    public static string TrackCount(int count) => count == 1 ? "1 track" : $"{count:N0} tracks";

    /// <summary>
    /// The header's track line: the count, then how many the library doesn't
    /// have, when any ("12 tracks · 2 not found"), so a short list says why.
    /// </summary>
    public static string HeaderTrackCount(int count, int missing) =>
        missing == 0 ? TrackCount(count) : $"{TrackCount(count)} · {missing:N0} not found";

    /// <summary>What the Songs panel says for a playlist with nothing to show.</summary>
    public static (string Title, string Detail) Empty(bool isLiked, int missing)
    {
        if (missing > 0)
        {
            return ("None of these songs were found",
                    missing == 1
                        ? "Its song isn't in the library any more."
                        : $"Its {missing:N0} songs aren't in the library any more.");
        }

        return isLiked
            ? ("No liked songs yet", "Click the heart beside a song to add it here.")
            : ("This playlist is empty", "Right-click songs and choose Add to Playlist.");
    }
}
