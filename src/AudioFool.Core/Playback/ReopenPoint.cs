using AudioFool.Core.Models;

namespace AudioFool.Core.Playback;

/// <summary>
/// Where a track that is closed and opened again carries on from: a save that
/// lets go of the playing file, a device change, Bit-Perfect switched mid-track,
/// About's loan of the DAC (session 67). Until then it was the decoder's place,
/// up to 0.2 s past what had been heard, so the reopen skipped that much.
/// </summary>
public static class ReopenPoint
{
    /// <param name="current">The track being reopened.</param>
    /// <param name="heard">What the speakers were playing (<see cref="AudioEngine.Heard"/>; held still while paused).</param>
    /// <param name="decoded">Where the decoder was in <paramref name="current"/>.</param>
    public static TimeSpan Choose(Track current, HeardPoint heard, TimeSpan decoded)
    {
        // Silence, or the device unable to say: the decoder's place, as before.
        if (heard.Track is null)
            return decoded;

        // The track before it is still playing out of the device (a gapless join
        // not yet heard): none of this one has been heard.
        if (!ReferenceEquals(heard.Track, current))
            return TimeSpan.Zero;

        return heard.Position < TimeSpan.Zero ? TimeSpan.Zero
            : heard.Position > decoded ? decoded
            : heard.Position;
    }
}
