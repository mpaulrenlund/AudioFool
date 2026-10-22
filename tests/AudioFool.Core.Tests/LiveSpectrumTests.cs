using AudioFool.Core.Analysis;

namespace AudioFool.Core.Tests;

public class LiveSpectrumTests
{
    private const double Frame = 1.0 / 60;

    /// <summary>A stereo window of tones (Hz, amplitude), the same on both channels, starting at sample <paramref name="start"/>.</summary>
    private static float[] Window(int rate, int frames, long start, params (double Hz, double Amplitude)[] tones)
    {
        var samples = new float[frames * 2];
        for (var f = 0; f < frames; f++)
        {
            var v = 0.0;
            foreach (var (hz, amplitude) in tones)
                v += amplitude * Math.Sin(2 * Math.PI * hz * (start + f) / rate);
            samples[f * 2] = samples[f * 2 + 1] = (float)v;
        }
        return samples;
    }

    /// <summary>Plays tones into the spectrum for <paramref name="seconds"/> at 60 frames a second.</summary>
    private static void Play(LiveSpectrum spectrum, double seconds, ref long position, params (double Hz, double Amplitude)[] tones)
    {
        var step = (int)(spectrum.SampleRate * Frame);
        for (var t = 0.0; t < seconds; t += Frame)
        {
            spectrum.Update(Window(spectrum.SampleRate, spectrum.FftSize, position, tones), 2, Frame);
            position += step;
        }
    }

    private static int BandOf(LiveSpectrum spectrum, double hz)
    {
        for (var b = 0; b < spectrum.BandCount; b++)
            if (hz >= spectrum.BandEdges[b] && hz < spectrum.BandEdges[b + 1])
                return b;
        return -1;
    }

    private static double Centre(LiveSpectrum spectrum, int band) => Math.Sqrt(spectrum.BandEdges[band] * spectrum.BandEdges[band + 1]);

    [Fact]
    public void Bands_are_spread_evenly_in_pitch_from_20_Hz_to_20_kHz()
    {
        var spectrum = new LiveSpectrum(44100, 2048, 32);

        Assert.Equal(20, spectrum.BandEdges[0], 6);
        Assert.Equal(20000, spectrum.BandEdges[^1], 6);
        var ratio = spectrum.BandEdges[1] / spectrum.BandEdges[0];
        for (var b = 1; b < spectrum.BandCount; b++)
            Assert.Equal(ratio, spectrum.BandEdges[b + 1] / spectrum.BandEdges[b], 9);
    }

    [Theory]
    [InlineData(44100, 1000, 0)]
    [InlineData(44100, 5000, 0)]
    [InlineData(44100, 1000, -20)]
    [InlineData(48000, 3000, -6)]
    [InlineData(48000, 12000, -40)]
    public void A_sine_reads_its_level_in_its_band(int rate, double nearHz, double db)
    {
        var spectrum = new LiveSpectrum(rate);
        var band = BandOf(spectrum, nearHz);
        var amplitude = Math.Pow(10, db / 20);

        spectrum.Update(Window(rate, spectrum.FftSize, 0, (Centre(spectrum, band), amplitude)), 2, Frame);

        Assert.InRange(spectrum.BandDb[band], db - 0.5, db + 0.5);
        Assert.InRange(spectrum.LevelDb, db - 0.5, db + 0.5);
        for (var b = 0; b < spectrum.BandCount; b++)
            if (Math.Abs(b - band) > 2)
                Assert.True(spectrum.BandDb[b] < db - 40, $"band {b} at {spectrum.BandDb[b]:0.0} dB");
    }

    [Fact]
    public void Every_band_hears_a_tone_in_it_from_300_Hz_up()
    {
        var spectrum = new LiveSpectrum(44100);
        for (var band = BandOf(spectrum, 300); band < spectrum.BandCount; band++)
        {
            spectrum.Update(Window(44100, spectrum.FftSize, 0, (Centre(spectrum, band), 0.5)), 2, Frame);
            var loudest = Array.IndexOf(spectrum.BandDb, spectrum.BandDb.Max());
            Assert.Equal(band, loudest);
        }
    }

    [Fact]
    public void The_lowest_bands_are_never_empty()
    {
        // Narrower than a bin, they borrow the nearest one: a 30 Hz tone shows in
        // the bands around 30 Hz instead of nowhere.
        var spectrum = new LiveSpectrum(44100);
        spectrum.Update(Window(44100, spectrum.FftSize, 0, (30, 0.5)), 2, Frame);

        Assert.True(spectrum.BandDb[BandOf(spectrum, 30)] > -20);
    }

    [Fact]
    public void Noise_reads_its_power()
    {
        // Uniform noise of amplitude a has a mean square of a^2 / 3, spread evenly to
        // 22.05 kHz. Against a full-scale sine as 0 dB (whose mean square is 1/2),
        // that reads twice as much: AES17's dBFS, as Analyze measures.
        var random = new Random(7);
        var spectrum = new LiveSpectrum(44100);
        var samples = new float[spectrum.FftSize * 2];
        for (var i = 0; i < samples.Length; i++)
            samples[i] = (float)(random.NextDouble() * 2 - 1) * 0.5f;

        spectrum.Update(samples, 2, Frame);

        var expected = 10 * Math.Log10(2 * 0.25 / 3 * (20000 - 20) / 22050);
        Assert.InRange(spectrum.LevelDb, expected - 0.5, expected + 0.5);
    }

    [Fact]
    public void Silence_is_the_floor_and_bass_mid_treble_nothing()
    {
        var spectrum = new LiveSpectrum(44100);
        spectrum.Update(new float[spectrum.FftSize * 2], 2, Frame);

        Assert.All(spectrum.BandDb, db => Assert.Equal(LiveSpectrum.FloorDb, db));
        Assert.Equal(LiveSpectrum.FloorDb, spectrum.LevelDb);
        Assert.Equal(0, spectrum.Bass);
        Assert.Equal(0, spectrum.Mid);
        Assert.Equal(0, spectrum.Treble);
    }

    [Fact]
    public void Levels_rise_quickly_and_fall_slowly()
    {
        var spectrum = new LiveSpectrum(44100);
        var band = BandOf(spectrum, 1000);
        var tone = (Centre(spectrum, band), 1.0);
        long position = 0;

        // Four frames in, nearly all the way up from the floor.
        Play(spectrum, 4 * Frame, ref position, tone);
        Assert.True(spectrum.Bands[band] > -10, $"{spectrum.Bands[band]:0.0} dB after 4 frames");

        Play(spectrum, 1, ref position, tone);
        var full = spectrum.Bands[band];

        // Four frames of silence later, still well up.
        Play(spectrum, 4 * Frame, ref position);
        Assert.True(spectrum.Bands[band] > full - 40, $"{spectrum.Bands[band]:0.0} dB after 4 silent frames");
    }

    [Fact]
    public void Higher_intensity_falls_faster()
    {
        double FallAfter(int intensity)
        {
            var spectrum = new LiveSpectrum(44100) { Intensity = intensity };
            var band = BandOf(spectrum, 1000);
            long position = 0;
            Play(spectrum, 1, ref position, (Centre(spectrum, band), 1.0));
            Play(spectrum, 10 * Frame, ref position);
            return spectrum.Bands[band];
        }

        Assert.True(FallAfter(10) < FallAfter(5));
        Assert.True(FallAfter(5) < FallAfter(1));
    }

    [Fact]
    public void Intensity_stays_between_1_and_10()
    {
        var spectrum = new LiveSpectrum(44100) { Intensity = 0 };
        Assert.Equal(1, spectrum.Intensity);
        spectrum.Intensity = 42;
        Assert.Equal(10, spectrum.Intensity);
        Assert.Equal(5, new LiveSpectrum(44100).Intensity);
    }

    [Fact]
    public void Bass_mid_and_treble_settle_at_usual_and_a_louder_bass_shows_only_in_bass()
    {
        var spectrum = new LiveSpectrum(44100);
        long position = 0;
        Play(spectrum, 4, ref position, (60, 0.2), (1000, 0.2), (8000, 0.2));

        Assert.InRange(spectrum.Bass, 0.95, 1.05);
        Assert.InRange(spectrum.Mid, 0.95, 1.05);
        Assert.InRange(spectrum.Treble, 0.95, 1.05);

        // The bass four times the power: well over usual, the rest unmoved.
        Play(spectrum, 0.2, ref position, (60, 0.4), (1000, 0.2), (8000, 0.2));
        Assert.True(spectrum.Bass > 2, $"bass {spectrum.Bass:0.00}");
        Assert.InRange(spectrum.Mid, 0.9, 1.1);
        Assert.InRange(spectrum.Treble, 0.9, 1.1);
    }

    [Fact]
    public void Higher_intensity_swings_further()
    {
        double BassAfterABeat(int intensity)
        {
            var spectrum = new LiveSpectrum(44100) { Intensity = intensity };
            long position = 0;
            Play(spectrum, 4, ref position, (60, 0.2), (1000, 0.2));
            Play(spectrum, 0.3, ref position, (60, 0.4), (1000, 0.2));
            return spectrum.Bass;
        }

        Assert.True(BassAfterABeat(10) > BassAfterABeat(5));
        Assert.True(BassAfterABeat(5) > BassAfterABeat(1));
    }

    [Fact]
    public void An_update_allocates_nothing()
    {
        var spectrum = new LiveSpectrum(44100);
        var window = Window(44100, spectrum.FftSize, 0, (1000, 0.5));
        spectrum.Update(window, 2, Frame);

        // The runtime itself sometimes allocates on this thread mid-measure (the
        // JIT moving Update to optimised code on the first run after a build), so
        // measure several rounds: an allocation of Update's own shows in every
        // round, the runtime's in one or two.
        var fewest = long.MaxValue;
        for (var round = 0; round < 5; round++)
        {
            var before = GC.GetAllocatedBytesForCurrentThread();
            for (var i = 0; i < 100; i++)
                spectrum.Update(window, 2, Frame);
            fewest = Math.Min(fewest, GC.GetAllocatedBytesForCurrentThread() - before);
        }

        Assert.Equal(0, fewest);
    }
}
