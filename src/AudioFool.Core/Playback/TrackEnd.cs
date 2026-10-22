namespace AudioFool.Core.Playback;

/// <summary>What the engine does when the playing track's audio runs out (<see cref="TrackEnd.Decide"/>).</summary>
public enum TrackEndAction
{
    /// <summary>Repeat One: the same track again from its start, gapless.</summary>
    RepeatOne,

    /// <summary>The next track, already open, joined on in the same block: gapless.</summary>
    Splice,

    /// <summary>The next track is open but can't join this output (another rate in exclusive mode, or DoP on a shared output): the device is reopened for it once the last track is heard.</summary>
    Reopen,

    /// <summary>The next track's file is out for a save, which plays it when done.</summary>
    WaitForSave,

    /// <summary>There is a next track, but it wasn't opened ahead (it wouldn't open, or hadn't yet): opened now, skipping any that won't, once the last track is heard.</summary>
    OpenNext,

    /// <summary>The end of the queue: the device is let go once the last track is heard.</summary>
    Finish,
}

/// <summary>
/// The decision the engine's END sync makes, pulled out of
/// <see cref="AudioEngine"/> so it can be tested without a sound device
/// (session 67, as <see cref="PlayOrder"/> was). Until then a next track that
/// wouldn't open ended the queue, and DoP could be joined onto a shared output.
/// </summary>
public static class TrackEnd
{
    /// <param name="repeatOne">Repeat is on One.</param>
    /// <param name="hasOutput">A device connection is open.</param>
    /// <param name="nextOpened">The next track's stream was opened ahead of time.</param>
    /// <param name="sharedOutput">The output is shared (Windows' mixer, which resamples every track).</param>
    /// <param name="nextAtOutputRate">The next track's rate is the output's (exclusive mode follows the track).</param>
    /// <param name="nextIsDop">The next track was opened as DSD over PCM, which only exclusive output keeps intact.</param>
    /// <param name="nextMidSave">The next track's file is being saved (its stream let go for it).</param>
    /// <param name="hasNext">The play order has a track after this one.</param>
    public static TrackEndAction Decide(bool repeatOne, bool hasOutput, bool nextOpened, bool sharedOutput,
                                        bool nextAtOutputRate, bool nextIsDop, bool nextMidSave, bool hasNext)
    {
        if (repeatOne && hasOutput)
            return TrackEndAction.RepeatOne;

        if (nextOpened)
        {
            var joins = hasOutput && (sharedOutput ? !nextIsDop : nextAtOutputRate);
            return joins ? TrackEndAction.Splice : TrackEndAction.Reopen;
        }

        if (nextMidSave)
            return TrackEndAction.WaitForSave;

        return hasNext ? TrackEndAction.OpenNext : TrackEndAction.Finish;
    }
}
