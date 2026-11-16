using AudioFool.Core.Equalizer;

namespace AudioFool.Core.Tests;

/// <summary>
/// The built-in equalizer's Core pieces (session 79): the filters, the processor
/// that runs them on the device's thread, and the Peace and Equalizer APO readers.
/// </summary>
public class EqualizerTests
{
    private static readonly string Fixtures = Path.Combine(AppContext.BaseDirectory, "TestData", "eq");

    private static EqPreset Preset(double preampDb, params EqBand[] bands) =>
        new("test", preampDb, bands, [], EqPresetFormat.EqualizerApo);

    private static float[] Sine(double frequency, double amplitude, int sampleRate, double seconds, int channels = 2)
    {
        var frames = (int)(sampleRate * seconds);
        var samples = new float[frames * channels];
        for (var f = 0; f < frames; f++)
        {
            var v = (float)(amplitude * Math.Sin(2 * Math.PI * frequency * f / sampleRate));
            for (var c = 0; c < channels; c++)
                samples[f * channels + c] = v;
        }
        return samples;
    }

    private static float[] Noise(int count, int seed = 7)
    {
        var random = new Random(seed);
        var samples = new float[count];
        for (var i = 0; i < count; i++)
            samples[i] = (float)(random.NextDouble() * 2 - 1);
        return samples;
    }

    // ---------- the filters ----------

    [Fact]
    public void Peak_coefficients_match_the_cookbook()
    {
        // At a quarter of the rate cos w0 = 0 and sin w0 = 1, so the cookbook's
        // formulas reduce to numbers worked out by hand.
        var filter = Biquad.Design(EqBand.Peak(12_000, 6, 1), 48_000)!.Value;

        var a = Math.Pow(10, 6.0 / 40);
        var alpha = 0.5;
        var a0 = 1 + alpha / a;
        Assert.Equal((1 + alpha * a) / a0, filter.B0, 12);
        Assert.Equal(0, filter.B1, 12);
        Assert.Equal((1 - alpha * a) / a0, filter.B2, 12);
        Assert.Equal(0, filter.A1, 12);
        Assert.Equal((1 - alpha / a) / a0, filter.A2, 12);
    }

    [Theory]
    [InlineData(10, 2.0, 1.41, 192_000, 1_000)]
    [InlineData(1_000, -6.0, 1.41, 44_100, 20)]
    [InlineData(16_000, 3.5, 1.41, 48_000, 800)]
    [InlineData(20_000, 1.5, 2.99, 44_100, 1_000)]
    [InlineData(83, 1.76, 1.41, 352_800, 5_000)]
    public void A_peak_raises_its_frequency_by_its_gain_and_leaves_the_far_away_alone(
        double frequency, double gain, double q, int rate, double farAway)
    {
        var filter = Biquad.Design(EqBand.Peak(frequency, gain, q), rate)!.Value;
        Assert.Equal(gain, filter.GainDbAt(frequency, rate), 2);
        Assert.InRange(filter.GainDbAt(farAway, rate), -0.1, 0.1);
    }

    [Fact]
    public void A_sine_through_a_six_dB_band_comes_out_at_twice_the_amplitude()
    {
        const int rate = 48_000;
        var design = EqDesign.For(Preset(0, EqBand.Peak(1_000, 20 * Math.Log10(2), 1.41)), rate);
        var samples = Sine(1_000, 0.25, rate, 1.0);

        new EqProcessor(2).Process(design, samples);

        var settled = samples.AsSpan(samples.Length / 2);
        var peak = 0f;
        foreach (var s in settled)
            peak = Math.Max(peak, Math.Abs(s));
        Assert.Equal(0.5, peak, 3);
    }

    [Theory]
    [InlineData(EqFilterKind.LowShelf, 5, 10_000)]
    [InlineData(EqFilterKind.HighShelf, 40_000, 20)]
    public void A_shelf_reaches_its_gain_on_its_side_and_half_of_it_at_its_frequency(
        EqFilterKind kind, double shelfSide, double otherSide)
    {
        const int rate = 96_000;
        var frequency = kind == EqFilterKind.LowShelf ? 105.0 : 4_000.0;
        var filter = Biquad.Design(new EqBand(kind, frequency, 6, 0.7), rate)!.Value;

        Assert.InRange(filter.GainDbAt(shelfSide, rate), 5.95, 6.05);
        Assert.Equal(3, filter.GainDbAt(frequency, rate), 2);
        Assert.InRange(filter.GainDbAt(otherSide, rate), -0.1, 0.1);
    }

    [Fact]
    public void A_corner_frequency_shelf_is_centred_further_in()
    {
        // Equalizer APO's LS (not LSC) with a Q: the given frequency is the shelf's
        // corner, so its half-gain point sits above it for a low shelf.
        const int rate = 48_000;
        var centred = Biquad.Design(new EqBand(EqFilterKind.LowShelf, 100, 6, 0.7), rate)!.Value;
        var corner = Biquad.Design(new EqBand(EqFilterKind.LowShelf, 100, 6, 0.7, CornerFrequency: true), rate)!.Value;

        Assert.Equal(3, centred.GainDbAt(100, rate), 2);
        Assert.True(corner.GainDbAt(100, rate) > 3.2);
    }

    [Fact]
    public void A_band_at_or_above_half_the_rate_is_left_out()
    {
        Assert.Null(Biquad.Design(EqBand.Peak(16_000, 3, 1.41), 32_000));
        Assert.Null(Biquad.Design(EqBand.Peak(20_000, 3, 1.41), 32_000));
        Assert.NotNull(Biquad.Design(EqBand.Peak(15_999, 3, 1.41), 32_000));

        var design = EqDesign.For(Preset(0, EqBand.Peak(1_000, 3, 1.41), EqBand.Peak(20_000, 3, 1.41)), 32_000);
        Assert.Equal(1, design.FilterCount);
        Assert.Equal(1, design.SkippedBands);
    }

    [Fact]
    public void Unworkable_numbers_make_no_filter()
    {
        Assert.Null(Biquad.Design(EqBand.Peak(0, 3, 1.41), 48_000));
        Assert.Null(Biquad.Design(EqBand.Peak(1_000, 3, 0), 48_000));
        Assert.Null(Biquad.Design(EqBand.Peak(1_000, double.NaN, 1.41), 48_000));
        Assert.Null(Biquad.Design(new EqBand(EqFilterKind.Peak, 1_000, 3, 1, EqWidth.Slope), 48_000));
    }

    // ---------- the processor ----------

    [Fact]
    public void A_flat_preset_leaves_every_sample_untouched()
    {
        var flat = Preset(0, EqBand.Peak(100, 0, 1.41), EqBand.Peak(1_000, 0, 1.41), EqBand.Peak(10_000, 0, 1.41));
        Assert.True(flat.IsFlat);
        var design = EqDesign.For(flat, 44_100);
        Assert.True(design.IsIdentity);

        var samples = Noise(4_410 * 2);
        samples[10] = 1.5f; // even past full scale
        var before = samples.ToArray();

        Assert.Equal(0, new EqProcessor(2).Process(design, samples));
        Assert.Equal(before, samples);
    }

    [Fact]
    public void No_design_leaves_every_sample_untouched()
    {
        var samples = Noise(1_000);
        var before = samples.ToArray();
        new EqProcessor(2).Process(null, samples);
        Assert.Equal(before, samples);
    }

    [Fact]
    public void A_preamp_alone_is_a_plain_gain()
    {
        var design = EqDesign.For(Preset(20 * Math.Log10(0.5)), 44_100);
        Assert.False(design.IsIdentity);
        var samples = Noise(1_000);
        var before = samples.ToArray();

        new EqProcessor(2).Process(design, samples);

        for (var i = 0; i < samples.Length; i++)
            Assert.Equal(before[i] * 0.5f, samples[i], 6);
    }

    [Fact]
    public void Splitting_the_audio_into_blocks_changes_nothing()
    {
        var design = EqDesign.For(PeacePresetReader.Read(Path.Combine(Fixtures, "IER-M9 ChatGPT.peace")), 44_100);
        var whole = Noise(44_100 * 2, seed: 3);
        var split = whole.ToArray();

        new EqProcessor(2).Process(design, whole);

        var processor = new EqProcessor(2);
        var at = 0;
        foreach (var frames in new[] { 441, 1, 4_410, 999, 16_000 })
        {
            processor.Process(design, split.AsSpan(at, frames * 2));
            at += frames * 2;
        }
        processor.Process(design, split.AsSpan(at));

        Assert.Equal(whole, split);
    }

    [Fact]
    public void Channels_are_filtered_apart()
    {
        // A sine on the left, silence on the right: the right must stay silent.
        var design = EqDesign.For(Preset(0, EqBand.Peak(1_000, 6, 1.41)), 48_000);
        var samples = Sine(1_000, 0.5, 48_000, 0.1);
        for (var i = 1; i < samples.Length; i += 2)
            samples[i] = 0;

        new EqProcessor(2).Process(design, samples);

        for (var i = 1; i < samples.Length; i += 2)
            Assert.Equal(0f, samples[i]);
    }

    [Fact]
    public void Samples_pushed_past_full_scale_are_counted()
    {
        var design = EqDesign.For(Preset(6), 48_000);
        var samples = new[] { 0.9f, -0.9f, 0.1f, 0.4f };
        Assert.Equal(2, new EqProcessor(2).Process(design, samples));
    }

    [Fact]
    public void A_design_for_an_unsupported_preset_is_refused()
    {
        var preset = new EqPreset("x", 0, [], ["Graphic EQ"], EqPresetFormat.Peace);
        Assert.Throws<ArgumentException>(() => EqDesign.For(preset, 44_100));
    }

    [Fact]
    public void The_designs_response_adds_up_its_bands_and_the_preamp()
    {
        var design = EqDesign.For(Preset(-5, EqBand.Peak(100, 4, 1.41), EqBand.Peak(5_000, 2, 1.41)), 48_000);
        Assert.Equal(-5 + 4, design.GainDbAt(100), 1);
        Assert.Equal(-5 + 2, design.GainDbAt(5_000), 1);
    }

    // ---------- Peace presets ----------

    [Theory]
    [InlineData("IER-M9 ChatGPT", -5.1, 11)]
    [InlineData("HD650 Chat GPT", 0, 12)]
    [InlineData("Marcus Bass Boost", -3.8, 13)]
    [InlineData("Tin P1's", -4, 12)]
    public void The_users_presets_read_whole(string name, double preamp, int movedSliders)
    {
        var preset = PeacePresetReader.Read(Path.Combine(Fixtures, name + ".peace"));

        Assert.Equal(name, preset.Name);
        Assert.True(preset.IsSupported, string.Join("; ", preset.Unsupported));
        Assert.Equal(preamp, preset.PreampDb, 6);
        Assert.Equal(13, preset.Bands.Count);
        Assert.All(preset.Bands, b => Assert.Equal(EqFilterKind.Peak, b.Kind));
        Assert.Equal(movedSliders, preset.Bands.Count(b => b.GainDb != 0));
        Assert.Equal(movedSliders, EqDesign.For(preset, 44_100).FilterCount);
    }

    [Fact]
    public void The_IER_M9_preset_reads_its_numbers()
    {
        var preset = PeacePresetReader.Read(Path.Combine(Fixtures, "IER-M9 ChatGPT.peace"));
        Assert.Equal(EqBand.Peak(10, 2, 1.41), preset.Bands[0]);
        Assert.Equal(EqBand.Peak(577, 0, 1.64), preset.Bands[6]);
        Assert.Equal(EqBand.Peak(20_000, 1.5, 2.99), preset.Bands[12]);
    }

    [Fact]
    public void At_a_low_rate_the_top_bands_are_left_out()
    {
        var preset = PeacePresetReader.Read(Path.Combine(Fixtures, "IER-M9 ChatGPT.peace"));
        var design = EqDesign.For(preset, 32_000); // half is 16 kHz: the 16k and 20k bands go
        Assert.Equal(9, design.FilterCount);
        Assert.Equal(2, design.SkippedBands);
    }

    private const string Sliders = """
        [Frequencies]
        Frequency1=100
        Frequency2=1000
        Frequency3=10000
        [Qualities]
        Quality1=1.41
        Quality2=1.41
        Quality3=1.41
        """;

    [Fact]
    public void Peace_disabled_sliders_are_left_out()
    {
        var preset = PeacePresetReader.Parse("p", Sliders + """

            [Gains]
            Gain1=3
            Gain2=-2
            [Disabled]
            Disabled2=1
            """);
        Assert.True(preset.IsSupported);
        Assert.Equal([100.0, 10_000.0], preset.Bands.Select(b => b.FrequencyHz));
    }

    [Fact]
    public void Peace_reads_a_comma_as_a_decimal_point()
    {
        var preset = PeacePresetReader.Parse("p", Sliders + """

            [Gains]
            Gain1=1,5
            [General]
            PreAmp=-5,1
            """);
        Assert.Equal(-5.1, preset.PreampDb, 6);
        Assert.Equal(1.5, preset.Bands[0].GainDb, 6);
    }

    [Theory]
    [InlineData("[Gains]\nGain1=10\n[Filters]\nFilter1=14", "a filter type other than peak (slider 1)")]
    [InlineData("[Gains2]\nGain1=3", "gains for single speakers")]
    [InlineData("[General]\nPreAmp3=-2", "a preamp for single speakers")]
    [InlineData("[General]\nGraphicEQ=1", "Graphic EQ")]
    [InlineData("[General]\nRouting=R=1.000*L", "channel routing")]
    [InlineData("[Surround]\nSurround On=1\nFront Delay=9", "the surround effect")]
    [InlineData("[Crossfeed]\nOn=1", "a section AudioFool doesn't know ([Crossfeed])")]
    [InlineData("[General]\nLoudness=1", "a setting AudioFool doesn't know (Loudness)")]
    public void Peace_features_AudioFool_doesnt_run_are_reported(string extra, string reported)
    {
        var preset = PeacePresetReader.Parse("p", Sliders + "\n" + extra);
        Assert.False(preset.IsSupported);
        Assert.Contains(reported, preset.Unsupported);
    }

    [Theory]
    [InlineData("[Filters]\nFilter1=14")] // a shelf at 0 dB changes nothing
    [InlineData("[Gains2]\nGain1=0")]
    [InlineData("[General]\nPreAmp1=0\nGraphicEQ=0\nRouting=\nDescription=x\nConfiguration Icon=2")]
    [InlineData("[Surround]\nSurround On=0")]
    [InlineData("[Speakers]\nSpeakerId0=0\n[Sliders]\nSlider1-0=bass\n[Configuration]\nHotKey=\n[Frequencies1]\nFrequency1=10\n[Qualities1]\nQuality1=1")]
    public void Peace_things_that_change_nothing_are_not_reported(string extra)
    {
        var preset = PeacePresetReader.Parse("p", Sliders + "\n" + extra);
        Assert.True(preset.IsSupported, string.Join("; ", preset.Unsupported));
    }

    // ---------- Equalizer APO / AutoEq text ----------

    [Fact]
    public void An_AutoEq_file_reads_whole()
    {
        var preset = EqualizerApoTextReader.Parse("Some IEM", """
            # AutoEq
            Preamp: -6.2 dB
            Filter 1: ON LSC Fc 105 Hz Gain 2.4 dB Q 0.70
            Filter 2: ON PK Fc 2000 Hz Gain -3.1 dB Q 1.41
            Filter 3: OFF PK Fc 3000 Hz Gain 5.0 dB Q 1.00
            Filter 4: ON HSC Fc 10000 Hz Gain -1,5 dB Q 0.70
            """);

        Assert.True(preset.IsSupported, string.Join("; ", preset.Unsupported));
        Assert.Equal(-6.2, preset.PreampDb, 6);
        Assert.Equal(
        [
            new EqBand(EqFilterKind.LowShelf, 105, 2.4, 0.7),
            EqBand.Peak(2_000, -3.1, 1.41),
            new EqBand(EqFilterKind.HighShelf, 10_000, -1.5, 0.7),
        ], preset.Bands);
    }

    [Fact]
    public void Equalizer_APO_defaults_and_variants_are_followed()
    {
        var preset = EqualizerApoTextReader.Parse("x", """
            Preamp: -3 dB
            Preamp: -1.5 dB
            Channel: all
            Filter: ON LSC Fc 100 Hz Gain 6 dB
            Filter: ON LS 12dB Fc 100 Hz Gain 6 dB
            Filter: ON HS Fc 8000 Hz Gain 3 dB Q 0.7
            Filter: ON PK Fc 1.000 Hz Gain 2 dB BW Oct 1
            Filter: ON PEQ Fc 500 Hz Gain 1 dB Q 2
            Filter: ON None
            """);

        Assert.True(preset.IsSupported, string.Join("; ", preset.Unsupported));
        Assert.Equal(-4.5, preset.PreampDb, 6);
        Assert.Equal(
        [
            new EqBand(EqFilterKind.LowShelf, 100, 6, 0.9, EqWidth.Slope),
            new EqBand(EqFilterKind.LowShelf, 100, 6, 1, EqWidth.Slope, CornerFrequency: true),
            new EqBand(EqFilterKind.HighShelf, 8_000, 3, 0.7, EqWidth.Q, CornerFrequency: true),
            new EqBand(EqFilterKind.Peak, 1_000, 2, 1, EqWidth.Bandwidth),
            EqBand.Peak(500, 1, 2),
        ], preset.Bands);
    }

    [Theory]
    [InlineData("Filter: ON LP Fc 100 Hz", "LP filters")]
    [InlineData("Filter: ON PK Fc 100 Hz Gain 3 dB", "a peak filter with no Q (Filter: ON PK Fc 100 Hz Gain 3 dB)")]
    [InlineData("GraphicEQ: 20 0; 20000 3", "GraphicEQ: lines")]
    [InlineData("Channel: L", "Channel: lines")]
    [InlineData("Include: other.txt", "Include: lines")]
    [InlineData("Device: Speakers", "Device: lines")]
    public void Equalizer_APO_commands_AudioFool_doesnt_run_are_reported(string line, string reported)
    {
        var preset = EqualizerApoTextReader.Parse("x", "Preamp: -3 dB\n" + line);
        Assert.False(preset.IsSupported);
        Assert.Contains(reported, preset.Unsupported);
    }

    // ---------- an edit made in Peace ----------

    [Fact]
    public void Two_reads_of_one_file_are_the_same_preset()
    {
        var path = Path.Combine(Fixtures, "IER-M9 ChatGPT.peace");
        var first = PeacePresetReader.Read(path);
        var second = PeacePresetReader.Read(path);

        Assert.NotEqual(first, second); // a record compares its lists by reference
        Assert.True(first.SameAs(second));
    }

    [Fact]
    public void An_edit_is_told_from_the_preset_as_it_was()
    {
        var text = File.ReadAllText(Path.Combine(Fixtures, "IER-M9 ChatGPT.peace"));
        var before = PeacePresetReader.Parse("IER-M9 ChatGPT", text);

        Assert.False(before.SameAs(PeacePresetReader.Parse("IER-M9 ChatGPT", text.Replace("Gain10=2", "Gain10=3"))));
        Assert.False(before.SameAs(PeacePresetReader.Parse("IER-M9 ChatGPT", text.Replace("PreAmp=-5.1", "PreAmp=-6"))));
        Assert.False(before.SameAs(PeacePresetReader.Parse("Tin P1's", text)));
        Assert.False(before.SameAs(null));
        Assert.True(before.SameAs(PeacePresetReader.Parse("ier-m9 chatgpt", text)));
    }

    // ---------- where presets come from ----------

    [Fact]
    public void The_library_reads_only_presets_from_Peaces_folder_and_both_kinds_from_its_own()
    {
        var root = Directory.CreateTempSubdirectory("audiofool-eq-");
        try
        {
            var peace = Directory.CreateDirectory(Path.Combine(root.FullName, "config")).FullName;
            var own = Directory.CreateDirectory(Path.Combine(root.FullName, "eq")).FullName;
            File.Copy(Path.Combine(Fixtures, "Tin P1's.peace"), Path.Combine(peace, "Tin P1's.peace"));
            File.WriteAllText(Path.Combine(peace, "config.txt"), "Include: peace.txt");
            File.Copy(Path.Combine(Fixtures, "HD650 Chat GPT.peace"), Path.Combine(own, "HD650 Chat GPT.peace"));
            File.WriteAllText(Path.Combine(own, "AutoEq IEM.txt"), "Preamp: -2 dB\nFilter 1: ON PK Fc 100 Hz Gain 2 dB Q 1");
            File.WriteAllText(Path.Combine(own, "notes.md"), "not a preset");

            var presets = EqPresetLibrary.Load(peace, own);

            Assert.Equal(["Tin P1's", "AutoEq IEM", "HD650 Chat GPT"], presets.Select(p => p.Name));
            Assert.Equal([EqPresetFormat.Peace, EqPresetFormat.EqualizerApo, EqPresetFormat.Peace],
                presets.Select(p => p.Format));
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [Fact]
    public void Missing_folders_give_no_presets()
    {
        var nowhere = Path.Combine(Path.GetTempPath(), "audiofool-no-such-folder-" + Guid.NewGuid());
        Assert.Empty(EqPresetLibrary.Load(nowhere, null));
        Assert.Empty(EqPresetLibrary.Load("", "  "));
    }
}
