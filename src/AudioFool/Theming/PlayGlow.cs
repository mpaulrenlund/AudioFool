using System.Windows;

namespace AudioFool.Theming;

/// <summary>
/// Whether Play/Pause's glow is lit (PS2 Dark spec 7.3): only while a song is
/// playing, the user's call (2026-11-10, session 77). The main window binds it to
/// <c>IsPlaying</c>, the state that shows the pause bars, so the glow comes and
/// goes with the icon, at once. <c>theme.transportButton</c>'s template gives the
/// glow only to a Play/Pause button with this set; the ring stays either way.
/// Off unless set. Light's glow is clear, so there it changes nothing.
/// </summary>
public static class PlayGlow
{
    public static readonly DependencyProperty LitProperty = DependencyProperty.RegisterAttached(
        "Lit", typeof(bool), typeof(PlayGlow), new PropertyMetadata(false));

    public static bool GetLit(DependencyObject element) => (bool)element.GetValue(LitProperty);

    public static void SetLit(DependencyObject element, bool value) => element.SetValue(LitProperty, value);
}
