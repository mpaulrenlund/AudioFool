using AudioFool.Core.Library;
using AudioFool.Core.Models;

namespace AudioFool.Core.Playlists;

/// <summary>
/// The Recently Added playlist (the user's request, 2026-10-07): every song that
/// arrived on the drive in the last <see cref="Days"/> days, worked out from the
/// library each time rather than saved. "Arrived" is <see cref="Track.AddedUtc"/>,
/// the file's creation time, as the ARTISTS · RECENT sort uses.
/// <para>
/// It is grouped into albums so the Albums panel can list them. An album holds
/// only its recent songs (the user's call), newest album first; within an album
/// the songs keep disc and track order, since files copied together get creation
/// times a few milliseconds apart in whatever order the copy took them.
/// </para>
/// </summary>
public static class RecentlyAdded
{
    public const string Name = "Recently Added";

    public const int Days = 30;

    /// <summary>The header's line in place of "Modified ...".</summary>
    public static string Window => $"Last {Days} days";

    /// <summary>What the Songs panel says when nothing arrived in the window.</summary>
    public static (string Title, string Detail) Empty =>
        ($"Nothing added in the last {Days} days",
         "Songs copied into the library show here for 30 days.");

    /// <summary>
    /// The library's albums that hold a song added since <paramref name="nowUtc"/>
    /// less <paramref name="days"/>, each cut down to those songs. Newest
    /// addition first; ties by artist, then title, as the Artists list sorts.
    /// Songs with no date yet are left out.
    /// </summary>
    public static IReadOnlyList<Album> Albums(MusicLibrary library, DateTime nowUtc, int days = Days)
    {
        var since = nowUtc.AddDays(-days);
        var found = new List<(Album Album, string ArtistKey, DateTime Newest)>();

        foreach (var artist in library.Artists)
        {
            foreach (var album in artist.Albums)
            {
                var recent = album.Tracks.Where(t => t.AddedUtc is { } added && added >= since).ToList();
                if (recent.Count == 0)
                    continue;

                var part = recent.Count == album.Tracks.Count
                    ? album
                    : new Album
                    {
                        Title = album.Title,
                        ArtistName = album.ArtistName,
                        Year = album.Year,
                        SortDate = album.SortDate,
                        FolderArtPath = album.FolderArtPath,
                        Tracks = recent,
                    };

                found.Add((part, artist.SortKey, recent.Max(t => t.AddedUtc!.Value)));
            }
        }

        return [.. found
            .OrderByDescending(f => f.Newest)
            .ThenBy(f => f.ArtistKey, SortRules.NameComparer)
            .ThenBy(f => f.Album.Title, SortRules.NameComparer)
            .Select(f => f.Album)];
    }

    /// <summary>Every song in <paramref name="albums"/>, in the order the Songs panel lists them.</summary>
    public static IReadOnlyList<Track> Tracks(IReadOnlyList<Album> albums) =>
        [.. albums.SelectMany(a => a.Tracks)];
}
