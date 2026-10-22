using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using AudioFool.Core.Playback;
using Wpf.Ui.Controls;

namespace AudioFool.Services;

/// <summary>
/// Clicks a window's X itself when a press and a release both land on it but
/// WPF-UI loses the click (the user's report, sessions 51, 59 and 66: now and then
/// the X of Statistics or a tag window ignores every click for minutes).
/// <para>
/// WPF-UI 4.3.0 clicks its title-bar buttons from WM_NC* messages, not WPF input:
/// it marks the X pressed on WM_NCLBUTTONDOWN and clicks on WM_NCLBUTTONUP, and a
/// hit test (WM_NCHITTEST) for a spot off the X, or a WM_NCMOUSELEAVE, in between
/// clears the mark. Session 66's log caught 19 clicks in a row lost that way, with
/// nothing holding the mouse and the pointer on the X throughout. WPF-UI also never
/// counts a WM_NCLBUTTONDBLCLK, the second press of a quick double-click.
/// </para>
/// <para>
/// This watches the presses and releases as they leave the thread's queue
/// (<see cref="ComponentDispatcher.ThreadPreprocessMessage"/>) and never handles
/// one. After a release on the X that followed a press on it, it waits until input
/// has run. If WPF-UI raised <see cref="TitleBar.CloseClickedEvent"/> meanwhile it
/// does nothing; otherwise it clicks the X through WPF-UI's own
/// <see cref="TitleBarButton.InvokeClick"/>, so the window closes as WPF-UI's click
/// closes it, and a close the window cancels (the tag windows' unsaved-changes
/// prompt) stays cancelled. A click on the X while something holds the mouse
/// captured arrives as a client click, and counts the same.
/// </para>
/// </summary>
public static class CloseButtonSafetyNet
{
    private const int WM_NCLBUTTONDOWN = 0x00A1;
    private const int WM_NCLBUTTONUP = 0x00A2;
    private const int WM_NCLBUTTONDBLCLK = 0x00A3;
    private const int WM_LBUTTONDOWN = 0x0201;
    private const int WM_LBUTTONUP = 0x0202;
    private const int WM_LBUTTONDBLCLK = 0x0203;

    private sealed class Watch
    {
        public TitleBarButton? Close;
        public bool LookedUp;
        public bool PressedOnClose;
        public bool CloseClicked;
    }

    private static readonly ConditionalWeakTable<Window, Watch> Windows = new();

    public static void Install() => ComponentDispatcher.ThreadPreprocessMessage += OnThreadMessage;

    private static void OnThreadMessage(ref MSG msg, ref bool handled)
    {
        if (msg.message is not (WM_NCLBUTTONDOWN or WM_NCLBUTTONDBLCLK or WM_NCLBUTTONUP
            or WM_LBUTTONDOWN or WM_LBUTTONDBLCLK or WM_LBUTTONUP))
            return;

        try
        {
            Follow(msg);
        }
        catch (Exception ex)
        {
            // A safety net must never break a click.
            PlaybackDiagnostics.Note($"titlebar: safety net failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static void Follow(MSG msg)
    {
        if (HwndSource.FromHwnd(msg.hwnd)?.RootVisual is not Window window)
            return;

        var watch = Windows.GetValue(window, w =>
        {
            var created = new Watch();
            // A plain RoutedEventHandler: at the window the sender is the window,
            // which WPF-UI's TypedEventHandler<TitleBar, ...> would throw on.
            w.AddHandler(TitleBar.CloseClickedEvent, new RoutedEventHandler((_, _) => created.CloseClicked = true));
            return created;
        });

        // Looked up once, and again if a theme switch rebuilt the title bar's template.
        if (!watch.LookedUp || (watch.Close is not null && PresentationSource.FromVisual(watch.Close) is null))
        {
            watch.Close = FindClose(window);
            watch.LookedUp = true;
        }
        if (watch.Close is not { } close)
            return;

        var onClose = IsOver(close, ScreenPoint(msg));

        if (msg.message is WM_NCLBUTTONUP or WM_LBUTTONUP)
        {
            var pressedOnClose = watch.PressedOnClose;
            watch.PressedOnClose = false;
            if (!pressedOnClose || !onClose)
                return;

            // WPF-UI's own click is queued at Input priority; Background runs after it.
            window.Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
            {
                if (watch.CloseClicked || !window.IsVisible || !close.IsEnabled || PresentationSource.FromVisual(close) is null)
                    return;

                PlaybackDiagnostics.Note($"titlebar: {Describe(window)} WPF-UI lost the click on the X; the safety net clicked it");
                close.InvokeClick();
            });
            return;
        }

        // A press, or the second press of a double-click.
        watch.PressedOnClose = onClose;
        watch.CloseClicked = false;
    }

    private static Point ScreenPoint(MSG msg)
    {
        // Non-client messages carry screen coordinates, client ones client coordinates.
        var point = new POINT { X = (short)((long)msg.lParam & 0xFFFF), Y = (short)(((long)msg.lParam >> 16) & 0xFFFF) };
        if (msg.message is WM_LBUTTONDOWN or WM_LBUTTONUP or WM_LBUTTONDBLCLK)
            ClientToScreen(msg.hwnd, ref point);
        return new Point(point.X, point.Y);
    }

    private static bool IsOver(TitleBarButton button, Point screen)
    {
        if (!button.IsVisible || PresentationSource.FromVisual(button) is null)
            return false;

        var local = button.PointFromScreen(screen);
        return local.X >= 0 && local.Y >= 0 && local.X < button.ActualWidth && local.Y < button.ActualHeight;
    }

    private static TitleBarButton? FindClose(DependencyObject node)
    {
        if (node is TitleBarButton { ButtonType: TitleBarButtonType.Close } close)
            return close;

        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
        {
            if (FindClose(VisualTreeHelper.GetChild(node, i)) is { } found)
                return found;
        }
        return null;
    }

    private static string Describe(Window window) =>
        string.IsNullOrEmpty(window.Title) ? window.GetType().Name : $"{window.GetType().Name} '{window.Title}'";

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll")]
    private static extern bool ClientToScreen(IntPtr hWnd, ref POINT point);
}
