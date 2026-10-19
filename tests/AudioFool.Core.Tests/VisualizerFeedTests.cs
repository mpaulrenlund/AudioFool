using AudioFool.Core.Analysis;
using AudioFool.Core.Playback;

namespace AudioFool.Core.Tests;

public class VisualizerFeedTests
{
    private const double Frame = 1.0 / 60;

    /// <summary>A stand-in tap: a 1 kHz sine at its layout, when it is willing to give one.</summary>
    private sealed class FakeTap
    {
        public TapFormat? Format = new TapFormat(88200, 2);
        public bool Gives = true;
        public int LastAhead = -1;
        public int Reads;

        public bool Read(float[] destination, TapFormat format, int ahead)
        {
            Reads++;
            LastAhead = ahead;
            if (!Gives)
                return false;
            for (var f = 0; f < destination.Length / format.Channels; f++)
                for (var c = 0; c < format.Channels; c++)
                    destination[f * format.Channels + c] = (float)(0.5 * Math.Sin(2 * Math.PI * 1000 * f / format.SampleRate));
            return true;
        }
    }

    private static (VisualizerFeed Feed, FakeTap Tap) Make()
    {
        var tap = new FakeTap();
        return (new VisualizerFeed(() => tap.Format, tap.Read), tap);
    }

    private static int BandOf(LiveSpectrum spectrum, double hz)
    {
        for (var b = 0; b < spectrum.BandCount; b++)
            if (hz >= spectrum.BandEdges[b] && hz < spectrum.BandEdges[b + 1])
                return b;
        return -1;
    }

    [Fact]
    public void What_is_heard_is_converted_and_analysed()
    {
        var (feed, _) = Make();
        for (var i = 0; i < 10; i++)
            feed.Next(Frame, listening: true);

        Assert.True(feed.Heard);
        Assert.Equal(44100, feed.Spectrum!.SampleRate);
        Assert.InRange(feed.Spectrum.BandDb[BandOf(feed.Spectrum, 1000)], -7, -5);   // a 0.5 sine is -6 dB
    }

    [Fact]
    public void The_window_is_centred_on_what_is_heard()
    {
        // Half of 2,048 converted frames ahead, in the tap's frames: twice as many at 88.2 kHz.
        var (feed, tap) = Make();
        feed.Next(Frame, listening: true);

        Assert.Equal(1024 * 2, tap.LastAhead);
    }

    [Fact]
    public void Paused_it_feeds_silence_and_the_picture_sinks()
    {
        var (feed, tap) = Make();
        for (var i = 0; i < 30; i++)
            feed.Next(Frame, listening: true);
        var playing = feed.Spectrum!.Bands[BandOf(feed.Spectrum, 1000)];
        var reads = tap.Reads;

        for (var i = 0; i < 60; i++)
            feed.Next(Frame, listening: false);

        Assert.False(feed.Heard);
        Assert.Equal(reads, tap.Reads);   // not even asked
        Assert.True(feed.Spectrum.Bands[BandOf(feed.Spectrum, 1000)] < playing - 40);
    }

    [Fact]
    public void Nothing_to_read_is_silence_too()
    {
        var (feed, tap) = Make();
        feed.Next(Frame, listening: true);
        tap.Gives = false;

        feed.Next(Frame, listening: true);

        Assert.False(feed.Heard);
    }

    [Fact]
    public void Nothing_until_the_tap_says_what_it_holds()
    {
        var (feed, tap) = Make();
        tap.Format = null;

        feed.Next(Frame, listening: true);

        Assert.Null(feed.Spectrum);
        Assert.Equal(0, tap.Reads);
    }

    [Fact]
    public void A_new_layout_gets_a_new_converter_and_keeps_the_intensity()
    {
        var (feed, tap) = Make();
        feed.Intensity = 8;
        feed.Next(Frame, listening: true);
        var at44 = feed.Spectrum;

        tap.Format = new TapFormat(352800, 2);   // still 44.1 kHz once converted: the same analysis
        feed.Next(Frame, listening: true);
        Assert.Same(at44, feed.Spectrum);
        Assert.Equal(1024 * 8, tap.LastAhead);

        tap.Format = new TapFormat(96000, 2);    // 48 kHz: a new one, at the same intensity
        feed.Next(Frame, listening: true);
        Assert.NotSame(at44, feed.Spectrum);
        Assert.Equal(48000, feed.Spectrum!.SampleRate);
        Assert.Equal(8, feed.Spectrum.Intensity);
    }

    [Fact]
    public void Intensity_stays_between_1_and_10()
    {
        var (feed, _) = Make();
        feed.Intensity = 0;
        Assert.Equal(1, feed.Intensity);
        feed.Intensity = 11;
        Assert.Equal(10, feed.Intensity);
    }

    [Fact]
    public void A_frame_allocates_nothing()
    {
        var (feed, _) = Make();
        feed.Next(Frame, listening: true);
        feed.Next(Frame, listening: false);

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 50; i++)
            feed.Next(Frame, listening: i % 2 == 0);
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }
}
