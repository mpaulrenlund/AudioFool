namespace AudioFool.Core.Playback;

/// <summary>
/// The output readout under the volume slider (spec 6.7):
/// "Shared · 96 kHz / 32-bit (resampled)" or "Exclusive · 44.1 kHz". Exclusive
/// mode is what the Bit-Perfect chip turns on, so the readout doesn't repeat it.
/// </summary>
public static class OutputReadout
{
    /// <param name="mode">The mode the device connection actually opened in.</param>
    /// <param name="deviceRate">The connection's sample rate, in Hz.</param>
    /// <param name="deviceBitDepth">The bit depth WASAPI opened, or 0 when unknown.</param>
    /// <param name="sourceRate">The playing track's decoded rate, or 0 when unknown.</param>
    /// <param name="isDop">Whether the track is passed through as DSD over PCM.</param>
    public static string Describe(OutputMode mode, int deviceRate, int deviceBitDepth, int sourceRate, bool isDop)
    {
        var rate = Rate(deviceRate);

        if (mode == OutputMode.Exclusive)
        {
            // DoP looks like ordinary high-rate PCM from the device's side, so say
            // which it is rather than leaving it ambiguous.
            return isDop
                ? $"Exclusive · {rate} · DSD over PCM"
                : $"Exclusive · {rate}";
        }

        var depth = deviceBitDepth > 0 ? $" / {deviceBitDepth}-bit" : "";
        var resampled = sourceRate > 0 && sourceRate != deviceRate ? " (resampled)" : "";
        return $"Shared · {rate}{depth}{resampled}";
    }

    /// <summary>"44.1 kHz", "96 kHz".</summary>
    public static string Rate(int hz) =>
        hz % 1000 == 0 ? $"{hz / 1000} kHz" : $"{hz / 1000.0:0.#} kHz";
}
