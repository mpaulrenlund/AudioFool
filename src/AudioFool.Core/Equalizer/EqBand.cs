namespace AudioFool.Core.Equalizer;

/// <summary>The filter shapes the built-in equalizer runs (Equalizer APO's PK, LS/LSC and HS/HSC).</summary>
public enum EqFilterKind
{
    /// <summary>A bell around the frequency.</summary>
    Peak,

    /// <summary>Raises or lowers everything below the frequency.</summary>
    LowShelf,

    /// <summary>Raises or lowers everything above the frequency.</summary>
    HighShelf,
}

/// <summary>How a band's <see cref="EqBand.Width"/> is meant, as in Equalizer APO.</summary>
public enum EqWidth
{
    /// <summary>A quality factor: higher is narrower.</summary>
    Q,

    /// <summary>A bandwidth in octaves (peak filters only: Equalizer APO's "BW Oct").</summary>
    Bandwidth,

    /// <summary>A shelf slope S, where 1 is the steepest without overshoot (shelves only).</summary>
    Slope,
}

/// <summary>
/// One filter of a preset, as written in the preset file. Equalizer APO's own
/// rules for reading the file (default widths, its "corner frequency" shelves)
/// are applied by the readers, so this is exactly what Equalizer APO would run.
/// </summary>
/// <param name="CornerFrequency">
/// Equalizer APO's LS/HS with a Q or slope: <see cref="FrequencyHz"/> is the shelf's
/// corner, and the filter is centred a little further in (BiQuadFilter.cpp, "frequency
/// adjustment for DCX2496"). LSC/HSC, and every Peace band, are centred on it.
/// </param>
public sealed record EqBand(
    EqFilterKind Kind,
    double FrequencyHz,
    double GainDb,
    double Width,
    EqWidth WidthKind = EqWidth.Q,
    bool CornerFrequency = false)
{
    /// <summary>A peak filter, the only kind a Peace preset AudioFool can play uses.</summary>
    public static EqBand Peak(double frequencyHz, double gainDb, double q) =>
        new(EqFilterKind.Peak, frequencyHz, gainDb, q);
}
