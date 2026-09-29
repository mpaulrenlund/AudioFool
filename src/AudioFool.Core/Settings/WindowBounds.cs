namespace AudioFool.Core.Settings;

/// <summary>A rectangle in physical screen pixels, right and bottom exclusive.</summary>
public readonly record struct PixelRect(int Left, int Top, int Right, int Bottom)
{
    public int Width => Right - Left;
    public int Height => Bottom - Top;
}

/// <summary>
/// Where the main window was when it last closed: its normal (not maximised)
/// rectangle in physical screen pixels, and whether it was maximised on top of
/// that. Pixels rather than WPF's device-independent units, because with
/// per-monitor DPI a DIP means something different on each monitor.
/// </summary>
public sealed record WindowBounds(int Left, int Top, int Width, int Height, bool Maximized)
{
    /// <summary>How much of the title strip must be on a screen to grab it.</summary>
    public const int MinVisibleWidth = 120;

    /// <summary>The height of the band along the top that counts as the title strip.</summary>
    public const int TitleBandHeight = 32;

    /// <summary>
    /// Whether the window can go back here: enough of its title strip lands on
    /// one of the screens connected now to be seen and dragged. False when the
    /// monitor it was on is gone, or a remote session's screen is smaller, and
    /// the window would open where nobody can reach it.
    /// </summary>
    public bool IsReachableOn(IEnumerable<PixelRect> workAreas)
    {
        if (Width <= 0 || Height <= 0)
            return false;

        var band = new PixelRect(Left, Top, Left + Width, Top + Math.Min(TitleBandHeight, Height));

        return workAreas.Any(area =>
        {
            var width = Math.Min(band.Right, area.Right) - Math.Max(band.Left, area.Left);
            var height = Math.Min(band.Bottom, area.Bottom) - Math.Max(band.Top, area.Top);
            return width >= Math.Min(MinVisibleWidth, Width) && height >= band.Height / 2;
        });
    }
}
