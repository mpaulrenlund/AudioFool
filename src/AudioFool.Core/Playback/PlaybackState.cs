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
