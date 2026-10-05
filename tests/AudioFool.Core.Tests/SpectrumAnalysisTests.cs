using AudioFool.Core.Analysis;

namespace AudioFool.Core.Tests;

/// <summary>The FFT and the accumulator, fed synthetic audio.</summary>
public class SpectrumAnalysisTests
{
    [Fact]
    public void Fft_puts_a_sine_in_its_own_bin()
    {
        var fft = new Fft(256);
        var re = new double[256];
        var im = new double[256];
        for (var i = 0; i < 256; i++)
            re[i] = Math.Sin(2 * Math.PI * 32 * i / 256);

        fft.Transform(re, im);

        var magnitudes = Enumerable.Range(0, 129).Select(k => Math.Sqrt((re[k] * re[k]) + (im[k] * im[k]))).ToArray();
        Assert.Equal(32, Array.IndexOf(magnitudes, magnitudes.Max()));
        Assert.Equal(128, magnitudes[32], 6);
        Assert.True(magnitudes.Where((_, k) => k != 32).All(m => m < 1e-9));
    }

    [Fact]
    public void Fft_refuses_a_size_that_is_not_a_power_of_two() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new Fft(1000));

    [Theory]
    [InlineData(44100, 4096)]
    [InlineData(48000, 4096)]
    [InlineData(96000, 8192)]
    [InlineData(176400, 16384)]
    [InlineData(192000, 16384)]
    public void Fft_size_keeps_about_eleven_hertz_per_bin(int rate, int size) =>
        Assert.Equal(size, SpectrumAccumulator.FftSizeFor(rate));

    [Fact]
    public void A_full_scale_sine_reads_0_dB_in_its_bin_and_nothing_elsewhere()
    {
        const int rate = 44100;
        var hz = 100 * rate / 4096.0; // exactly on bin 100
        var analysis = Feed(rate, 2, seconds: 3, (t, _) => Math.Sin(2 * Math.PI * hz * t));

        Assert.Equal(0, analysis.Average[100], 1);
        Assert.Equal(0, analysis.Peak[100], 1);
        Assert.True(analysis.Average[analysis.BinOf(10000)] < -100);
        Assert.Equal(0, analysis.PeakSampleDb, 1);
        Assert.Equal(3, analysis.Duration.TotalSeconds, 2);
    }

    [Fact]
    public void Channels_are_averaged_by_power_so_opposite_phases_do_not_cancel()
    {
        const int rate = 44100;
        var hz = 200 * rate / 4096.0;
        var analysis = Feed(rate, 2, seconds: 2, (t, c) => (c == 0 ? 1 : -1) * 0.5 * Math.Sin(2 * Math.PI * hz * t));

        // Half amplitude is -6 dB in each channel, so -6 dB on average too.
        Assert.Equal(-6.02, analysis.Average[200], 1);
    }

    [Fact]
    public void The_picture_has_a_row_per_band_and_shows_the_tone_in_its_row()
    {
        const int rate = 44100;
        var analysis = Feed(rate, 1, seconds: 10, (t, _) => 0.5 * Math.Sin(2 * Math.PI * 5000 * t));

        Assert.Equal(800, analysis.Columns);
        Assert.Equal(512, analysis.Rows);
        Assert.Equal(800 * 512, analysis.Picture.Length);

        var expectedRow = (int)(5000 / (rate / 2.0) * 512);
        for (var column = 0; column < analysis.Columns; column += 97)
        {
            var slice = analysis.Picture.AsSpan(column * 512, 512).ToArray();
            Assert.InRange(Array.IndexOf(slice, slice.Max()), expectedRow - 1, expectedRow + 1);
        }
    }

    [Fact]
    public void A_track_shorter_than_one_transform_still_gets_a_picture()
    {
        var analysis = Feed(44100, 2, seconds: 0.05, (t, _) => 0.1 * Math.Sin(2 * Math.PI * 1000 * t));

        Assert.True(analysis.Average.Max() > -60);
        Assert.DoesNotContain(analysis.Picture, v => float.IsNaN(v));
        Assert.True(analysis.Picture.Max() > -100);
    }

    [Fact]
    public void Silence_reads_as_the_floor()
    {
        var analysis = Feed(44100, 2, seconds: 1, (_, _) => 0);

        Assert.All(analysis.Average, v => Assert.Equal(SpectrumAccumulator.FloorDb, v));
        Assert.Equal(SpectrumAccumulator.FloorDb, analysis.PeakSampleDb);
    }

    [Fact]
    public void Sixteen_bit_values_in_a_24_bit_file_use_16_bits()
    {
        var analysis = Feed(44100, 2, seconds: 1, (t, _) => Quantise(0.5 * Math.Sin(2 * Math.PI * 440 * t), 16), checkBits: true);
        Assert.Equal(16, analysis.UsedBits);
    }

    [Fact]
    public void Real_24_bit_values_use_24_bits()
    {
        var analysis = Feed(44100, 2, seconds: 1, (t, _) => Quantise(0.5 * Math.Sin(2 * Math.PI * 440 * t), 24), checkBits: true);
        Assert.Equal(24, analysis.UsedBits);
    }

    [Fact]
    public void Sixteen_bit_music_with_a_24_bit_fade_uses_24_bits_but_mostly_16()
    {
        // Nine seconds on the 16-bit grid, then a one-second 24-bit fade.
        var analysis = Feed(44100, 1, seconds: 10,
            (t, _) => Quantise(0.5 * Math.Sin(2 * Math.PI * 440 * t), t < 9 ? 16 : 24), checkBits: true);

        Assert.Equal(24, analysis.UsedBits);
        Assert.InRange(analysis.ExtraBitsShare!.Value, 0.08, 0.12);
    }

    [Fact]
    public void Real_24_bit_values_use_the_extra_bits_in_nearly_every_sample()
    {
        var analysis = Feed(44100, 1, 1, (t, _) => Quantise(0.5 * Math.Sin(2 * Math.PI * 440 * t), 24), checkBits: true);
        Assert.True(analysis.ExtraBitsShare > 0.98);
    }

    [Fact]
    public void A_restart_keeps_the_join_between_slices_out_of_the_spectrum()
    {
        // Two slices of a quiet tone whose phase jumps at the join: heard across
        // the join it is a click, which would put noise in every band.
        const int rate = 44100;
        var hz = 100 * rate / 4096.0;
        float[] Slice(double phase) => [.. Enumerable.Range(0, rate)
            .Select(i => (float)(0.5 * Math.Sin((2 * Math.PI * hz * i / rate) + phase)))];

        SpectrumAnalysis Run(bool restart)
        {
            var accumulator = new SpectrumAccumulator(rate, 1, 2 * rate, 16, 16);
            accumulator.Add(Slice(0));
            if (restart)
                accumulator.Restart();
            accumulator.Add(Slice(Math.PI / 2));
            return accumulator.Finish();
        }

        var top = Run(restart: true).BinOf(18000);
        Assert.True(Run(restart: true).Average[top] < -140);
        Assert.True(Run(restart: false).Average[top] > -120);
    }

    [Fact]
    public void Slices_spread_evenly_and_a_short_track_is_read_whole()
    {
        var sampling = new AnalysisSampling(3, TimeSpan.FromSeconds(10));
        var slice = 441000L;

        Assert.Equal([2425500L, 5071500L, 7717500L], sampling.StartsFor(44100L * 240, slice));
        Assert.Null(sampling.StartsFor(44100L * 60, slice));
    }

    [Fact]
    public void Bits_are_not_counted_unless_asked_or_when_samples_are_off_the_24_bit_grid()
    {
        Assert.Null(Feed(44100, 1, 1, (t, _) => Quantise(Math.Sin(t * 1000), 16)).UsedBits);
        Assert.Null(Feed(44100, 1, 1, (t, _) => 0.3 * Math.Sin(t * 1000), checkBits: true).UsedBits);
    }

    /// <summary>A sample on an n-bit grid, as float, as BASS hands back a lossless decode.</summary>
    private static double Quantise(double x, int bits)
    {
        var scale = Math.Pow(2, bits - 1);
        return (float)(Math.Round(x * scale) / scale);
    }

    internal static SpectrumAnalysis Feed(int rate, int channels, double seconds, Func<double, int, double> sample, bool checkBits = false)
    {
        var frames = (long)(rate * seconds);
        var accumulator = new SpectrumAccumulator(rate, channels, frames, checkBits: checkBits);
        var buffer = new float[4096 * channels];
        long done = 0;
        while (done < frames)
        {
            var count = (int)Math.Min(4096, frames - done);
            for (var f = 0; f < count; f++)
            {
                var t = (double)(done + f) / rate;
                for (var c = 0; c < channels; c++)
                    buffer[(f * channels) + c] = (float)sample(t, c);
            }

            accumulator.Add(buffer.AsSpan(0, count * channels));
            done += count;
        }

        return accumulator.Finish();
    }
}
