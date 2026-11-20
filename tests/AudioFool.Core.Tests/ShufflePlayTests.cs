using AudioFool.Core.Models;
using AudioFool.Core.Playback;

namespace AudioFool.Core.Tests;

/// <summary>The Artists and Albums headers' shuffle buttons (session 80): the queue and where it starts.</summary>
public class ShufflePlayTests
{
    private static Album AlbumOf(string title, params string[] files) => new()
    {
        Title = title,
        ArtistName = "Artist",
        Tracks = files.Select(f => new Track { FilePath = f }).ToList(),
    };

    private static ArtistGroup ArtistOf(string name, params Album[] albums) =>
        new() { Name = name, SortKey = name, Albums = albums };

    [Fact]
    public void An_empty_queue_has_nowhere_to_start()
    {
        Assert.Null(ShufflePlay.StartIndex(0, new Random(1)));
    }

    [Fact]
    public void The_start_is_random_and_reaches_every_song()
    {
        var random = new Random(80);
        var starts = Enumerable.Range(0, 2000).Select(_ => ShufflePlay.StartIndex(10, random)!.Value).ToList();

        Assert.All(starts, s => Assert.InRange(s, 0, 9));
        Assert.Equal(10, starts.Distinct().Count());
    }

    [Fact]
    public void A_one_song_queue_starts_on_it()
    {
        Assert.Equal(0, ShufflePlay.StartIndex(1, new Random(3)));
    }

    [Fact]
    public void An_artist_s_queue_is_every_album_in_list_order()
    {
        var artist = ArtistOf("A", AlbumOf("First", "a1", "a2"), AlbumOf("Second", "b1"));

        var queue = ShufflePlay.ArtistsQueue([artist]);

        Assert.Equal(["a1", "a2", "b1"], queue.Select(t => t.FilePath));
    }

    [Fact]
    public void Several_artists_queue_one_after_another_each_song_once()
    {
        var shared = AlbumOf("Split", "s1");
        var a = ArtistOf("A", AlbumOf("One", "a1"), shared);
        var b = ArtistOf("B", AlbumOf("Two", "b1"), shared);

        var queue = ShufflePlay.ArtistsQueue([a, b, a]);

        Assert.Equal(["a1", "s1", "b1"], queue.Select(t => t.FilePath));
    }

    [Fact]
    public void No_artist_gives_an_empty_queue()
    {
        Assert.Empty(ShufflePlay.ArtistsQueue([]));
    }
}
