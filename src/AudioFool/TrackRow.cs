using System.Windows;

namespace AudioFool;

/// <summary>
/// Marks the song table's now-playing row (spec 6.6). Set on the
/// <c>DataGridRow</c> by its style and inherited by everything inside it, so
/// the row's fill and each cell's text can trigger on it directly rather than
/// each repeating the comparison with <c>NowPlaying</c>.
/// </summary>
public static class TrackRow
{
    public static readonly DependencyProperty IsNowPlayingProperty =
        DependencyProperty.RegisterAttached(
            "IsNowPlaying",
            typeof(bool),
            typeof(TrackRow),
            new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.Inherits));

    public static void SetIsNowPlaying(DependencyObject target, bool value) =>
        target.SetValue(IsNowPlayingProperty, value);

    public static bool GetIsNowPlaying(DependencyObject target) =>
        (bool)target.GetValue(IsNowPlayingProperty);
}
