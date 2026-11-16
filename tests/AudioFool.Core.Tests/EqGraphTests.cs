using AudioFool.Core.Equalizer;

namespace AudioFool.Core.Tests;

/// <summary>The album header's EQ panel (session 80): the curve, the dots, the scale and the labels.</summary>
public class EqGraphTests
{
    private static readonly string Fixtures = Path.Combine(AppContext.BaseDirectory, "TestData", "eq");

    private static EqPreset Preset(double preampDb, params EqBand[] bands) =>
        new("test", preampDb, bands, [], EqPresetFormat.EqualizerApo);

    [Fact]
    public void The_axis_runs_from_10_Hz_to_20_kHz_on_a_log_scale()
    {
        Assert.Equal(0, EqGraph.Position(10), 9);
        Assert.Equal(1, EqGraph.Position(20000), 9);
        // Each octave takes the same room.
        Assert.Equal(EqGraph.Position(200) - EqGraph.Position(100), EqGraph.Position(4000) - EqGraph.Position(2000), 9);
        Assert.Equal(1000, EqGraph.FrequencyAt(EqGraph.Position(1000)), 6);
    }

    [Fact]
    public void The_curve_leaves_the_preamp_out()
    {
        var withPreamp = EqGraph.For(Preset(-9.8, EqBand.Peak(1000, 3, 1.41)));
        var without = EqGraph.For(Preset(0, EqBand.Peak(1000, 3, 1.41)));

        Assert.All(without.Curve.Zip(withPreamp.Curve), pair => Assert.Equal(pair.First.Db, pair.Second.Db, 9));
        Assert.Equal("Preamp −9.8 dB", withPreamp.Preamp);
        Assert.Equal("Preamp 0 dB", without.Preamp);
    }

    [Fact]
    public void A_dot_sits_on_the_curve_at_its_band_and_reads_the_band_s_gain_alone()
    {
        var graph = EqGraph.For(Preset(0, EqBand.Peak(1000, 4, 1.41)));

        var dot = Assert.Single(graph.Dots);
        Assert.Equal(EqGraph.Position(1000), dot.X, 9);
        Assert.Equal(4, dot.Db, 2); // a peak's gain at its own frequency
        Assert.All(graph.Curve, p => Assert.InRange(p.Db, -0.01, 4.01));
        Assert.Equal(0, graph.Curve[0].Db, 1); // far from the band, about nothing
    }

    [Fact]
    public void The_curve_spans_the_whole_axis()
    {
        var graph = EqGraph.For(Preset(0, EqBand.Peak(1000, 4, 1.41)));

        Assert.Equal(EqGraph.CurvePoints, graph.Curve.Count);
        Assert.Equal(0, graph.Curve[0].X);
        Assert.Equal(1, graph.Curve[^1].X);
    }

    [Theory]
    [InlineData(0, 6)]
    [InlineData(3.1, 6)]
    [InlineData(6, 6)]
    [InlineData(6.2, 12)]
    [InlineData(-13, 18)]
    public void The_scale_grows_in_6_dB_steps(double peak, double range)
    {
        Assert.Equal(range, EqGraph.RangeFor(peak));
    }

    [Fact]
    public void The_scale_fits_a_cut_as_well_as_a_boost()
    {
        var graph = EqGraph.For(Preset(0, EqBand.Peak(100, -8, 1)));

        Assert.Equal(12, graph.RangeDb);
        Assert.Equal("+12", graph.TopLabel);
        Assert.Equal("−12", graph.BottomLabel);
    }

    [Theory]
    [InlineData(10, "10")]
    [InlineData(577, "577")]
    [InlineData(1000, "1k")]
    [InlineData(2500, "2.5k")]
    [InlineData(16000, "16k")]
    [InlineData(105.5, "105.5")]
    public void Frequencies_are_labelled_as_Peace_s_sliders(double hz, string label)
    {
        Assert.Equal(label, EqGraph.FormatFrequency(hz));
    }

    [Theory]
    [InlineData(3.11, true, "+3.1")]
    [InlineData(-1.56, true, "−1.6")]
    [InlineData(0, true, "0")]
    [InlineData(0.04, true, "0")]
    [InlineData(-0.04, true, "0")]
    [InlineData(3, true, "+3.0")]
    [InlineData(-9.8, false, "−9.8")]
    [InlineData(2.5, false, "2.5")]
    public void Gains_get_one_decimal_and_a_true_minus(double db, bool signed, string text)
    {
        Assert.Equal(text, EqGraph.FormatDb(db, signed));
    }

    [Fact]
    public void Bands_are_listed_lowest_first_whatever_the_file_s_order()
    {
        var graph = EqGraph.For(Preset(0, EqBand.Peak(4000, 1, 1), EqBand.Peak(100, 2, 1)));

        Assert.Equal(["100", "4k"], graph.Bands.Select(b => b.Frequency));
        Assert.Equal(["+2.0", "+1.0"], graph.Bands.Select(b => b.Gain));
        Assert.True(graph.Dots[0].X < graph.Dots[1].X);
    }

    [Fact]
    public void A_shelf_plays_into_the_curve()
    {
        var graph = EqGraph.For(Preset(0, new EqBand(EqFilterKind.LowShelf, 105, 6, 0.7)));

        Assert.Equal(6, graph.Curve[0].Db, 1); // the whole low end lifted
        Assert.Equal(0, graph.Curve[^1].Db, 1);
    }

    [Fact]
    public void The_user_s_IER_M9_preset_draws_as_Peace_lists_it()
    {
        var preset = PeacePresetReader.Read(Path.Combine(Fixtures, "IER-M9 ChatGPT.peace"));
        var graph = EqGraph.For(preset);

        Assert.Equal("IER-M9 ChatGPT", graph.Name);
        Assert.Equal(13, graph.Bands.Count);
        Assert.Equal(13, graph.Dots.Count);
        Assert.Equal(["10", "21", "42", "83", "166", "333", "577", "1k", "2k", "4k", "8k", "16k", "20k"],
            graph.Bands.Select(b => b.Frequency));
        // Neighbouring boosts add up: the curve rises above any one band's gain.
        Assert.True(graph.Curve.Max(p => p.Db) > graph.Bands.Max(b => b.GainDb));
        Assert.InRange(graph.RangeDb, 6, 12);
        Assert.StartsWith("Equalizer IER-M9 ChatGPT, Preamp ", graph.AutomationText);
        Assert.Contains(", 1000 Hz ", graph.AutomationText); // whole hertz, not the label's "1k"
        Assert.DoesNotContain("k Hz", graph.AutomationText);
    }
}
