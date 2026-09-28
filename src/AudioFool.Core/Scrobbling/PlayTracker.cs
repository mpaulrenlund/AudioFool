using AudioFool.Core.Models;

namespace AudioFool.Core.Scrobbling;

/// <summary>
/// Decides when a play has earned a scrobble, by Last.fm's rule: the track is
/// longer than 30 seconds and has been played for half its length or for four
/// minutes, whichever comes first.
/// <para>
/// Listening time is counted from the audio position, not the clock. The
/// caller reports the position a few times a second while playing, and only
/// small forward steps count. So a seek forward is not listening, a pause or
/// the machine sleeping adds nothing, and a jump back to the first few seconds
/// is a new play - which is how a Repeat-One loop and a Previous that restarts
/// the track are recognised, since the engine raises no event for either.
/// </para>
/// <para>
/// Pure and clock-free: every time comes in as an argument.
/// </para>
/// </summary>
public sealed class PlayTracker
{
    public static readonly TimeSpan MinimumLength = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan MaximumThreshold = TimeSpan.FromMinutes(4);

    /// <summary>
    /// The largest position step counted as listening. Ticks come every 250 ms;
    /// this leaves room for a busy UI thread while still treating a short skip
    /// forward as a seek.
    /// </summary>
    public static readonly TimeSpan MaxStep = TimeSpan.FromSeconds(3);

    /// <summary>A jump back to before this point starts a new play.</summary>
    public static readonly TimeSpan RestartWindow = TimeSpan.FromSeconds(3);

    private Track? _track;
    private TimeSpan _duration;
    private TimeSpan _played;
    private TimeSpan _lastPosition;
    private DateTimeOffset _startedAt;
    private bool _scrobbled;

    public Track? Current => _track;
    public TimeSpan Played => _played;

    /// <summary>
    /// How long a track must be played to scrobble, or null if it is too short
    /// ever to scrobble (or its length is unknown).
    /// </summary>
    public static TimeSpan? Threshold(TimeSpan duration) =>
        duration <= MinimumLength ? null
        : duration / 2 < MaximumThreshold ? duration / 2
        : MaximumThreshold;

    /// <summary>
    /// A track began playing from the start. Returns true when this is a new
    /// play, which is when "now playing" should be sent.
    /// <para>
    /// The same file starting again before it has scrobbled continues the play
    /// rather than starting over: switching output mode reopens the current
    /// track through the engine's ordinary track-change path, and that should
    /// not throw away the minute already heard.
    /// </para>
    /// </summary>
    public bool Start(Track track, TimeSpan duration, DateTimeOffset now)
    {
        var sameUnfinished = _track is not null && !_scrobbled
            && string.Equals(_track.FilePath, track.FilePath, StringComparison.OrdinalIgnoreCase);

        _track = track;
        _duration = duration;
        _lastPosition = TimeSpan.Zero;

        if (sameUnfinished)
            return false;

        _played = TimeSpan.Zero;
        _startedAt = now;
        _scrobbled = false;
        return true;
    }

    /// <summary>Playback stopped: the play is over, scrobbled or not.</summary>
    public void Stop() => _track = null;

    /// <summary>
    /// Reports the current position. Returns <see cref="AdvanceResult.Restarted"/>
    /// when the track jumped back to its start, and the scrobble - exactly once
    /// per play - when this step crossed the threshold.
    /// </summary>
    public AdvanceResult Advance(TimeSpan position, DateTimeOffset now)
    {
        if (_track is null)
            return default;

        var step = position - _lastPosition;
        var restarted = false;

        if (step < TimeSpan.Zero)
        {
            if (position < RestartWindow && _lastPosition >= RestartWindow)
            {
                _played = position;
                _startedAt = now - position;
                _scrobbled = false;
                restarted = true;
            }
        }
        else if (step <= MaxStep)
        {
            _played += step;
        }

        _lastPosition = position;

        ScrobbleEntry? due = null;
        if (!_scrobbled && Threshold(_duration) is { } threshold && _played >= threshold)
        {
            _scrobbled = true;
            due = ScrobbleEntry.From(_track, _duration, _startedAt);
        }

        return new AdvanceResult(restarted, due);
    }

    /// <summary>The play as "now playing" would describe it.</summary>
    public ScrobbleEntry? NowPlaying() =>
        _track is null ? null : ScrobbleEntry.From(_track, _duration, _startedAt);
}

public readonly record struct AdvanceResult(bool Restarted, ScrobbleEntry? Due);
