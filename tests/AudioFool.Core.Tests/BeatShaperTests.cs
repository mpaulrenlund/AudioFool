using AudioFool.Core.Visualizer;

namespace AudioFool.Core.Tests;

public class BeatShaperTests
{
    private const int Rate = 44100;

    /// <summary>A frame's worth of samples at 60 frames a second, as projectM is handed them.</summary>
    private const int Chunk = 735;

    /// <summary>Stereo, the same on both channels: <paramref name="value"/> of the time in seconds.</summary>
    private static float[] Signal(double seconds, Func<double, double> value)
    {
        var frames = (int)(seconds * Rate);
        var samples = new float[frames * 2];
        for (var f = 0; f < frames; f++)
            samples[f * 2] = samples[f * 2 + 1] = (float)value((double)f / Rate);
        return samples;
    }

    /// <summary>Shapes the whole signal in chunks of <paramref name="chunk"/> frames; the output, stereo.</summary>
    private static float[] Shaped(float[] input, int intensity, int chunk = Chunk)
    {
        var shaper = new BeatShaper(Rate) { Intensity = intensity };
        var output = new float[input.Length];
        var frames = input.Length / 2;
        for (var from = 0; from < frames; from += chunk)
        {
            var count = Math.Min(chunk, frames - from);
            shaper.Shape(input, from, count);
            Array.Copy(shaper.Output, 0, output, from * 2, count * 2);
        }
        return output;
    }

    private static double Rms(float[] samples, double from, double to)
    {
        var sum = 0.0;
        var first = (int)(from * Rate);
        var last = (int)(to * Rate);
        for (var f = first; f < last; f++)
            sum += samples[f * 2] * (double)samples[f * 2];
        return Math.Sqrt(sum / (last - first));
    }

    /// <summary>The level of one frequency over a stretch (Goertzel).</summary>
    private static double LevelAt(float[] samples, double hz, double from, double to)
    {
        var first = (int)(from * Rate);
        var last = (int)(to * Rate);
        var k = 2 * Math.Cos(2 * Math.PI * hz / Rate);
        double s1 = 0, s2 = 0;
        for (var f = first; f < last; f++)
            (s2, s1) = (s1, samples[f * 2] + k * s1 - s2);
        return Math.Sqrt(s1 * s1 + s2 * s2 - k * s1 * s2) / (last - first);
    }

    /// <summary>A 100 Hz beat: loud for the first 100 ms of every 500 ms, quiet (a tenth) in between.</summary>
    private static double Beat(double t) =>
        (t % 0.5 < 0.1 ? 0.4 : 0.04) * Math.Sin(2 * Math.PI * 100 * t);

    [Theory]
    [InlineData(1)]
    [InlineData(5)]
    [InlineData(10)]
    public void A_steady_tone_comes_out_as_it_went_in(int intensity)
    {
        var input = Signal(4, t => 0.3 * Math.Sin(2 * Math.PI * 440 * t));

        var output = Shaped(input, intensity);

        Assert.InRange(Rms(output, 3, 4) / Rms(input, 3, 4), 0.85, 1.1);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(5)]
    [InlineData(10)]
    public void Nothing_comes_out_louder_than_it_went_in(int intensity)
    {
        var input = Signal(4, t => Beat(t) + 0.05 * Math.Sin(2 * Math.PI * 5000 * t));

        var output = Shaped(input, intensity);

        Assert.True(output.Max(Math.Abs) <= input.Max(Math.Abs) * 1.1,
            $"peak {output.Max(Math.Abs):0.000} out, {input.Max(Math.Abs):0.000} in");
    }

    [Fact]
    public void Beats_stand_out_more_the_higher_the_intensity()
    {
        var input = Signal(6, Beat);

        // On: the middle of a beat; off: the middle of the quiet between, over the last 2 s.
        double Contrast(float[] samples)
        {
            double on = 0, off = 0;
            for (var beat = 4.0; beat < 6; beat += 0.5)
            {
                on += Rms(samples, beat + 0.03, beat + 0.07);
                off += Rms(samples, beat + 0.28, beat + 0.32);
            }
            return on / off;
        }

        var unshaped = Contrast(input);
        var calm = Contrast(Shaped(input, 1));
        var usual = Contrast(Shaped(input, 5));
        var lively = Contrast(Shaped(input, 10));

        Assert.InRange(unshaped, 9, 11);
        Assert.True(calm < unshaped, $"intensity 1: {calm:0.0} against {unshaped:0.0} unshaped");
        Assert.True(usual > unshaped * 1.5, $"intensity 5: {usual:0.0} against {unshaped:0.0} unshaped");
        // Not much more: the quiet is never turned down past 1/16.
        Assert.True(lively > usual * 1.2, $"intensity 10: {lively:0.0} against {usual:0.0} at 5");
    }

    [Fact]
    public void Each_band_follows_its_own_beat()
    {
        // A bass beat with a steady 9 kHz tone over it: the tone isn't pumped by the beat.
        var input = Signal(6, t => Beat(t) + 0.05 * Math.Sin(2 * Math.PI * 9000 * t));

        var output = Shaped(input, 10);

        double on = 0, off = 0;
        for (var beat = 4.0; beat < 6; beat += 0.5)
        {
            on += LevelAt(output, 9000, beat + 0.03, beat + 0.07);
            off += LevelAt(output, 9000, beat + 0.28, beat + 0.32);
        }
        Assert.InRange(on / off, 0.8, 1.25);
    }

    [Fact]
    public void Silence_stays_silent()
    {
        var output = Shaped(Signal(2, _ => 0), 10);

        Assert.All(output, sample => Assert.Equal(0f, sample));
    }

    [Fact]
    public void The_output_stays_within_one()
    {
        var input = Signal(4, t => (t % 0.5 < 0.05 ? 1.0 : 0.001) * Math.Sin(2 * Math.PI * 60 * t));

        var output = Shaped(input, 10);

        Assert.All(output, sample => Assert.InRange(sample, -1f, 1f));
        Assert.DoesNotContain(output, float.IsNaN);
    }

    [Fact]
    public void Calls_continue_one_another()
    {
        // The same sound handed over in different pieces comes out the same.
        var input = Signal(3, Beat);

        Assert.Equal(Shaped(input, 7, chunk: 44100 * 3), Shaped(input, 7, chunk: 613));
    }

    [Fact]
    public void Intensity_is_kept_between_1_and_10()
    {
        var shaper = new BeatShaper(Rate) { Intensity = 40 };
        Assert.Equal(10, shaper.Intensity);
        shaper.Intensity = -3;
        Assert.Equal(1, shaper.Intensity);
        Assert.Equal(BeatShaper.DefaultSwing, BeatShaper.SwingFor(5));
    }
}
