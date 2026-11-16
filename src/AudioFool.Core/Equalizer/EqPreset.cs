namespace AudioFool.Core.Equalizer;

/// <summary>Where a preset came from.</summary>
public enum EqPresetFormat
{
    /// <summary>A Peace preset (<c>.peace</c>, an INI file).</summary>
    Peace,

    /// <summary>Equalizer APO's own text, as AutoEq writes it (<c>Preamp:</c>, <c>Filter n:</c> lines).</summary>
    EqualizerApo,
}

/// <summary>
/// An equalizer preset read from a file: a preamp and a list of filters. A preset
/// that uses anything AudioFool doesn't run lists it in <see cref="Unsupported"/>
/// and must not be played: playing the rest of it would sound different from what
/// its author set up.
/// </summary>
public sealed record EqPreset(
    string Name,
    double PreampDb,
    IReadOnlyList<EqBand> Bands,
    IReadOnlyList<string> Unsupported,
    EqPresetFormat Format,
    string? SourcePath = null)
{
    /// <summary>True when everything in the file is something AudioFool runs.</summary>
    public bool IsSupported => Unsupported.Count == 0;

    /// <summary>No preamp and no band that changes anything: playing it leaves the samples untouched.</summary>
    public bool IsFlat => PreampDb == 0 && Bands.All(b => b.GainDb == 0);

    /// <summary>
    /// The same preset as <paramref name="other"/>, sound for sound: the same name,
    /// preamp, filters and unsupported parts (a record's own equality compares the
    /// lists by reference, so two reads of one file would differ). How an edit made
    /// in Peace is told from a fresh read of the same file.
    /// </summary>
    public bool SameAs(EqPreset? other) =>
        other is not null
        && string.Equals(Name, other.Name, StringComparison.OrdinalIgnoreCase)
        && PreampDb.Equals(other.PreampDb)
        && Bands.SequenceEqual(other.Bands)
        && Unsupported.SequenceEqual(other.Unsupported);
}
