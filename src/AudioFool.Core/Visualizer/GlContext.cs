using System.Runtime.InteropServices;

namespace AudioFool.Core.Visualizer;

/// <summary>
/// A plain Win32 window and an OpenGL context on it, for projectM, which in 4.1.6
/// can only draw into the window its context belongs to (framebuffer 0). The
/// visualizer hosts the window as a child of its picture; the preset screen uses
/// a hidden one. The context is the classic kind (<c>wglCreateContext</c>): the
/// driver's newest compatibility profile, which projectM's GLEW start-up accepts.
/// <para>
/// Created and used on one thread, which keeps the context current: the window
/// may belong to another (the UI thread), as it does in the visualizer.
/// </para>
/// </summary>
public sealed class GlContext : IDisposable
{
    private const string ClassName = "AudioFoolGL";
    private static readonly object ClassLock = new();
    private static bool _classRegistered;

    private readonly IntPtr _hwnd;
    private IntPtr _dc;
    private IntPtr _rc;

    private GlContext(IntPtr hwnd, IntPtr dc, IntPtr rc)
    {
        _hwnd = hwnd;
        _dc = dc;
        _rc = rc;
        Renderer = GlString(GlRenderer);
        Version = GlString(GlVersion);
    }

    /// <summary>The GPU and driver drawing, as OpenGL names them.</summary>
    public string Renderer { get; }

    public string Version { get; }

    /// <summary>
    /// A window for OpenGL: a child of <paramref name="parent"/> filling it, or
    /// with no parent a hidden one (never shown, never activated). Owns its device
    /// context and passes double-clicks on, for the visualizer's fullscreen.
    /// </summary>
    public static IntPtr CreateWindow(IntPtr parent, int width, int height)
    {
        RegisterClass();
        var style = parent != IntPtr.Zero ? WsChild | WsVisible | WsClipChildren | WsClipSiblings : WsPopup;
        return CreateWindowExW(0, ClassName, "", style, 0, 0, Math.Max(1, width), Math.Max(1, height),
            parent, IntPtr.Zero, GetModuleHandleW(null), IntPtr.Zero);
    }

    public static void DestroyWindow(IntPtr hwnd)
    {
        if (hwnd != IntPtr.Zero)
            _ = DestroyWindowNative(hwnd);
    }

    /// <summary>Makes a context on <paramref name="hwnd"/>, current on this thread; null with the reason.</summary>
    public static GlContext? TryCreate(IntPtr hwnd, out string? error)
    {
        error = null;
        var dc = GetDC(hwnd);
        if (dc == IntPtr.Zero)
        {
            error = "no device context";
            return null;
        }

        var format = new PixelFormat
        {
            Size = (ushort)Marshal.SizeOf<PixelFormat>(),
            Version = 1,
            Flags = PfdDrawToWindow | PfdSupportOpenGl | PfdDoubleBuffer,
            PixelType = 0,
            ColorBits = 32,
            AlphaBits = 8,
            DepthBits = 24,
            StencilBits = 8,
        };

        var chosen = ChoosePixelFormat(dc, ref format);
        if (chosen == 0 || !SetPixelFormat(dc, chosen, ref format))
        {
            _ = ReleaseDC(hwnd, dc);
            error = $"no pixel format ({Marshal.GetLastWin32Error()})";
            return null;
        }

        var rc = wglCreateContext(dc);
        if (rc == IntPtr.Zero || !wglMakeCurrent(dc, rc))
        {
            if (rc != IntPtr.Zero)
                _ = wglDeleteContext(rc);
            _ = ReleaseDC(hwnd, dc);
            error = $"no OpenGL context ({Marshal.GetLastWin32Error()})";
            return null;
        }

        // Paced by the compositor (DwmFlush) rather than by blocking in SwapBuffers.
        var swapInterval = wglGetProcAddress("wglSwapIntervalEXT");
        if (swapInterval != IntPtr.Zero)
            _ = Marshal.GetDelegateForFunctionPointer<SwapIntervalExt>(swapInterval)(0);

        return new GlContext(hwnd, dc, rc);
    }

    public void SwapBuffers() => _ = SwapBuffersNative(_dc);

    /// <summary>Waits for everything drawn so far to be done, for timing.</summary>
    public static void Finish() => glFinish();

    /// <summary>
    /// The back buffer's pixels as RGBA, bottom row first (OpenGL's order): what the
    /// last frame drew, before it is swapped. For checking a picture off-screen.
    /// </summary>
    public static byte[] ReadPixels(int width, int height)
    {
        var pixels = new byte[width * height * 4];
        glReadBuffer(GlBack);
        glReadPixels(0, 0, width, height, GlRgba, GlUnsignedByte, pixels);
        return pixels;
    }

    /// <summary>Waits for the compositor's next frame: about one display refresh.</summary>
    public static void WaitForCompositor() => _ = DwmFlush();

    public void Dispose()
    {
        if (_rc == IntPtr.Zero)
            return;
        _ = wglMakeCurrent(IntPtr.Zero, IntPtr.Zero);
        _ = wglDeleteContext(_rc);
        _ = ReleaseDC(_hwnd, _dc);
        _rc = IntPtr.Zero;
        _dc = IntPtr.Zero;
    }

    private static string GlString(uint name) => Marshal.PtrToStringAnsi(glGetString(name)) ?? "";

    private static void RegisterClass()
    {
        lock (ClassLock)
        {
            if (_classRegistered)
                return;

            var user32 = NativeLibrary.Load("user32.dll");
            var windowClass = new WindowClass
            {
                Size = (uint)Marshal.SizeOf<WindowClass>(),
                Style = CsOwnDc | CsDoubleClicks | CsHorizontalRedraw | CsVerticalRedraw,
                WindowProcedure = NativeLibrary.GetExport(user32, "DefWindowProcW"),
                Instance = GetModuleHandleW(null),
                Cursor = LoadCursorW(IntPtr.Zero, IdcArrow),
                Background = GetStockObject(BlackBrush),
                ClassName = ClassName,
            };
            if (RegisterClassExW(ref windowClass) == 0 && Marshal.GetLastWin32Error() != ErrorClassAlreadyExists)
                throw new InvalidOperationException($"Couldn't register the OpenGL window class ({Marshal.GetLastWin32Error()}).");
            _classRegistered = true;
        }
    }

    // ------------------------------------------------------------------ Win32

    private const uint WsChild = 0x40000000;
    private const uint WsVisible = 0x10000000;
    private const uint WsPopup = 0x80000000;
    private const uint WsClipChildren = 0x02000000;
    private const uint WsClipSiblings = 0x04000000;
    private const uint CsVerticalRedraw = 0x0001;
    private const uint CsHorizontalRedraw = 0x0002;
    private const uint CsDoubleClicks = 0x0008;
    private const uint CsOwnDc = 0x0020;
    private const int IdcArrow = 32512;
    private const int BlackBrush = 4;
    private const int ErrorClassAlreadyExists = 1410;
    private const uint PfdDoubleBuffer = 0x00000001;
    private const uint PfdDrawToWindow = 0x00000004;
    private const uint PfdSupportOpenGl = 0x00000020;
    private const uint GlRenderer = 0x1F01;
    private const uint GlVersion = 0x1F02;

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int SwapIntervalExt(int interval);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WindowClass
    {
        public uint Size;
        public uint Style;
        public IntPtr WindowProcedure;
        public int ClassExtra;
        public int WindowExtra;
        public IntPtr Instance;
        public IntPtr Icon;
        public IntPtr Cursor;
        public IntPtr Background;
        public string? MenuName;
        public string ClassName;
        public IntPtr SmallIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PixelFormat
    {
        public ushort Size;
        public ushort Version;
        public uint Flags;
        public byte PixelType;
        public byte ColorBits;
        public byte RedBits, RedShift, GreenBits, GreenShift, BlueBits, BlueShift;
        public byte AlphaBits, AlphaShift;
        public byte AccumBits, AccumRedBits, AccumGreenBits, AccumBlueBits, AccumAlphaBits;
        public byte DepthBits;
        public byte StencilBits;
        public byte AuxBuffers;
        public byte LayerType;
        public byte Reserved;
        public uint LayerMask, VisibleMask, DamageMask;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClassExW(ref WindowClass windowClass);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowExW(uint exStyle, string className, string windowName, uint style,
        int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

    [DllImport("user32.dll", EntryPoint = "DestroyWindow", SetLastError = true)]
    private static extern bool DestroyWindowNative(IntPtr hwnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr LoadCursorW(IntPtr instance, int name);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandleW(string? name);

    [DllImport("gdi32.dll")]
    private static extern IntPtr GetStockObject(int index);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern int ChoosePixelFormat(IntPtr dc, ref PixelFormat format);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern bool SetPixelFormat(IntPtr dc, int format, ref PixelFormat descriptor);

    [DllImport("gdi32.dll", EntryPoint = "SwapBuffers")]
    private static extern bool SwapBuffersNative(IntPtr dc);

    [DllImport("opengl32.dll", SetLastError = true)]
    private static extern IntPtr wglCreateContext(IntPtr dc);

    [DllImport("opengl32.dll", SetLastError = true)]
    private static extern bool wglMakeCurrent(IntPtr dc, IntPtr rc);

    [DllImport("opengl32.dll")]
    private static extern bool wglDeleteContext(IntPtr rc);

    [DllImport("opengl32.dll", CharSet = CharSet.Ansi, BestFitMapping = false)]
    private static extern IntPtr wglGetProcAddress(string name);

    [DllImport("opengl32.dll")]
    private static extern IntPtr glGetString(uint name);

    [DllImport("opengl32.dll")]
    private static extern void glFinish();

    private const uint GlBack = 0x0405;
    private const uint GlRgba = 0x1908;
    private const uint GlUnsignedByte = 0x1401;

    [DllImport("opengl32.dll")]
    private static extern void glReadBuffer(uint mode);

    [DllImport("opengl32.dll")]
    private static extern void glReadPixels(int x, int y, int width, int height, uint format, uint type, byte[] pixels);

    [DllImport("dwmapi.dll")]
    private static extern int DwmFlush();
}
