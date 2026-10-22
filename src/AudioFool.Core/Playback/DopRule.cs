namespace AudioFool.Core.Playback;

/// <summary>
/// When DSD goes out as DSD over PCM (DoP): the DAC reads markers hidden in the
/// samples, so they must reach it untouched, which only exclusive output at the
/// DoP rate does. Anything else (Windows' mixer resampling or changing the
/// volume) turns them into hiss, and the file is converted to PCM instead.
/// Pulled out of <see cref="AudioEngine"/> in session 67, which also closed the
/// way DoP could reach a shared output: a rate exclusive mode had just refused.
/// </summary>
public static class DopRule
{
    /// <summary>
    /// Whether a DSD file is opened as DoP: Bit-Perfect is on, the device can be
    /// clocked at the DoP rate, and exclusive mode hasn't been refused at it.
    /// </summary>
    public static bool MayOpen(OutputMode requested, int dopRate, IReadOnlyCollection<int> exclusiveRates,
                               IReadOnlyCollection<int> refusedRates) =>
        requested == OutputMode.Exclusive && dopRate > 0
        && exclusiveRates.Contains(dopRate) && !refusedRates.Contains(dopRate);

    /// <summary>Whether a DoP stream at <paramref name="dopRate"/> may play on this output: exclusive, at its rate.</summary>
    public static bool MayPlayOn(OutputMode outputMode, int outputRate, int dopRate) =>
        outputMode == OutputMode.Exclusive && outputRate == dopRate;
}
