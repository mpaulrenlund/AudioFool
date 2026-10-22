using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;

namespace AudioFool.Services;

/// <summary>
/// Hears drives being plugged in and pulled out (session 68). Windows tells every
/// top-level window when a volume arrives or goes (WM_DEVICECHANGE), with no
/// registration needed. One drive can bring several notices, and a drive just
/// plugged in can take a moment to answer, so <see cref="Changed"/> is raised
/// once the notices have stopped for <see cref="Settle"/>.
/// </summary>
public sealed class DriveWatcher : IDisposable
{
    private const int WmDeviceChange = 0x0219;
    private const int DbtDeviceArrival = 0x8000;
    private const int DbtDeviceRemoveComplete = 0x8004;
    private const int DbtDevTypVolume = 0x0002;

    /// <summary>How long after the last notice the change is reported.</summary>
    public static readonly TimeSpan Settle = TimeSpan.FromSeconds(1.5);

    /// <summary>The start of DEV_BROADCAST_HDR, which every notice's lParam points to.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct DevBroadcastHeader
    {
        public int Size;
        public int DeviceType;
        public int Reserved;
    }

    private readonly DispatcherTimer _settle;
    private HwndSource? _source;
    private bool _arrived;

    /// <summary>Raised on the UI thread: true when a drive arrived, false when drives only went.</summary>
    public event EventHandler<bool>? Changed;

    public DriveWatcher()
    {
        _settle = new DispatcherTimer { Interval = Settle };
        _settle.Tick += (_, _) =>
        {
            _settle.Stop();
            var arrived = _arrived;
            _arrived = false;
            Changed?.Invoke(this, arrived);
        };
    }

    /// <summary>Starts listening. Needs the window's handle: SourceInitialized or later.</summary>
    public void Attach(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero)
            return;

        _source = HwndSource.FromHwnd(handle);
        _source?.AddHook(WndProc);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != WmDeviceChange || lParam == IntPtr.Zero)
            return IntPtr.Zero;

        var kind = wParam.ToInt32();
        if (kind is not (DbtDeviceArrival or DbtDeviceRemoveComplete))
            return IntPtr.Zero;

        if (Marshal.PtrToStructure<DevBroadcastHeader>(lParam).DeviceType != DbtDevTypVolume)
            return IntPtr.Zero;

        _arrived |= kind == DbtDeviceArrival;

        // Every notice starts the wait again.
        _settle.Stop();
        _settle.Start();
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        _settle.Stop();
        _source?.RemoveHook(WndProc);
        _source = null;
    }
}
