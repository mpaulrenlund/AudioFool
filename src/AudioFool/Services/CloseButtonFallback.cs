using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using Wpf.Ui.Controls;

namespace AudioFool.Services;

/// <summary>
/// Closes a window when a press and a release both land on its title bar's close
/// button but WPF-UI's own click never comes (the user's report, session 51: now
/// and then the X of Statistics or a tag window did nothing).
/// <para>
/// WPF-UI doesn't click its title-bar buttons through WPF input: it watches the
/// window's WM_NC* messages, marks the button pressed on WM_NCLBUTTONDOWN and
/// clicks on WM_NCLBUTTONUP, and any WM_NCMOUSELEAVE or missed hit test in
/// between clears the mark, losing the click without a trace. Off-screen, with
/// the messages sent in order, it closed every time, so what loses it only comes
/// with a real mouse.
/// </para>
/// <para>
/// This watches the same messages as they leave the thread's queue
/// (<see cref="ComponentDispatcher.ThreadPreprocessMessage"/>), before any window
/// hook can swallow them. Their wParam is the hit test, HTCLOSE over the X. After
/// a release there it waits until input has been handled; if WPF-UI raised
/// <see cref="TitleBar.CloseClickedEvent"/> meanwhile it does nothing, so a close
/// the window itself cancelled (the tag windows' unsaved-changes prompt) stays
/// cancelled. Otherwise it closes the window, as WPF-UI's click would have.
/// </para>
/// </summary>
public static class CloseButtonFallback
{
    private const int WM_NCLBUTTONDOWN = 0x00A1;
    private const int WM_NCLBUTTONUP = 0x00A2;
    private const int HTCLOSE = 20;

    private static readonly ConditionalWeakTable<Window, object> Watched = new();
    private static bool _installed;

    // The window whose X the left button went down on, and the window WPF-UI
    // last raised CloseClicked for. One pointer, one press at a time.
    private static IntPtr _pressedOn;
    private static Window? _clickedByTitleBar;

    /// <summary>Call from the window's constructor, on the UI thread.</summary>
    public static void Attach(Window window)
    {
        if (!_installed)
        {
            ComponentDispatcher.ThreadPreprocessMessage += OnThreadMessage;
            _installed = true;
        }

        Watched.AddOrUpdate(window, new object());
        // A plain RoutedEventHandler: by the time the event reaches the window its
        // sender is the window, and WPF-UI's TypedEventHandler<TitleBar, ...> type
        // would throw on that (found by ThemeLab --window closex).
        window.AddHandler(TitleBar.CloseClickedEvent,
            new RoutedEventHandler((_, _) => _clickedByTitleBar = window));
    }

    private static void OnThreadMessage(ref MSG msg, ref bool handled)
    {
        switch (msg.message)
        {
            case WM_NCLBUTTONDOWN:
                _pressedOn = (int)msg.wParam == HTCLOSE ? msg.hwnd : IntPtr.Zero;
                _clickedByTitleBar = null;
                break;

            case WM_NCLBUTTONUP:
                var pressedOn = _pressedOn;
                _pressedOn = IntPtr.Zero;
                if ((int)msg.wParam != HTCLOSE || msg.hwnd != pressedOn)
                    break;

                if (HwndSource.FromHwnd(msg.hwnd)?.RootVisual is Window window && Watched.TryGetValue(window, out _))
                {
                    // WPF-UI's click is queued at Input priority; Background runs after it.
                    window.Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
                    {
                        if (ReferenceEquals(_clickedByTitleBar, window) || !window.IsVisible)
                            return;

                        window.Close();
                    });
                }
                break;
        }
    }
}
