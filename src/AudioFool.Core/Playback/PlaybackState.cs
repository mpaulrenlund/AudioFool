namespace AudioFool.Core.Playback;

public enum PlaybackState
{
    Stopped,
    Playing,
    Paused,
}

public enum RepeatMode
{
    Off,
    All,
    One,
}

/// <summary>How audio reaches the sound card.</summary>
public enum OutputMode
{
    /// <summary>
    /// Shared WASAPI. Mixes with every other app on the machine, so Windows
    /// resamples everything to the device's shared mix rate - usually 48 kHz.
    /// System volume behaves normally.
    /// </summary>
    Shared,

    /// <summary>
    /// Exclusive WASAPI. AudioFool takes sole ownership of the endpoint and opens
    /// it at the track's own sample rate, so the samples reach the DAC untouched.
    /// Other applications fall silent while a track is playing, and the volume has
    /// to stay at 100% for the output to genuinely be bit-perfect.
    /// </summary>
    Exclusive,
}

/// <summary>What to do with DSD material.</summary>
public enum DsdMode
{
    /// <summary>
    /// Decode DSD to PCM. Works with any DAC. The conversion runs at the highest
    /// rate in the 44.1 kHz family the device accepts - up to an eighth of the DSD
    /// rate, which is 705.6 kHz for DSD128.
    /// </summary>
    ConvertToPcm,

    /// <summary>
    /// DSD over PCM. The DSD bitstream is carried inside a 24-bit PCM container
    /// with 0x05/0xFA marker bytes, which a DoP-aware DAC detects and unwraps to
    /// get the original bits. Needs exclusive output at one sixteenth of the DSD
    /// rate (352.8 kHz for DSD128) and a DAC that speaks DoP; falls back to
    /// <see cref="ConvertToPcm"/> when either is unavailable.
    /// </summary>
    DsdOverPcm,
}
