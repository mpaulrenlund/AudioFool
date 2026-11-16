namespace AudioFool.Core.Equalizer;

/// <summary>
/// A preset worked out for one sample rate: the preamp as a plain gain and the
/// filters it runs. Immutable, so it can be built off the device's thread and
/// handed over whole. Bands that change nothing (0 dB) are left out, as are bands
/// the rate can't carry, so a flat preset designs to <see cref="IsIdentity"/>.
/// </summary>
public sealed class EqDesign
{
    /// <summary>The most filters one preset may run; <see cref="EqProcessor"/> sets its state aside for this many.</summary>
    public const int MaxBands = 64;

    private EqDesign(string name, int sampleRate, double preampDb, Biquad[] filters, int skipped)
    {
        Name = name;
        SampleRate = sampleRate;
        PreampDb = preampDb;
        PreampGain = Math.Pow(10, preampDb / 20);
        Filters = filters;
        SkippedBands = skipped;
    }

    public string Name { get; }
    public int SampleRate { get; }
    public double PreampDb { get; }

    /// <summary>The preamp as a factor on each sample.</summary>
    public double PreampGain { get; }

    /// <summary>The filters, in the preset's order. Internal so the device thread reads them without a copy.</summary>
    internal Biquad[] Filters { get; }

    public int FilterCount => Filters.Length;

    /// <summary>Bands that change something but can't run at this rate (at or above half of it).</summary>
    public int SkippedBands { get; }

    /// <summary>Nothing to do: processing would hand every sample back unchanged.</summary>
    public bool IsIdentity => Filters.Length == 0 && PreampDb == 0;

    /// <summary>
    /// <paramref name="preset"/> at <paramref name="sampleRate"/>. Throws for a preset
    /// that isn't <see cref="EqPreset.IsSupported"/>: half a preset is not the preset.
    /// </summary>
    public static EqDesign For(EqPreset preset, int sampleRate)
    {
        ArgumentNullException.ThrowIfNull(preset);
        if (!preset.IsSupported)
            throw new ArgumentException($"Preset '{preset.Name}' uses things AudioFool doesn't run.", nameof(preset));
        if (sampleRate <= 0)
            throw new ArgumentOutOfRangeException(nameof(sampleRate));

        var filters = new List<Biquad>(preset.Bands.Count);
        var skipped = 0;
        foreach (var band in preset.Bands)
        {
            if (band.GainDb == 0)
                continue; // a 0 dB peak or shelf is exactly a pass-through
            if (Biquad.Design(band, sampleRate) is { } filter)
                filters.Add(filter);
            else
                skipped++;
        }

        if (filters.Count > MaxBands)
            throw new ArgumentException($"Preset '{preset.Name}' has more than {MaxBands} filters.", nameof(preset));

        var preamp = double.IsFinite(preset.PreampDb) ? preset.PreampDb : 0;
        return new EqDesign(preset.Name, sampleRate, preamp, filters.ToArray(), skipped);
    }

    /// <summary>The whole preset's effect on a sine at <paramref name="frequencyHz"/>, preamp included, in dB.</summary>
    public double GainDbAt(double frequencyHz)
    {
        var total = PreampDb;
        foreach (var filter in Filters)
            total += filter.GainDbAt(frequencyHz, SampleRate);
        return total;
    }
}
