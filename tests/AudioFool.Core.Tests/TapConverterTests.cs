using AudioFool.Core.Analysis;
using AudioFool.Core.Playback;

namespace AudioFool.Core.Tests;

public class TapConverterTests
{
    private const int Out = 4096;

    private static TapConverter Converter(int rate) => new(new TapFormat(rate, 2), Out);

    /// <summary>Fills the converter's input with a sine on the left (and right, if asked).</summary>
    private static void FillSine(TapConverter converter, double hz, double amplitude, bool right = true)
    {
        var rate = converter.Format.SampleRate;
        for (var f = 0; f < converter.InputFrames; f++)
        {
            var v = (float)(amplitude * Math.Sin(2 * Math.PI * hz * f / rate + 0.3));
            converter.Input[f * 2] = v;
            converter.Input[f * 2 + 1] = right ? v : 0;
        }
    }

    /// <summary>The amplitude of <paramref name="hz"/> in the output, Hann-windowed.</summary>
    private static double Amplitude(TapConverter converter, double hz, int channel = 0)
    {
        double re = 0, im = 0, weights = 0;
        for (var n = 0; n < Out; n++)
        {
            var w = 0.5 - 0.5 * Math.Cos(2 * Math.PI * n / (Out - 1));
            var x = converter.Output[n * 2 + channel];
            var angle = 2 * Math.PI * hz * n / converter.OutputRate;
            re += w * x * Math.Cos(angle);
            im += w * x * Math.Sin(angle);
            weights += w;
        }
        return 2 * Math.Sqrt(re * re + im * im) / weights;
    }

    private static double Rms(TapConverter converter, int channel = 0)
    {
        var sum = 0.0;
        for (var n = 0; n < Out; n++)
            sum += converter.Output[n * 2 + channel] * (double)converter.Output[n * 2 + channel];
        return Math.Sqrt(sum / Out);
    }

    private static double Db(double ratio) => 20 * Math.Log10(ratio);

    [Theory]
    [InlineData(44100, 44100)]
    [InlineData(48000, 48000)]
    [InlineData(88200, 44100)]
    [InlineData(96000, 48000)]
    [InlineData(176400, 44100)]
    [InlineData(192000, 48000)]
    [InlineData(352800, 44100)]
    [InlineData(384000, 48000)]
    [InlineData(705600, 44100)]
    [InlineData(768000, 48000)]
    [InlineData(64000, 64000)]
    [InlineData(32000, 32000)]
    public void Comes_out_at_44_1_or_48_kHz(int rate, int expected)
    {
        Assert.Equal(expected, Converter(rate).OutputRate);
    }

    [Fact]
    public void At_44_1_kHz_the_window_is_its_newest_frames_as_they_were()
    {
        var converter = Converter(44100);
        for (var i = 0; i < converter.Input.Length; i++)
            converter.Input[i] = i * 0.0001f;

        converter.Convert();

        var skipped = (converter.InputFrames - Out) * 2;
        Assert.Equal(converter.Input[skipped..], converter.Output);
        Assert.False(converter.WasDop);
    }

    [Theory]
    [InlineData(352800, 1000)]
    [InlineData(352800, 19000)]
    [InlineData(88200, 19500)]
    [InlineData(96000, 1000)]
    [InlineData(192000, 19500)]
    [InlineData(768000, 10000)]
    public void Music_below_20_kHz_keeps_its_level(int rate, double hz)
    {
        var converter = Converter(rate);
        FillSine(converter, hz, 0.5);

        converter.Convert();

        Assert.InRange(Db(Amplitude(converter, hz) / 0.5), -0.05, 0.05);
    }

    [Theory]
    [InlineData(88200, 25000)]     // would fold to 19.1 kHz
    [InlineData(88200, 40000)]
    [InlineData(352800, 30000)]
    [InlineData(352800, 100000)]
    [InlineData(352800, 170000)]
    [InlineData(96000, 30000)]     // would fold to 18 kHz
    [InlineData(768000, 360000)]
    public void Nothing_above_it_folds_back_below_20_kHz(int rate, double hz)
    {
        var converter = Converter(rate);
        FillSine(converter, hz, 0.5);

        converter.Convert();

        Assert.True(Db(Rms(converter) * Math.Sqrt(2) / 0.5) < -80, $"{Db(Rms(converter) * Math.Sqrt(2) / 0.5):0.0} dB");
    }

    [Fact]
    public void The_channels_stay_apart()
    {
        var converter = Converter(352800);
        FillSine(converter, 1000, 0.5, right: false);

        converter.Convert();

        Assert.InRange(Db(Amplitude(converter, 1000, 0) / 0.5), -0.05, 0.05);
        Assert.Equal(0, Rms(converter, 1), 6);
    }

    // ---------------------------------------------------------------- DoP

    /// <summary>
    /// A 1 kHz sine as DSD: a second-order delta-sigma modulator at 64 or 128 times
    /// 44.1 kHz, packed into DoP words the way BASSDSD sends them (marker 0x05 /
    /// 0xFA alternating, 16 bits a word, oldest first), on both channels.
    /// </summary>
    private static void FillDop(TapConverter converter, double amplitude, double hz = 1000)
    {
        var bitRate = converter.Format.SampleRate * 16.0;
        double i1 = 0, i2 = 0, y = 0;
        long bit = 0;
        for (var f = 0; f < converter.InputFrames; f++)
        {
            var payload = 0;
            for (var b = 0; b < 16; b++, bit++)
            {
                var x = amplitude * Math.Sin(2 * Math.PI * hz * bit / bitRate + 0.3);
                i1 += x - y;
                i2 += i1 - y;
                y = i2 >= 0 ? 1 : -1;
                payload = (payload << 1) | (y > 0 ? 1 : 0);
            }

            var word = ((f % 2 == 0 ? 0x05 : 0xFA) << 16) | payload;
            var signed = (word & 0x800000) != 0 ? word - 0x1000000 : word;
            converter.Input[f * 2] = converter.Input[f * 2 + 1] = signed / 8388608f;
        }
    }

    [Theory]
    [InlineData(176400, 1000)]    // DSD64
    [InlineData(176400, 16000)]
    [InlineData(352800, 1000)]    // DSD128
    [InlineData(352800, 16000)]
    public void DSD_over_PCM_comes_back_as_its_music_at_PCM_level(int dopRate, double hz)
    {
        // A quarter modulation: half modulation is DSD's 0 dB, so this is -6 dB.
        // Flat into the treble, where BASSDSD's own conversion to 88.2 kHz is
        // 2 dB down at 16 kHz (measured on a made DSF in session 58).
        var converter = Converter(dopRate);
        FillDop(converter, 0.25, hz);

        converter.Convert();

        Assert.True(converter.WasDop);
        Assert.Equal(44100, converter.OutputRate);
        Assert.InRange(Db(Amplitude(converter, hz) / 0.5), -0.2, 0.2);
        Assert.InRange(Db(Amplitude(converter, hz, 1) / 0.5), -0.2, 0.2);
    }

    [Theory]
    [InlineData(176400)]
    [InlineData(352800)]
    public void DSD_s_noise_above_hearing_is_left_out(int dopRate)
    {
        // What isn't the sine is the modulator's noise below 20 kHz and whatever
        // got through the filters: noise shaping puts nearly all of it far above
        // 20 kHz, so a leak would show here.
        var converter = Converter(dopRate);
        FillDop(converter, 0.25);
        converter.Convert();

        var amplitude = Amplitude(converter, 1000);
        double re = 0, im = 0, weights = 0;
        for (var n = 0; n < Out; n++)
        {
            var w = 0.5 - 0.5 * Math.Cos(2 * Math.PI * n / (Out - 1));
            var angle = 2 * Math.PI * 1000 * n / 44100;
            re += w * converter.Output[n * 2] * Math.Cos(angle);
            im += w * converter.Output[n * 2] * Math.Sin(angle);
            weights += w;
        }
        re = 2 * re / weights;
        im = 2 * im / weights;

        // Subtract the sine, away from the window's edges, and see what is left.
        var residual = 0.0;
        var counted = 0;
        for (var n = Out / 8; n < Out * 7 / 8; n++)
        {
            var angle = 2 * Math.PI * 1000 * n / 44100;
            var left = converter.Output[n * 2] - (re * Math.Cos(angle) + im * Math.Sin(angle));
            residual += left * left;
            counted++;
        }
        var residualDb = Db(Math.Sqrt(residual / counted) * Math.Sqrt(2) / amplitude);

        Assert.True(residualDb < -60, $"{residualDb:0.0} dB under the sine");
    }

    [Fact]
    public void DSD_s_idle_pattern_is_silence()
    {
        var converter = Converter(176400);
        for (var f = 0; f < converter.InputFrames; f++)
        {
            var word = ((f % 2 == 0 ? 0x05 : 0xFA) << 16) | 0x6969;
            var signed = (word & 0x800000) != 0 ? word - 0x1000000 : word;
            converter.Input[f * 2] = converter.Input[f * 2 + 1] = signed / 8388608f;
        }

        converter.Convert();

        Assert.True(converter.WasDop);
        Assert.True(converter.Output.All(s => Math.Abs(s) < 1e-3));
    }

    [Fact]
    public void PCM_at_a_DoP_rate_is_not_taken_for_DoP()
    {
        var converter = Converter(176400);
        FillSine(converter, 1000, 0.9);

        converter.Convert();

        Assert.False(converter.WasDop);
        Assert.InRange(Db(Amplitude(converter, 1000) / 0.9), -0.05, 0.05);
    }

    [Fact]
    public void Converting_allocates_nothing()
    {
        var converter = Converter(352800);
        FillDop(converter, 0.25);
        converter.Convert();

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 20; i++)
            converter.Convert();
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }
}
