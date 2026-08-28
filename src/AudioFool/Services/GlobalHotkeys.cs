using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace AudioFool.Services;

/// <summary>
/// System-wide playback keys, working whether or not AudioFool has focus.
/// <para>
/// Windows grants a hotkey exclusively to whoever registers it first, so these keys
/// stop reaching every other application - F11 will no longer toggle fullscreen in a
/// browser, for instance. That is inherent to <c>RegisterHotKey</c>, not a choice
/// made here, which is why registration failures are surfaced rather than swallowed:
/// a key already owned by something else simply won't work.
/// </para>
/// </summary>
public sealed class GlobalHotkeys : IDisposable
{
    public enum Action
    {
        PlayPause,
        Previous,
        Next,
    }

    private const int WmHotkey = 0x0312;

    /// <summary>Suppresses auto-repeat, so holding Next doesn't skip a whole album.</summary>
    private const uint ModNoRepeat = 0x4000;

    private const uint VkF9 = 0x78;
    private const uint VkF10 = 0x79;
    private const uint VkF11 = 0x7A;

    private static readonly (Action Action, uint Key, string Name)[] Bindings =
    [
        (Action.PlayPause, VkF9, "F9"),
        (Action.Previous, VkF10, "F10"),
        (Action.Next, VkF11, "F11"),
    ];

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    private readonly Dictionary<int, Action> _registered = [];
    private HwndSource? _source;
    private IntPtr _handle;
    private bool _disposed;

    /// <summary>Raised on the UI thread when one of the keys is pressed.</summary>
    public event EventHandler<Action>? Pressed;

    /// <summary>Key names another application already owns, so they won't work here.</summary>
    public IReadOnlyList<string> Unavailable { get; private set; } = [];

    /// <summary>
    /// Claims the keys. Must be called once the window has a handle - from
    /// <c>SourceInitialized</c> or later.
    /// </summary>
    public void Register(Window window)
    {
        _handle = new WindowInteropHelper(window).Handle;
        if (_handle == IntPtr.Zero)
            return;

        _source = HwndSource.FromHwnd(_handle);
        _source?.AddHook(WndProc);

        var unavailable = new List<string>();
        var id = 0;

        foreach (var (action, key, name) in Bindings)
        {
            id++;

            if (RegisterHotKey(_handle, id, ModNoRepeat, key))
                _registered[id] = action;
            else
                unavailable.Add(name);
        }

        Unavailable = unavailable;
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != WmHotkey || !_registered.TryGetValue(wParam.ToInt32(), out var action))
            return IntPtr.Zero;

        Pressed?.Invoke(this, action);
        handled = true;
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        // Released explicitly: a hotkey left registered stays claimed until the
        // process dies, which would block the next instance from taking it.
        if (_handle != IntPtr.Zero)
        {
            foreach (var id in _registered.Keys)
                UnregisterHotKey(_handle, id);
        }

        _registered.Clear();
        _source?.RemoveHook(WndProc);
        _source = null;
    }
}
