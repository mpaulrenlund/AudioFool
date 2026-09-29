using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using AudioFool.Core.Settings;

namespace AudioFool.Services;

/// <summary>
/// Saves and restores where the main window sits, through Win32 rather than
/// WPF's Left/Top/Width/Height: those are device-independent units, which mean
/// a different number of pixels on each monitor under per-monitor DPI, and they
/// don't see Aero Snap.
/// </summary>
public static class WindowPlacement
{
    /// <summary>
    /// The window's normal rectangle and whether it is maximised. A snapped window
    /// is taken at its snapped position (what the user sees) rather than the
    /// pre-snap one Windows keeps; a minimised one at where it restores to.
    /// </summary>
    public static WindowBounds? Capture(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero)
            return null;

        var placement = new WINDOWPLACEMENT { length = Marshal.SizeOf<WINDOWPLACEMENT>() };
        if (!GetWindowPlacement(hwnd, ref placement))
            return null;

        var maximized = placement.showCmd == SW_SHOWMAXIMIZED
                        || (placement.showCmd == SW_SHOWMINIMIZED && (placement.flags & WPF_RESTORETOMAXIMIZED) != 0);

        RECT rect;
        if (placement.showCmd == SW_SHOWNORMAL)
        {
            if (!GetWindowRect(hwnd, out rect))
                return null;
        }
        else
        {
            rect = WorkspaceToScreen(placement.rcNormalPosition);
        }

        return new WindowBounds(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top, maximized);
    }

    /// <summary>
    /// Puts the window at <paramref name="bounds"/> if that is still on a screen,
    /// and returns whether it did. Call it once the window has a handle but
    /// before it is shown (SourceInitialized), so it never appears anywhere else.
    /// </summary>
    public static bool Apply(Window window, WindowBounds bounds)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero || !bounds.IsReachableOn(WorkAreas()))
            return false;

        // Landing on a monitor with a different DPI makes Windows rescale the
        // window on the way, so the size is set a second time once it's there.
        for (var attempt = 0; attempt < 2; attempt++)
        {
            SetWindowPos(hwnd, IntPtr.Zero, bounds.Left, bounds.Top, bounds.Width, bounds.Height,
                SWP_NOZORDER | SWP_NOACTIVATE | SWP_NOOWNERZORDER);

            if (GetWindowRect(hwnd, out var now)
                && now.Left == bounds.Left && now.Top == bounds.Top
                && now.Right - now.Left == bounds.Width && now.Bottom - now.Top == bounds.Height)
                break;
        }

        // Maximises on the monitor it now sits on, and restores to the rectangle above.
        if (bounds.Maximized)
            window.WindowState = WindowState.Maximized;

        return true;
    }

    /// <summary>Every connected monitor's work area (the screen less the taskbar).</summary>
    public static List<PixelRect> WorkAreas()
    {
        var areas = new List<PixelRect>();
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (monitor, _, _, _) =>
        {
            var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
            if (GetMonitorInfo(monitor, ref info))
                areas.Add(new PixelRect(info.rcWork.Left, info.rcWork.Top, info.rcWork.Right, info.rcWork.Bottom));
            return true;
        }, IntPtr.Zero);
        return areas;
    }

    /// <summary>
    /// GetWindowPlacement's rectangle is in workspace coordinates, which are
    /// offset from screen coordinates when the taskbar sits on the top or left.
    /// </summary>
    private static RECT WorkspaceToScreen(RECT rect)
    {
        var monitor = MonitorFromRect(ref rect, MONITOR_DEFAULTTONEAREST);
        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (monitor == IntPtr.Zero || !GetMonitorInfo(monitor, ref info))
            return rect;

        var dx = info.rcWork.Left - info.rcMonitor.Left;
        var dy = info.rcWork.Top - info.rcMonitor.Top;
        return new RECT { Left = rect.Left + dx, Top = rect.Top + dy, Right = rect.Right + dx, Bottom = rect.Bottom + dy };
    }

    private const int SW_SHOWNORMAL = 1;
    private const int SW_SHOWMINIMIZED = 2;
    private const int SW_SHOWMAXIMIZED = 3;
    private const int WPF_RESTORETOMAXIMIZED = 2;
    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_NOOWNERZORDER = 0x0200;
    private const uint MONITOR_DEFAULTTONEAREST = 2;

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X, Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WINDOWPLACEMENT
    {
        public int length;
        public int flags;
        public int showCmd;
        public POINT ptMinPosition;
        public POINT ptMaxPosition;
        public RECT rcNormalPosition;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    private delegate bool MonitorEnumProc(IntPtr monitor, IntPtr hdc, IntPtr rect, IntPtr data);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowPlacement(IntPtr hWnd, ref WINDOWPLACEMENT lpwndpl);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromRect(ref RECT lprc, uint dwFlags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr lprcClip, MonitorEnumProc lpfnEnum, IntPtr dwData);
}
