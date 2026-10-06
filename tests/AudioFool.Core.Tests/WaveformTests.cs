using AudioFool.Core.Analysis;

namespace AudioFool.Core.Tests;

/// <summary>The seekbar waveform's levels, fed synthetic audio.</summary>
public class WaveformTests
{
    [Fact]
    public void A_steady_tone_is_level_at_full_height()
    {
        var levels = Feed(44100, 2, seconds: 10, columns: 100, _ => 0.5);

        Assert.Equal(100, levels.Length);
        Assert.All(levels, l => Assert.Equal(1, l, 3));
    }

    [Fact]
    public void A_quiet_half_then_a_loud_half_reads_as_quiet_then_loud()
    {
        // Ten times quieter, so a tenth of the height: the scale is linear.
        var levels = Feed(44100, 2, seconds: 10, columns: 100, t => t < 5 ? 0.05 : 0.5);

        Assert.All(levels[..48], l => Assert.Equal(0.1, l, 2));
        Assert.All(levels[52..], l => Assert.Equal(1, l, 3));
    }

    [Fact]
    public void Silence_gives_zeros_not_nan()
    {
        var levels = Feed(44100, 2, seconds: 3, columns: 50, _ => 0);

        Assert.Equal(50, levels.Length);
        Assert.All(levels, l => Assert.Equal(0, l));
    }

    [Fact]
    public void An_unknown_length_still_fills_every_column()
    {
        var levels = Feed(44100, 1, seconds: 30, columns: 200, t => t < 15 ? 0.2 : 0.4, knownLength: false);

        Assert.Equal(200, levels.Length);
        Assert.Equal(0.5, levels[50], 2);
        Assert.Equal(1, levels[150], 2);
    }

    [Fact]
    public void A_length_estimate_that_is_short_still_spreads_evenly()
    {
        // A VBR MP3's length is an estimate; here the file runs 20% longer.
        const int rate = 8000;
        var levels = new WaveformLevels(1, totalFrames: rate * 10, columns: 100);
        var samples = Signal(rate, 1, seconds: 12, t => t < 6 ? 0.1 : 0.3);
        levels.Add(samples);

        var result = levels.Finish();

        Assert.Equal(100, result.Length);
        Assert.Equal(1.0 / 3, result[40], 2);
        Assert.Equal(1, result[60], 3);
    }

    [Fact]
    public void A_track_shorter_than_the_columns_gives_fewer_columns()
    {
        var levels = new WaveformLevels(2, totalFrames: 10, columns: 600);
        levels.Add(new float[20]);

        Assert.Equal(10, levels.Finish().Length);
    }

    [Fact]
    public void Nothing_read_gives_nothing() =>
        Assert.Empty(new WaveformLevels(2, totalFrames: 0, columns: 600).Finish());

    [Fact]
    public void Channels_are_measured_together()
    {
        // Left loud, right silent, throughout: still level, not halved in places.
        var levels = new WaveformLevels(2, totalFrames: 8000, columns: 10);
        var samples = new float[16000];
        for (var i = 0; i < samples.Length; i += 2)
            samples[i] = 0.5f;
        levels.Add(samples);

        Assert.All(levels.Finish(), l => Assert.Equal(1, l, 3));
    }

    [Fact]
    public void A_missing_file_gives_no_waveform() =>
        Assert.Null(Waveform.Read(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".flac")));

    private static float[] Feed(int rate, int channels, int seconds, int columns, Func<double, double> amplitude,
        bool knownLength = true)
    {
        var levels = new WaveformLevels(channels, knownLength ? (long)rate * seconds : 0, columns);
        var samples = Signal(rate, channels, seconds, amplitude);

        // In uneven pieces, as a decoder hands them over.
        for (var at = 0; at < samples.Length; at += 7777 * channels)
            levels.Add(samples.AsSpan(at, Math.Min(7777 * channels, samples.Length - at)));

        return levels.Finish();
    }

    /// <summary>A 440 Hz sine whose amplitude follows <paramref name="amplitude"/>(seconds).</summary>
    private static float[] Signal(int rate, int channels, int seconds, Func<double, double> amplitude)
    {
        var samples = new float[rate * seconds * channels];
        for (var i = 0; i < rate * seconds; i++)
        {
            var t = (double)i / rate;
            var value = (float)(amplitude(t) * Math.Sin(2 * Math.PI * 440 * t));
            for (var c = 0; c < channels; c++)
                samples[(i * channels) + c] = value;
        }

        return samples;
    }
}
