using System.Globalization;

namespace AudioFool.Core.Equalizer;

/// <summary>One band as the album header's EQ panel lists it: its frequency and gain as text.</summary>
public sealed record EqGraphBand(double FrequencyHz, double GainDb, string Frequency, string Gain);

/// <summary>A point on the panel's graph: across, 0 to 1 on <see cref="EqGraph"/>'s log axis; up, in dB.</summary>
public readonly record struct EqGraphPoint(double X, double Db);

/// <summary>
/// What the album header's EQ panel draws for the preset in use (session 80, the
/// user's design, mock-up A): the preset's real response, as Equalizer APO would
/// run it, from 10 Hz to 20 kHz on a log scale like Peace's, a dot on it at each
/// band, a dB scale that grows to fit, and each band's frequency and gain as text.
/// <para>
/// The curve leaves the preamp out (the user's call): it shows the shape the bands
/// make, which is what the gain values under it add up to, and the preamp is shown
/// as its own number. It is worked out at <see cref="CurveSampleRate"/>; the rate the
/// device runs at bends only the top octave, and by a fraction of a dB.
/// </para>
/// </summary>
public sealed class EqGraph
{
    /// <summary>The axis' ends, Peace's own.</summary>
    public const double LowestHz = 10;
    public const double HighestHz = 20000;

    /// <summary>The rate the curve is worked out at.</summary>
    public const int CurveSampleRate = 48000;

    /// <summary>Points along the curve: about one every 2 px at the panel's width.</summary>
    public const int CurvePoints = 241;

    /// <summary>The scale grows in steps of this many dB (±6, ±12, …).</summary>
    public const double ScaleStepDb = 6;

    private EqGraph(EqPreset preset, IReadOnlyList<EqGraphPoint> curve, IReadOnlyList<EqGraphPoint> dots,
        IReadOnlyList<EqGraphBand> bands, double rangeDb)
    {
        Preset = preset;
        Curve = curve;
        Dots = dots;
        Bands = bands;
        RangeDb = rangeDb;
    }

    public EqPreset Preset { get; }

    public string Name => Preset.Name;

    /// <summary>"Preamp −9.8 dB"; "Preamp 0 dB" for none.</summary>
    public string Preamp => $"Preamp {FormatDb(Preset.PreampDb, signed: false)} dB";

    /// <summary>The bands' response, preamp left out, left to right.</summary>
    public IReadOnlyList<EqGraphPoint> Curve { get; }

    /// <summary>A dot on <see cref="Curve"/> at each band's frequency, in <see cref="Bands"/>' order.</summary>
    public IReadOnlyList<EqGraphPoint> Dots { get; }

    /// <summary>The bands, lowest frequency first.</summary>
    public IReadOnlyList<EqGraphBand> Bands { get; }

    /// <summary>The scale's top, and its bottom below 0: the smallest step of <see cref="ScaleStepDb"/> the curve and the gains fit in.</summary>
    public double RangeDb { get; }

    /// <summary>"+6", the label at the scale's top.</summary>
    public string TopLabel => "+" + RangeDb.ToString("0", CultureInfo.InvariantCulture);

    /// <summary>"−6", the label at the scale's bottom.</summary>
    public string BottomLabel => "−" + RangeDb.ToString("0", CultureInfo.InvariantCulture);

    /// <summary>
    /// The panel's whole content for a screen reader: name, preamp and every band,
    /// frequencies in whole hertz ("1000 Hz", not the label's "1k").
    /// </summary>
    public string AutomationText =>
        $"Equalizer {Name}, {Preamp}, " + string.Join(", ", Bands.Select(b =>
            $"{b.FrequencyHz.ToString("0.#", CultureInfo.InvariantCulture)} Hz {b.Gain} dB"));

    /// <summary>
    /// The graph for <paramref name="preset"/>, one AudioFool plays (<see cref="EqPreset.IsSupported"/>).
    /// </summary>
    public static EqGraph For(EqPreset preset)
    {
        ArgumentNullException.ThrowIfNull(preset);
        var design = EqDesign.For(preset, CurveSampleRate);
        double BandsDbAt(double hz) => design.GainDbAt(hz) - design.PreampDb;

        var curve = new EqGraphPoint[CurvePoints];
        for (var i = 0; i < CurvePoints; i++)
        {
            var x = (double)i / (CurvePoints - 1);
            curve[i] = new EqGraphPoint(x, BandsDbAt(FrequencyAt(x)));
        }

        var sorted = preset.Bands
            .Where(b => double.IsFinite(b.FrequencyHz) && double.IsFinite(b.GainDb))
            .OrderBy(b => b.FrequencyHz)
            .ToArray();
        var bands = sorted
            .Select(b => new EqGraphBand(b.FrequencyHz, b.GainDb, FormatFrequency(b.FrequencyHz), FormatDb(b.GainDb, signed: true)))
            .ToArray();
        var dots = sorted
            .Where(b => b.FrequencyHz >= LowestHz && b.FrequencyHz <= HighestHz)
            .Select(b => new EqGraphPoint(Position(b.FrequencyHz), BandsDbAt(b.FrequencyHz)))
            .ToArray();

        var peak = curve.Select(p => Math.Abs(p.Db)).Concat(sorted.Select(b => Math.Abs(b.GainDb))).DefaultIfEmpty(0).Max();
        return new EqGraph(preset, curve, dots, bands, RangeFor(peak));
    }

    /// <summary>Where <paramref name="hz"/> sits across the axis, 0 at 10 Hz and 1 at 20 kHz.</summary>
    public static double Position(double hz) => Math.Log(hz / LowestHz) / Math.Log(HighestHz / LowestHz);

    /// <summary>The frequency at <paramref name="x"/> across the axis; the inverse of <see cref="Position"/>.</summary>
    public static double FrequencyAt(double x) => LowestHz * Math.Pow(HighestHz / LowestHz, x);

    /// <summary>The smallest multiple of <see cref="ScaleStepDb"/>, at least one step, that holds <paramref name="peakDb"/>.</summary>
    public static double RangeFor(double peakDb)
    {
        // A hair of slack, so a curve that peaks at exactly +6.0 dB, give or take
        // the filters' rounding, stays on the ±6 scale.
        var steps = Math.Ceiling((Math.Abs(peakDb) - 1e-6) / ScaleStepDb);
        return Math.Max(1, steps) * ScaleStepDb;
    }

    /// <summary>"10", "577", "1k", "2.5k", "16k": as Peace's sliders are labelled, thousands as k.</summary>
    public static string FormatFrequency(double hz)
    {
        if (hz >= 1000)
            return (hz / 1000).ToString("0.#", CultureInfo.InvariantCulture) + "k";
        return hz.ToString("0.#", CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// One decimal, a true minus sign: "+3.1", "−1.6", and "0" for anything that
    /// rounds to it. <paramref name="signed"/> puts the plus on gains; a preamp has none.
    /// </summary>
    public static string FormatDb(double db, bool signed)
    {
        var rounded = Math.Round(db, 1, MidpointRounding.AwayFromZero);
        if (rounded == 0)
            return "0";
        var digits = Math.Abs(rounded).ToString("0.0", CultureInfo.InvariantCulture);
        return rounded < 0 ? "−" + digits : (signed ? "+" : "") + digits;
    }
}
