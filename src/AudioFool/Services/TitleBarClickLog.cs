using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using AudioFool.Core.Playback;
using Wpf.Ui.Controls;

namespace AudioFool.Services;

/// <summary>
/// A log, in playback.log, of the clicks that reach a window's title-bar buttons,
/// to catch why the X of Statistics or a tag window sometimes ignores every click
/// (the user's report, sessions 51, 59 and 66). Nothing here changes what a click
/// does; <see cref="CloseButtonSafetyNet"/> clicks an X whose click WPF-UI lost.
/// <para>
/// WPF-UI 4.3.0 clicks its title-bar buttons only from WM_NC* messages: it marks
/// the X pressed on WM_NCLBUTTONDOWN over it, clicks on WM_NCLBUTTONUP, and a
/// WM_NCMOUSELEAVE or a hit test elsewhere in between clears the mark. While
/// anything in the window holds the mouse captured, Windows sends a click on the
/// X as a plain WM_LBUTTONDOWN / WM_LBUTTONUP instead, which WPF-UI never sees.
/// So each line says which kind arrived, where, the X's own pressed and hovered
/// state, who holds the capture, and whether the window was enabled and active.
/// </para>
/// <para>
/// Read off the thread's queue (<see cref="ComponentDispatcher.ThreadPreprocessMessage"/>),
/// before any window hook can swallow a message, so the X's state is as it was
/// before WPF-UI saw it. Logged: every non-client press and release, a client
/// press or release over a title-bar button, a WM_NCMOUSELEAVE while the X is
/// marked pressed, WPF-UI's CloseClicked, and after each release over the X,
/// whether the window is still open.
/// </para>
/// <para>
/// Hit tests (WM_NCHITTEST) are sent, never queued, so a hook on this thread's
/// window procedures logs those instead: each one between a press on the X and
/// its release, with the spot it asks about, where the pointer really is, which
/// button is there, and whether it came from this thread (Windows handling the
/// mouse) or another (another program asking). Session 66 found the X's mark
/// gone by the release with neither a capture nor a WM_NCMOUSELEAVE to blame.
/// </para>
/// </summary>
public static class TitleBarClickLog
{
    private const int WM_NCHITTEST = 0x0084;
    private const int WM_NCLBUTTONDOWN = 0x00A1;
    private const int WM_NCLBUTTONUP = 0x00A2;
    private const int WM_NCLBUTTONDBLCLK = 0x00A3;
    private const int WM_LBUTTONDOWN = 0x0201;
    private const int WM_LBUTTONUP = 0x0202;
    private const int WM_LBUTTONDBLCLK = 0x0203;
    private const int WM_NCMOUSELEAVE = 0x02A2;
    private const int WH_CALLWNDPROC = 4;
    private const uint ISMEX_NOSEND = 0;

    private static readonly FieldInfo? ButtonsField =
        typeof(TitleBar).GetField("_buttons", BindingFlags.Instance | BindingFlags.NonPublic);
    private static readonly FieldInfo? ClickedDownField =
        typeof(TitleBarButton).GetField("_isClickedDown", BindingFlags.Instance | BindingFlags.NonPublic);

    private sealed class Entry
    {
        public TitleBar? Bar;
        public bool CloseClicked;
    }

    private static readonly ConditionalWeakTable<Window, Entry> Windows = new();

    // A press on an X whose release hasn't come yet: the hit tests its window gets
    // in between are logged. One pointer, one press at a time.
    private sealed record Press(IntPtr Hwnd, Window Window, TitleBar Bar, long At);
    private static Press? _pressOnClose;

    private delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);

    // Kept in a field for as long as the hook is in: a collected delegate would crash it.
    private static readonly HookProc CallWndProc = OnCallWndProc;
    private static IntPtr _hook;

    /// <summary>Call on the UI thread.</summary>
    public static void Install()
    {
        ComponentDispatcher.ThreadPreprocessMessage += OnThreadMessage;
        _hook = SetWindowsHookEx(WH_CALLWNDPROC, CallWndProc, IntPtr.Zero, GetCurrentThreadId());
    }

    private static void OnThreadMessage(ref MSG msg, ref bool handled)
    {
        var name = msg.message switch
        {
            WM_NCLBUTTONDOWN => "NC down",
            WM_NCLBUTTONUP => "NC up",
            WM_NCLBUTTONDBLCLK => "NC double",
            WM_LBUTTONDOWN => "client down",
            WM_LBUTTONUP => "client up",
            WM_LBUTTONDBLCLK => "client double",
            WM_NCMOUSELEAVE => "NC mouse leave",
            _ => null,
        };
        if (name is null)
            return;

        try
        {
            Log(msg, name);
        }
        catch (Exception ex)
        {
            // A log must never break a click.
            PlaybackDiagnostics.Note($"titlebar: log failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static void Log(MSG msg, string name)
    {
        if (HwndSource.FromHwnd(msg.hwnd)?.RootVisual is not Window window)
            return;

        var entry = Windows.GetValue(window, w =>
        {
            var created = new Entry();
            // A plain RoutedEventHandler: at the window the sender is the window,
            // which WPF-UI's TypedEventHandler<TitleBar, ...> would throw on.
            w.AddHandler(TitleBar.CloseClickedEvent, new RoutedEventHandler((_, _) =>
            {
                created.CloseClicked = true;
                PlaybackDiagnostics.Note($"titlebar: {Describe(w)} WPF-UI raised CloseClicked");
            }));
            return created;
        });

        entry.Bar ??= FindTitleBar(window);
        if (entry.Bar is not { } bar || ButtonsField?.GetValue(bar) is not TitleBarButton?[] buttons)
            return;

        var close = buttons.Length > 2 ? buttons[2] : null;
        var clickedDown = close is not null && ClickedDownField?.GetValue(close) is true;

        // Non-client messages carry screen coordinates, client ones client coordinates.
        var nonClient = msg.message is WM_NCLBUTTONDOWN or WM_NCLBUTTONUP or WM_NCLBUTTONDBLCLK or WM_NCMOUSELEAVE;
        var point = new POINT { X = (short)((long)msg.lParam & 0xFFFF), Y = (short)(((long)msg.lParam >> 16) & 0xFFFF) };
        if (!nonClient)
            ClientToScreen(msg.hwnd, ref point);

        var over = msg.message == WM_NCMOUSELEAVE ? null : ButtonUnder(buttons, point);

        var press = msg.message is WM_NCLBUTTONDOWN or WM_NCLBUTTONDBLCLK or WM_LBUTTONDOWN or WM_LBUTTONDBLCLK;
        if (press)
            _pressOnClose = over == "Close" ? new Press(msg.hwnd, window, bar, Stopwatch.GetTimestamp()) : null;
        else if (msg.message is WM_NCLBUTTONUP or WM_LBUTTONUP)
            _pressOnClose = null;

        // A client click elsewhere in the window is ordinary use; a mouse leave
        // only matters while it can clear the X's mark.
        if (!nonClient && over is null)
            return;
        if (msg.message == WM_NCMOUSELEAVE && !clickedDown)
            return;

        var capture = GetCapture();
        var captureText = capture == IntPtr.Zero ? "none" : capture == msg.hwnd ? "this window" : $"other window {capture}";
        var hitTest = nonClient && msg.message != WM_NCMOUSELEAVE ? $" hit {HitTestName((int)msg.wParam)}" : "";

        PlaybackDiagnostics.Note(
            $"titlebar: {Describe(window)} {name}{hitTest} at {point.X},{point.Y} over {over ?? "nothing"}; " +
            $"X pressed {clickedDown}, hovered {close?.IsHovered}; capture {captureText}, WPF capture " +
            $"{(Mouse.Captured is FrameworkElement f ? $"{f.GetType().Name} '{f.Name}'" : Mouse.Captured?.GetType().Name ?? "none")}; " +
            $"enabled {IsWindowEnabled(msg.hwnd)}, active {window.IsActive}, modal {ComponentDispatcher.IsThreadModal}");

        if (press)
            entry.CloseClicked = false;

        if (msg.message is WM_NCLBUTTONUP or WM_LBUTTONUP && over == "Close")
        {
            // WPF-UI's click is queued at Input priority and the safety net's at
            // Background; ContextIdle runs after both.
            window.Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, () =>
                PlaybackDiagnostics.Note(
                    $"titlebar: {Describe(window)} after the release on X: CloseClicked {entry.CloseClicked}, " +
                    $"window {(window.IsVisible ? "STILL OPEN" : "closed")}"));
        }
    }

    private static IntPtr OnCallWndProc(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0 && _pressOnClose is { } press)
        {
            try
            {
                var sent = Marshal.PtrToStructure<CWPSTRUCT>(lParam);
                // The hook's own wParam can't tell: it reads "this thread" for a
                // send from another thread of the process (ThemeLab --window closex).
                if (sent.message == WM_NCHITTEST && sent.hwnd == press.Hwnd)
                    LogHitTest(press, sent.lParam, fromThisThread: InSendMessageEx(IntPtr.Zero) == ISMEX_NOSEND);
            }
            catch (Exception ex)
            {
                // A log must never break a message.
                PlaybackDiagnostics.Note($"titlebar: hit-test log failed: {ex.GetType().Name}: {ex.Message}");
            }
        }

        return CallNextHookEx(_hook, code, wParam, lParam);
    }

    private static void LogHitTest(Press press, IntPtr lParam, bool fromThisThread)
    {
        // A press this old was let go somewhere the log didn't see.
        if (Stopwatch.GetElapsedTime(press.At) > TimeSpan.FromSeconds(3))
        {
            _pressOnClose = null;
            return;
        }
        if (ButtonsField?.GetValue(press.Bar) is not TitleBarButton?[] buttons)
            return;

        var close = buttons.Length > 2 ? buttons[2] : null;
        var point = new POINT { X = (short)((long)lParam & 0xFFFF), Y = (short)(((long)lParam >> 16) & 0xFFFF) };
        GetCursorPos(out var pointer);

        PlaybackDiagnostics.Note(
            $"titlebar: {Describe(press.Window)} hit test at {point.X},{point.Y} from {(fromThisThread ? "this thread" : "another thread")}, " +
            $"pointer at {pointer.X},{pointer.Y}, over {ButtonUnder(buttons, point) ?? "nothing"}; " +
            $"X pressed {close is not null && ClickedDownField?.GetValue(close) is true}, hovered {close?.IsHovered}");
    }

    private static string? ButtonUnder(TitleBarButton?[] buttons, POINT point)
    {
        foreach (var button in buttons)
        {
            if (button is not { IsVisible: true } || PresentationSource.FromVisual(button) is null)
                continue;

            var local = button.PointFromScreen(new Point(point.X, point.Y));
            if (local.X >= 0 && local.Y >= 0 && local.X < button.ActualWidth && local.Y < button.ActualHeight)
                return button.ButtonType.ToString();
        }
        return null;
    }

    private static TitleBar? FindTitleBar(DependencyObject node)
    {
        if (node is TitleBar bar)
            return bar;

        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
        {
            if (FindTitleBar(VisualTreeHelper.GetChild(node, i)) is { } found)
                return found;
        }
        return null;
    }

    private static string Describe(Window window) =>
        string.IsNullOrEmpty(window.Title) ? window.GetType().Name : $"{window.GetType().Name} '{window.Title}'";

    private static string HitTestName(int code) => code switch
    {
        1 => "client",
        2 => "caption",
        3 => "sysmenu",
        8 => "minimize",
        9 => "maximize",
        20 => "CLOSE",
        _ => code.ToString(),
    };

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CWPSTRUCT
    {
        public IntPtr lParam;
        public IntPtr wParam;
        public uint message;
        public IntPtr hwnd;
    }

    [DllImport("user32.dll")]
    private static extern bool ClientToScreen(IntPtr hWnd, ref POINT point);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT point);

    [DllImport("user32.dll")]
    private static extern IntPtr GetCapture();

    [DllImport("user32.dll")]
    private static extern bool IsWindowEnabled(IntPtr hWnd);

    [DllImport("user32.dll", EntryPoint = "SetWindowsHookExW")]
    private static extern IntPtr SetWindowsHookEx(int idHook, HookProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern uint InSendMessageEx(IntPtr reserved);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();
}
