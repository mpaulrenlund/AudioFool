using AudioFool.Core.Playback;

namespace AudioFool.Core.Tests;

public class PlayOrderTests
{
    /// <summary>A fixed seed, so a shuffled order is reproducible in assertions.</summary>
    private static PlayOrder Shuffled(int count, int currentIndex, int seed = 1234)
    {
        var order = new PlayOrder();
        order.Reset(count, currentIndex, shuffle: true, rng: new Random(seed));
        return order;
    }

    private static PlayOrder Sequential(int count)
    {
        var order = new PlayOrder();
        order.Reset(count, 0, shuffle: false);
        return order;
    }

    // ---------- sequential ----------

    [Fact]
    public void Sequential_order_is_the_queue_order()
    {
        var order = Sequential(5);

        Assert.Equal([0, 1, 2, 3, 4], order.Order);
        Assert.False(order.IsShuffled);
    }

    [Fact]
    public void Sequential_steps_forward_one_at_a_time()
    {
        var order = Sequential(4);

        Assert.Equal(1, order.Next(0, wrap: false));
        Assert.Equal(2, order.Next(1, wrap: false));
        Assert.Equal(3, order.Next(2, wrap: false));
    }

    [Fact]
    public void Stops_at_the_end_without_wrap()
    {
        var order = Sequential(3);

        Assert.Equal(-1, order.Next(2, wrap: false));
    }

    [Fact]
    public void Wraps_to_the_start_with_wrap()
    {
        var order = Sequential(3);

        Assert.Equal(0, order.Next(2, wrap: true));
    }

    [Fact]
    public void Stops_at_the_start_going_back_without_wrap()
    {
        var order = Sequential(3);

        Assert.Equal(-1, order.Previous(0, wrap: false));
    }

    [Fact]
    public void Wraps_to_the_end_going_back_with_wrap()
    {
        var order = Sequential(3);

        Assert.Equal(2, order.Previous(0, wrap: true));
    }

    // ---------- shuffled ----------

    [Fact]
    public void Shuffling_keeps_every_track_exactly_once()
    {
        var order = Shuffled(50, currentIndex: 0);

        Assert.Equal(50, order.Count);
        Assert.Equal(Enumerable.Range(0, 50), order.Order.OrderBy(i => i));
    }

    [Fact]
    public void Shuffling_actually_reorders_something()
    {
        // Not a guarantee for any single seed in theory, but with 50 tracks the
        // odds of the identity permutation are 1 in 50 factorial.
        var order = Shuffled(50, currentIndex: 0);

        Assert.NotEqual(Enumerable.Range(0, 50), order.Order);
        Assert.True(order.IsShuffled);
    }

    [Fact]
    public void The_playing_track_is_pinned_to_the_front()
    {
        // Toggling shuffle mid-album must not change what is currently playing.
        foreach (var current in new[] { 0, 1, 7, 19 })
        {
            var order = Shuffled(20, current);

            Assert.Equal(current, order.Order[0]);
            Assert.Equal(0, order.PositionOf(current));
        }
    }

    [Fact]
    public void Next_follows_the_shuffled_order_not_the_queue_order()
    {
        var order = Shuffled(10, currentIndex: 3);

        var walked = new List<int> { 3 };
        var at = 3;
        while (true)
        {
            at = order.Next(at, wrap: false);
            if (at < 0)
                break;
            walked.Add(at);
        }

        Assert.Equal(order.Order, walked);
    }

    [Fact]
    public void Previous_retraces_the_shuffled_order()
    {
        var order = Shuffled(10, currentIndex: 4);

        var first = order.Next(4, wrap: false);
        var second = order.Next(first, wrap: false);

        Assert.Equal(first, order.Previous(second, wrap: false));
        Assert.Equal(4, order.Previous(first, wrap: false));
    }

    [Fact]
    public void Walking_forward_visits_every_track_once()
    {
        var order = Shuffled(30, currentIndex: 11);

        var seen = new HashSet<int> { 11 };
        var at = 11;
        for (var step = 0; step < 29; step++)
        {
            at = order.Next(at, wrap: false);
            Assert.True(at >= 0);
            Assert.True(seen.Add(at), "a track came up twice in one pass");
        }

        Assert.Equal(30, seen.Count);
        Assert.Equal(-1, order.Next(at, wrap: false));
    }

    [Fact]
    public void Turning_shuffle_off_restores_the_queue_order()
    {
        var order = new PlayOrder();
        order.Reset(8, 5, shuffle: true, rng: new Random(99));
        order.Reset(8, 5, shuffle: false);

        Assert.Equal([0, 1, 2, 3, 4, 5, 6, 7], order.Order);
        Assert.False(order.IsShuffled);
        Assert.Equal(6, order.Next(5, wrap: false));
    }

    // ---------- edges ----------

    [Fact]
    public void An_empty_queue_has_no_next_or_previous()
    {
        var order = new PlayOrder();
        order.Reset(0, -1, shuffle: false);

        Assert.Equal(0, order.Count);
        Assert.Equal(-1, order.Next(0, wrap: true));
        Assert.Equal(-1, order.Previous(0, wrap: true));
    }

    [Fact]
    public void A_single_track_wraps_to_itself()
    {
        var order = Sequential(1);

        Assert.Equal(-1, order.Next(0, wrap: false));
        Assert.Equal(0, order.Next(0, wrap: true));
        Assert.Equal(0, order.Previous(0, wrap: true));
    }

    [Fact]
    public void A_single_track_shuffles_to_itself()
    {
        var order = Shuffled(1, currentIndex: 0);

        Assert.Equal([0], order.Order);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(5)]
    [InlineData(99)]
    public void An_index_outside_the_queue_has_no_neighbours(int index)
    {
        var order = Sequential(5);

        Assert.Equal(-1, order.PositionOf(index));
        Assert.Equal(-1, order.Next(index, wrap: true));
        Assert.Equal(-1, order.Previous(index, wrap: true));
    }

    [Fact]
    public void Shuffling_with_no_current_track_still_covers_the_queue()
    {
        var order = Shuffled(12, currentIndex: -1);

        Assert.Equal(Enumerable.Range(0, 12), order.Order.OrderBy(i => i));
    }
}
