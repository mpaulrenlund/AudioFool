using AudioFool.Core.Models;
using AudioFool.Core.Playback;

namespace AudioFool.Core.Tests;

public class HearingTimelineTests
{
    private static readonly Track First = new() { FilePath = "first.flac" };
    private static readonly Track Second = new() { FilePath = "second.flac" };

    /// <summary>A timeline at 1000 Hz, so a frame is a millisecond.</summary>
    private static HearingTimeline AtOneKilohertz()
    {
        var timeline = new HearingTimeline();
        timeline.Reset(1000);
        return timeline;
    }

    [Fact]
    public void Nothing_to_go_on_before_the_first_block()
    {
        var timeline = AtOneKilohertz();

        Assert.False(timeline.TryLookup(0, out _, out _));
        Assert.Equal(0, timeline.TotalFrames);
    }

    [Fact]
    public void A_frame_inside_a_block_is_that_block_s_start_plus_the_way_in()
    {
        var timeline = AtOneKilohertz();
        timeline.Append(First, 10.0, 20);
        timeline.Append(First, 10.020, 20);

        Assert.True(timeline.TryLookup(25, out var track, out var seconds));
        Assert.Same(First, track);
        Assert.Equal(10.025, seconds, 6);
        Assert.Equal(40, timeline.TotalFrames);
    }

    [Fact]
    public void The_heard_frame_trails_what_was_handed_over_by_what_the_device_holds()
    {
        // 300 ms handed over, 200 ms still in the device: 100 ms has been heard.
        var timeline = AtOneKilohertz();
        for (var i = 0; i < 15; i++)
            timeline.Append(First, 5.0 + i * 0.020, 20);

        Assert.True(timeline.TryLookup(timeline.TotalFrames - 200, out _, out var seconds));
        Assert.Equal(5.100, seconds, 6);
    }

    [Fact]
    public void A_frame_past_the_end_gives_the_end_of_the_last_block()
    {
        var timeline = AtOneKilohertz();
        timeline.Append(First, 1.0, 20);

        Assert.True(timeline.TryLookup(500, out var track, out var seconds));
        Assert.Same(First, track);
        Assert.Equal(1.020, seconds, 6);
    }

    [Fact]
    public void A_gapless_join_switches_track_at_the_join_not_at_the_block()
    {
        // The engine splits a block at the join: 12 frames of the old track's
        // end, then 8 of the new one from its start.
        var timeline = AtOneKilohertz();
        timeline.Append(First, 179.988, 12);
        timeline.Append(Second, 0, 8);

        Assert.True(timeline.TryLookup(11, out var before, out var end));
        Assert.Same(First, before);
        Assert.Equal(179.999, end, 6);

        Assert.True(timeline.TryLookup(12, out var after, out var start));
        Assert.Same(Second, after);
        Assert.Equal(0, start, 6);
    }

    [Fact]
    public void A_seek_shows_its_target_until_it_is_heard()
    {
        // At 30 s the bar was moved to 90 s. The device still holds the old spot.
        var timeline = AtOneKilohertz();
        timeline.Append(First, 30.000, 20);
        timeline.Append(First, 30.020, 20);
        timeline.MarkSeek(First, 90.000);

        // Before the first block from the target has even been handed over.
        Assert.True(timeline.TryLookup(10, out var track, out var held));
        Assert.Same(First, track);
        Assert.Equal(90.000, held, 6);

        timeline.Append(First, 90.000, 20);
        timeline.Append(First, 90.020, 20);

        Assert.True(timeline.TryLookup(39, out _, out var stillHeld));
        Assert.Equal(90.000, stillHeld, 6);

        Assert.True(timeline.TryLookup(45, out _, out var moving));
        Assert.Equal(90.005, moving, 6);
    }

    [Fact]
    public void Two_seeks_in_a_row_hold_at_the_newer_target()
    {
        var timeline = AtOneKilohertz();
        timeline.Append(First, 30.000, 20);
        timeline.MarkSeek(First, 90.000);
        timeline.Append(First, 90.000, 20);
        timeline.MarkSeek(First, 120.000);
        timeline.Append(First, 120.000, 20);

        Assert.True(timeline.TryLookup(5, out _, out var held));
        Assert.Equal(120.000, held, 6);

        // Inside the first seek's block, the second one is still to come.
        Assert.True(timeline.TryLookup(25, out _, out var stillHeld));
        Assert.Equal(120.000, stillHeld, 6);

        Assert.True(timeline.TryLookup(50, out _, out var moving));
        Assert.Equal(120.010, moving, 6);
    }

    [Fact]
    public void A_reset_forgets_a_seek()
    {
        var timeline = AtOneKilohertz();
        timeline.Append(First, 30.000, 20);
        timeline.MarkSeek(First, 90.000);

        timeline.Reset(1000);
        timeline.Append(Second, 0, 20);

        Assert.True(timeline.TryLookup(10, out var track, out var seconds));
        Assert.Same(Second, track);
        Assert.Equal(0.010, seconds, 6);
    }

    [Fact]
    public void Silence_after_the_queue_ends_has_no_track()
    {
        var timeline = AtOneKilohertz();
        timeline.Append(First, 200.0, 20);
        timeline.Append(null, 0, 20);

        Assert.True(timeline.TryLookup(30, out var track, out _));
        Assert.Null(track);
    }

    [Fact]
    public void Old_frames_drop_out_of_the_history()
    {
        var timeline = AtOneKilohertz();
        for (var i = 0; i < 2000; i++)
            timeline.Append(First, i * 0.010, 10);

        Assert.False(timeline.TryLookup(0, out _, out _));
        Assert.True(timeline.TryLookup(timeline.TotalFrames - 200, out _, out var recent));
        Assert.Equal(19.800, recent, 6);
    }

    [Fact]
    public void A_reset_starts_again_at_the_new_rate()
    {
        var timeline = AtOneKilohertz();
        timeline.Append(First, 100.0, 1000);
        var generation = timeline.Generation;

        timeline.Reset(48000);
        Assert.NotEqual(generation, timeline.Generation);
        Assert.Equal(0, timeline.TotalFrames);
        Assert.False(timeline.TryLookup(0, out _, out _));

        timeline.Append(Second, 0, 4800);
        Assert.True(timeline.TryLookup(2400, out var track, out var seconds));
        Assert.Same(Second, track);
        Assert.Equal(0.050, seconds, 6);
    }

    [Fact]
    public void Empty_blocks_are_ignored()
    {
        var timeline = AtOneKilohertz();
        timeline.Append(First, 1.0, 0);

        Assert.Equal(0, timeline.TotalFrames);
        Assert.False(timeline.TryLookup(0, out _, out _));
    }

    [Fact]
    public async Task A_reader_on_another_thread_never_sees_a_torn_or_backward_reading()
    {
        // The writer appends 10 ms blocks of one steadily advancing track as fast
        // as it can; the reader asks for a frame 200 ms behind the end each time.
        // Every answer must be a real position, never earlier than the last one.
        var timeline = AtOneKilohertz();
        timeline.Append(First, 0, 10);
        using var stop = new CancellationTokenSource();

        var writer = Task.Run(() =>
        {
            for (var i = 1; i < 300_000 && !stop.IsCancellationRequested; i++)
                timeline.Append(First, i * 0.010, 10);
        });

        var last = -1.0;
        var readings = 0;
        bool done;
        do
        {
            done = writer.IsCompleted;
            var frame = Math.Max(0, timeline.TotalFrames - 200);
            if (!timeline.TryLookup(frame, out var track, out var seconds))
                continue;

            Assert.Same(First, track);
            Assert.InRange(seconds, last, double.MaxValue);
            Assert.Equal(frame / 1000.0, seconds, 6);
            last = seconds;
            readings++;
        }
        while (!done);

        stop.Cancel();
        await writer;
        Assert.True(readings > 0);
    }
}
