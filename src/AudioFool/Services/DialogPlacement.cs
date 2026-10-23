using System.Windows;
using System.Windows.Media;

namespace AudioFool.Services;

/// <summary>
/// Where a window opened from the main window goes: centred over the main window
/// as it is on screen, and kept on that window's monitor (session 71; Find and
/// Replace's way since session 63). Before, six windows centred on the main
/// window's Left and Top, which maximised are where it would restore to, and
/// kept to the primary monitor's work area, so on a second monitor they opened
/// on the first.
/// </summary>
public static class DialogPlacement
{
    /// <summary>Centres <paramref name="dialog"/>, at this size, over <paramref name="owner"/> on the owner's monitor.</summary>
    public static void CentreOverOwner(Window dialog, Window owner, double width, double height)
    {
        var work = WorkAreaOf(owner);
        var corner = CentreOver(OwnerFrame(owner, work), work, width, height);
        dialog.Left = corner.X;
        dialog.Top = corner.Y;
    }

    /// <summary>The top-left corner that centres a window of this size over <paramref name="frame"/>, kept on the work area.</summary>
    public static Point CentreOver(Rect frame, Rect work, double width, double height) =>
        new(Math.Clamp(frame.Left + ((frame.Width - width) / 2), work.Left, Math.Max(work.Left, work.Right - width)),
            Math.Clamp(frame.Top + ((frame.Height - height) / 2), work.Top, Math.Max(work.Top, work.Bottom - height)));

    /// <summary>
    /// Where the main window is on screen. Maximised, that is its monitor's work
    /// area: its Left and Top then say where it would restore to.
    /// </summary>
    public static Rect OwnerFrame(Window owner, Rect work) =>
        owner.WindowState == WindowState.Maximized
            ? work
            : new Rect(owner.Left, owner.Top, owner.ActualWidth, owner.ActualHeight);

    /// <summary>The work area of the monitor <paramref name="window"/> is mostly on, in WPF's units; the primary monitor's if it can't be had.</summary>
    public static Rect WorkAreaOf(Window window)
    {
        if (WindowPlacement.WorkAreaOf(window) is not { } area)
            return SystemParameters.WorkArea;

        var dpi = VisualTreeHelper.GetDpi(window);
        return new Rect(area.Left / dpi.DpiScaleX, area.Top / dpi.DpiScaleY,
                        (area.Right - area.Left) / dpi.DpiScaleX, (area.Bottom - area.Top) / dpi.DpiScaleY);
    }

    /// <summary>Moves <paramref name="window"/> back onto the monitor it is on, if part of it has gone past an edge.</summary>
    public static void KeepOnScreen(Window window)
    {
        var work = WorkAreaOf(window);
        window.Left = Math.Clamp(window.Left, work.Left, Math.Max(work.Left, work.Right - window.ActualWidth));
        window.Top = Math.Clamp(window.Top, work.Top, Math.Max(work.Top, work.Bottom - window.ActualHeight));
    }
}
