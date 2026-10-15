using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using AudioFool.Core.Theme;

namespace AudioFool.Theming;

/// <summary>
/// What is painted round and behind each window's content, so that it follows a
/// theme switch too.
/// <list type="bullet">
/// <item>The border Windows draws (PS2 Dark spec 3.3, item 5). On Windows 11 DWM
/// draws a 1 px border round a window with rounded corners, a light one unless the
/// window says it is dark. <c>DWMWA_USE_IMMERSIVE_DARK_MODE</c> says so: set in Dark,
/// cleared in Light, on each window once it has a handle, and on every open window
/// at a switch.</item>
/// <item>The window's own background, under the content (every window paints
/// <c>color.window.bg</c> over it). WPF-UI copies its <c>ApplicationBackgroundBrush</c>
/// into it as a fixed value when the window opens; a reference to the key instead
/// follows WPF-UI's base dictionary when a switch swaps it.</item>
/// </list>
/// </summary>
internal static class WindowFrames
{
    private const int DwmwaUseImmersiveDarkMode = 20;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    // What each window was last told, so a resize doesn't tell it again.
    private static readonly ConditionalWeakTable<Window, object> Told = new();

    private static bool _dark;
    private static bool _registered;

    /// <summary>Tells every open window, and from now on every new one, which theme is on.</summary>
    public static void Follow(AppTheme theme)
    {
        _dark = theme == AppTheme.Dark;

        if (!_registered)
        {
            // A window's first SizeChanged comes once its handle exists, as it is
            // shown; Loaded would be no good, as a class handler for it only runs
            // on elements that also have a handler of their own.
            EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.SizeChangedEvent,
                new SizeChangedEventHandler((sender, _) => Tell((Window)sender)));
            _registered = true;
        }

        foreach (Window window in Application.Current.Windows)
            Tell(window);
    }

    private static void Tell(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero)
            return;
        if (Told.TryGetValue(window, out var told) && told is bool dark && dark == _dark)
            return;

        // Again at every switch: WPF-UI's fixed copy can come after this, as the
        // window's handle starts up.
        window.SetResourceReference(Control.BackgroundProperty, "ApplicationBackgroundBrush");

        var value = _dark ? 1 : 0;
        _ = DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkMode, ref value, sizeof(int));
        Told.AddOrUpdate(window, _dark);
    }
}
