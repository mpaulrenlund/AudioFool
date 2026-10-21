namespace AudioFool.Core.Theme;

/// <summary>
/// The About intro (session 64): the PlayStation's start-up, the Sony screen and
/// then the PlayStation screen, timed to its sound. Each picture's opacity is a
/// function of the time since the sound started, so the pictures follow the
/// sound wherever it is, and a render can be made at any moment.
/// <para>
/// The Sony screen fades in from black, and out again just before
/// <see cref="LogoAt"/>, where the sound's chime starts (measured from the file:
/// the treble rises from 7.3 s). The PlayStation screen fades in from there, and
/// out over <see cref="EndFade"/> as the sound dies away at <see cref="Length"/>.
/// </para>
/// </summary>
public sealed record IntroTimeline(TimeSpan LogoAt, TimeSpan Fade, TimeSpan EndFade, TimeSpan Length)
{
    /// <summary>From the intro.* tokens.</summary>
    public static IntroTimeline FromTokens(IReadOnlyDictionary<string, double> numbers) => new(
        TimeSpan.FromMilliseconds(numbers["intro.logoAtMs"]),
        TimeSpan.FromMilliseconds(numbers["intro.fadeMs"]),
        TimeSpan.FromMilliseconds(numbers["intro.endFadeMs"]),
        TimeSpan.FromMilliseconds(numbers["intro.lengthMs"]));

    /// <summary>How visible each screen is at <paramref name="at"/>, 0 to 1. Black shows where neither is.</summary>
    public (double Sony, double PlayStation) Opacities(TimeSpan at)
    {
        var sony = Math.Min(Rise(at, TimeSpan.Zero, Fade), Fall(at, LogoAt - Fade, Fade));
        var playStation = Math.Min(Rise(at, LogoAt, Fade), Fall(at, Length - EndFade, EndFade));
        return (sony, playStation);
    }

    /// <summary>Whether the intro is over at <paramref name="at"/>.</summary>
    public bool IsOver(TimeSpan at) => at >= Length;

    private static double Rise(TimeSpan at, TimeSpan from, TimeSpan over) =>
        over <= TimeSpan.Zero
            ? (at >= from ? 1 : 0)
            : Math.Clamp((at - from) / over, 0, 1);

    private static double Fall(TimeSpan at, TimeSpan from, TimeSpan over) => 1 - Rise(at, from, over);
}
