using AudioFool.Core.Models;

namespace AudioFool.Core.Playback;

/// <summary>
/// The Artists and Albums headers' shuffle buttons (session 80, the user's
/// request): the queue a click plays and the random song it starts on. The
/// queue is in library order; the engine's Shuffle, turned on by the click,
/// plays it in a random order with the starting song pinned first.
/// </summary>
public static class ShufflePlay
{
    /// <summary>A random place to start in a queue of <paramref name="count"/> songs; null for none.</summary>
    public static int? StartIndex(int count, Random random)
    {
        ArgumentNullException.ThrowIfNull(random);
        return count > 0 ? random.Next(count) : null;
    }

    /// <summary>
    /// Every song of <paramref name="artists"/>, artist by artist and album by album
    /// as the lists show them; an artist picked twice, or a song under two artists,
    /// is queued once.
    /// </summary>
    public static IReadOnlyList<Track> ArtistsQueue(IEnumerable<ArtistGroup> artists)
    {
        ArgumentNullException.ThrowIfNull(artists);
        var queue = new List<Track>();
        var seen = new HashSet<Track>(ReferenceEqualityComparer.Instance);
        foreach (var artist in artists.Distinct())
            foreach (var album in artist.Albums)
                foreach (var track in album.Tracks)
                    if (seen.Add(track))
                        queue.Add(track);
        return queue;
    }
}
