namespace AudioFool.Core.Playback;

/// <summary>
/// The volume slider and its mute button (spec 6.7). Muting shows the slider
/// at zero and remembers the level; unmuting restores it. Moving the slider
/// while muted unmutes at the new level.
/// </summary>
public sealed class VolumeState
{
    private double _level;

    public VolumeState(double level) => _level = Math.Clamp(level, 0, 1);

    /// <summary>The level to restore, kept while muted.</summary>
    public double Level => _level;

    public bool IsMuted { get; private set; }

    /// <summary>What the slider shows and the engine plays at.</summary>
    public double Effective => IsMuted ? 0 : _level;

    public void ToggleMute() => IsMuted = !IsMuted;

    /// <summary>The slider moved: set the level and unmute.</summary>
    public void SetLevel(double level)
    {
        level = Math.Clamp(level, 0, 1);

        // A muted slider sits at zero, and WPF writes that zero back through the
        // binding; that is not the user choosing silence.
        if (IsMuted && level == 0)
            return;

        _level = level;
        IsMuted = false;
    }
}
