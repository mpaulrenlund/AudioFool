using System.Runtime.InteropServices;
using AudioFool.Core.Playback;

namespace AudioFool.Core.Tests;

public class SampleTapTests
{
    /// <summary>1000 Hz stereo, so the ring is 1000 frames and a frame is a millisecond.</summary>
    private static readonly TapFormat Format = new(1000, 2);

    private static SampleTap On()
    {
        var tap = new SampleTap();
        tap.Reset(Format.SampleRate, Format.Channels);
        tap.Enable(true);
        return tap;
    }

    /// <summary>A block whose every sample is its frame's number, left and right.</summary>
    private static float[] Block(long firstFrame, int frames)
    {
        var samples = new float[frames * 2];
        for (var f = 0; f < frames; f++)
            samples[f * 2] = samples[f * 2 + 1] = firstFrame + f;
        return samples;
    }

    /// <summary>Writes from pinned memory, as the device's buffer is.</summary>
    private static void Write(SampleTap tap, long firstFrame, float[] samples)
    {
        var handle = GCHandle.Alloc(samples, GCHandleType.Pinned);
        try
        {
            tap.Write(firstFrame, handle.AddrOfPinnedObject(), samples.Length / 2);
        }
        finally
        {
            handle.Free();
        }
    }

    private static void Feed(SampleTap tap, long fromFrame, long toFrame, int blockFrames = 20)
    {
        for (var f = fromFrame; f < toFrame; f += blockFrames)
            Write(tap, f, Block(f, (int)Math.Min(blockFrames, toFrame - f)));
    }

    private static void AssertFrames(float[] read, long firstFrame)
    {
        for (var f = 0; f < read.Length / 2; f++)
        {
            Assert.Equal(firstFrame + f, read[f * 2]);
            Assert.Equal(firstFrame + f, read[f * 2 + 1]);
        }
    }

    [Fact]
    public void Off_until_turned_on()
    {
        var tap = new SampleTap();
        tap.Reset(Format.SampleRate, Format.Channels);
        Write(tap, 0, Block(0, 20));

        Assert.False(tap.IsEnabled);
        Assert.Null(tap.Format);
        Assert.False(tap.TryRead(20, new float[20], Format));
    }

    [Fact]
    public void Reads_back_the_frames_that_end_where_asked()
    {
        var tap = On();
        Feed(tap, 0, 300);

        var read = new float[100 * 2];
        Assert.True(tap.TryRead(250, read, Format));
        AssertFrames(read, 150);
    }

    [Fact]
    public void Reads_across_the_end_of_the_ring()
    {
        // 1000 frames held; frames 950-1049 sit at the ring's end and its start.
        var tap = On();
        Feed(tap, 0, 1100);

        var read = new float[100 * 2];
        Assert.True(tap.TryRead(1050, read, Format));
        AssertFrames(read, 950);
    }

    [Fact]
    public void Nothing_that_has_not_been_copied_yet()
    {
        var tap = On();
        Feed(tap, 0, 100);

        Assert.False(tap.TryRead(120, new float[20 * 2], Format));
        Assert.False(tap.TryRead(10, new float[20 * 2], Format));   // before the first frame
    }

    [Fact]
    public void Nothing_older_than_three_quarters_of_a_second()
    {
        var tap = On();
        Feed(tap, 0, 2000);

        Assert.False(tap.TryRead(1240, new float[20 * 2], Format));
        Assert.True(tap.TryRead(1270, new float[20 * 2], Format));
    }

    [Fact]
    public void At_most_half_a_second_at_once()
    {
        var tap = On();
        Feed(tap, 0, 1000);

        Assert.False(tap.TryRead(1000, new float[501 * 2], Format));
        Assert.True(tap.TryRead(1000, new float[500 * 2], Format));
    }

    [Fact]
    public void A_block_that_does_not_follow_on_starts_again()
    {
        var tap = On();
        Feed(tap, 0, 200);
        Feed(tap, 5000, 5100);

        Assert.False(tap.TryRead(200, new float[20 * 2], Format));
        var read = new float[50 * 2];
        Assert.True(tap.TryRead(5100, read, Format));
        AssertFrames(read, 5050);
    }

    [Fact]
    public void A_new_connection_starts_empty_at_its_layout()
    {
        var tap = On();
        Feed(tap, 0, 200);

        tap.Reset(48000, 2);
        Assert.Equal(new TapFormat(48000, 2), tap.Format);
        Assert.False(tap.TryRead(200, new float[20 * 2], Format));
        Assert.False(tap.TryRead(200, new float[20 * 2], new TapFormat(48000, 2)));
    }

    [Fact]
    public void Turning_off_lets_go_and_on_again_starts_empty()
    {
        var tap = On();
        Feed(tap, 0, 200);

        tap.Enable(false);
        Assert.Null(tap.Format);
        Assert.False(tap.TryRead(200, new float[20 * 2], Format));

        tap.Enable(true);
        Assert.False(tap.TryRead(200, new float[20 * 2], Format));
        Feed(tap, 200, 300);
        Assert.True(tap.TryRead(300, new float[20 * 2], Format));
    }

    [Fact]
    public void A_block_longer_than_the_ring_keeps_its_last_second()
    {
        var tap = On();
        Write(tap, 0, Block(0, 2500));

        var read = new float[100 * 2];
        Assert.True(tap.TryRead(2500, read, Format));
        AssertFrames(read, 2400);
    }

    [Fact]
    public void The_device_s_block_is_left_as_it_was()
    {
        var tap = On();
        var block = Block(0, 20);
        var before = (float[])block.Clone();

        Write(tap, 0, block);

        Assert.Equal(before, block);
    }

    [Fact]
    public void Copying_a_block_allocates_nothing()
    {
        var tap = On();
        var block = Block(0, 20);
        var handle = GCHandle.Alloc(block, GCHandleType.Pinned);
        try
        {
            var at = handle.AddrOfPinnedObject();
            tap.Write(0, at, 20);   // anything first-time out of the way

            var before = GC.GetAllocatedBytesForCurrentThread();
            for (var i = 1; i <= 10_000; i++)
                tap.Write(i * 20L, at, 20);
            Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
        }
        finally
        {
            handle.Free();
        }
    }

    [Fact]
    public async Task A_reader_on_another_thread_only_ever_sees_whole_runs_of_frames()
    {
        // The writer copies 10 ms blocks as fast as it can, round and round the
        // ring; the reader takes 100 ms windows ending 200 ms behind the newest.
        // Every window it is given must be 100 consecutive frames.
        var tap = On();
        Feed(tap, 0, 400, 10);
        var writer = Task.Run(() => Feed(tap, 400, 2_000_000, 10));

        var read = new float[100 * 2];
        var windows = 0;
        bool done;
        do
        {
            done = writer.IsCompleted;
            var end = tap.EndFrame - 200;
            if (end < 100 || !tap.TryRead(end, read, Format))
                continue;

            AssertFrames(read, end - 100);
            windows++;
        }
        while (!done);

        await writer;
        Assert.True(windows > 0);
        Assert.Equal(2_000_000, tap.EndFrame);
    }
}
