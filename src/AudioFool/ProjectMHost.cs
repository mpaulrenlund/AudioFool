using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using AudioFool.Core.Analysis;
using AudioFool.Core.Playback;
using AudioFool.Core.Visualizer;

namespace AudioFool;

/// <summary>
/// projectM in the visualizer's picture (session 58, the trial). projectM 4.1.6
/// can only draw into its OpenGL context's own window, so it gets a plain child
/// window here, and a thread of its own: its GPU work and its preset loading
/// (tens of milliseconds of shader compiling) never hold up AudioFool's windows.
/// <para>
/// The thread owns everything projectM: the context, the session, and its own
/// <see cref="VisualizerFeed"/> (the engine's tap may be read from any thread).
/// The window talks to it through commands. Each frame it hands projectM the
/// frames since the last one, ending at what is heard now (silence while paused),
/// draws, and waits for the compositor's next frame.
/// </para>
/// <para>
/// WPF can't draw over a child window, and doesn't see its mouse: double-clicks
/// and moves are passed on as events, and the cursor is hidden here.
/// </para>
/// </summary>
public sealed class ProjectMHost : HwndHost
{
    private readonly AudioEngine _engine;
    private readonly ConcurrentQueue<Action<ProjectMSession>> _commands = new();
    private Thread? _thread;
    private volatile bool _stop;
    private volatile bool _paused;
    private volatile int _width = 1;
    private volatile int _height = 1;
    private volatile int _intensity = LiveSpectrum.DefaultIntensity;
    private IntPtr _hwnd;

    public ProjectMHost(AudioEngine engine) => _engine = engine;

    /// <summary>projectM couldn't start or stopped working; the reason. On the UI thread.</summary>
    public event Action<string>? Failed;

    public event Action? DoubleClicked;

    public event Action? MouseMoved;

    /// <summary>Whether the cursor is hidden over the picture (fullscreen, still).</summary>
    public bool HideCursor { get; set; }

    /// <summary>Nothing drawn while true: minimised, locked.</summary>
    public bool Paused
    {
        get => _paused;
        set => _paused = value;
    }

    public int Intensity
    {
        get => _intensity;
        set
        {
            _intensity = value;
            _commands.Enqueue(session => session.SetIntensity(value));
        }
    }

    /// <summary>Frames drawn so far, and the GPU drawing them: for ThemeLab and the log.</summary>
    public long FramesDrawn => Interlocked.Read(ref _framesDrawn);

    public string? Renderer { get; private set; }

    /// <summary>The preset playing, as a path (it changes on projectM's thread).</summary>
    public string? CurrentPreset => Volatile.Read(ref _currentPreset);

    /// <summary>
    /// The next frame's pixels (RGBA, bottom row first) and its size, read back
    /// before it is shown: for ThemeLab, which can't capture a native window.
    /// </summary>
    public Task<(byte[] Pixels, int Width, int Height)> SnapshotAsync()
    {
        var done = new TaskCompletionSource<(byte[], int, int)>(TaskCreationOptions.RunContinuationsAsynchronously);
        Volatile.Write(ref _snapshot, done);
        return done.Task;
    }

    private long _framesDrawn;
    private string? _currentPreset;
    private TaskCompletionSource<(byte[], int, int)>? _snapshot;

    public void Next() => _commands.Enqueue(session => session.Next());

    public void Previous() => _commands.Enqueue(session => session.Previous());

    /// <summary>Locks or unlocks the preset playing; <paramref name="done"/> gets the new state, on the UI thread.</summary>
    public void ToggleLock(Action<bool> done) => _commands.Enqueue(session =>
    {
        session.Locked = !session.Locked;
        var locked = session.Locked;
        Dispatcher.BeginInvoke(() => done(locked));
    });

    /// <summary>Never this preset again: dropped from the playlist and added to the skip list.</summary>
    public void SkipCurrent(PresetLibrary library) => _commands.Enqueue(session =>
    {
        if (session.RemoveCurrent() is { } path)
        {
            library.Skip(path);
            PlaybackDiagnostics.Note($"visualizer: skipped for good: {library.Relative(path)}");
        }
    });

    protected override HandleRef BuildWindowCore(HandleRef hwndParent)
    {
        _hwnd = GlContext.CreateWindow(hwndParent.Handle, _width, _height);
        PlaybackDiagnostics.Note($"visualizer: projectM's window {_hwnd} in {hwndParent.Handle}");
        _stop = false;
        _thread = new Thread(Run) { IsBackground = true, Name = "projectM" };
        _thread.Start();
        return new HandleRef(this, _hwnd);
    }

    protected override void DestroyWindowCore(HandleRef hwnd)
    {
        _stop = true;
        _thread?.Join(TimeSpan.FromSeconds(5));
        _thread = null;
        GlContext.DestroyWindow(hwnd.Handle);
        _hwnd = IntPtr.Zero;
    }

    protected override IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        switch (msg)
        {
            case WmSize:
                _width = Math.Max(1, (int)(lParam.ToInt64() & 0xFFFF));
                _height = Math.Max(1, (int)((lParam.ToInt64() >> 16) & 0xFFFF));
                break;
            case WmEraseBackground:
                handled = true;
                return 1;
            case WmLeftButtonDoubleClick:
                DoubleClicked?.Invoke();
                handled = true;
                return IntPtr.Zero;
            case WmMouseMove:
                MouseMoved?.Invoke();
                break;
            case WmSetCursor when HideCursor:
                _ = SetCursor(IntPtr.Zero);
                handled = true;
                return 1;
        }

        return base.WndProc(hwnd, msg, wParam, lParam, ref handled);
    }

    /// <summary>The thread: context, projectM, then a frame at a time until stopped.</summary>
    private void Run()
    {
        GlContext? context = null;
        ProjectMSession? session = null;
        try
        {
            context = GlContext.TryCreate(_hwnd, out var error);
            if (context is null)
            {
                Fail($"no OpenGL: {error}");
                return;
            }

            var library = PresetLibrary.Default();
            var presets = library.Playable();
            session = ProjectMSession.TryCreate(presets, [library.TexturesFolder], _width, _height, out error);
            if (session is null)
            {
                Fail(error ?? "projectM didn't start");
                return;
            }

            session.SetIntensity(_intensity);
            Renderer = context.Renderer;
            Volatile.Write(ref _currentPreset, session.CurrentPreset);
            session.PresetStarted += path => Volatile.Write(ref _currentPreset, path);
            PlaybackDiagnostics.Note($"visualizer: projectM {ProjectMSession.LibraryVersion()} on {context.Renderer} ({context.Version}), " +
                                     $"{presets.Count} of {library.All().Count} presets");

            var feed = VisualizerFeed.For(_engine);
            var silence = new float[(feed.PcmNow + 1) * 2];
            var clock = Stopwatch.StartNew();
            var last = clock.Elapsed;
            int width = 0, height = 0;

            while (!_stop)
            {
                if (_paused)
                {
                    Thread.Sleep(50);
                    last = clock.Elapsed;
                    continue;
                }

                while (_commands.TryDequeue(out var command))
                    command(session);

                if (width != _width || height != _height)
                {
                    (width, height) = (_width, _height);
                    session.SetSize(width, height);
                }

                var now = clock.Elapsed;
                var seconds = Math.Clamp((now - last).TotalSeconds, 0, 0.25);
                last = now;

                // The frames since the last one, ending at what is heard now.
                feed.Next(seconds, _engine.State == PlaybackState.Playing);
                var frames = Math.Clamp((int)Math.Round(seconds * Math.Max(1, feed.PcmRate)), 0, feed.PcmNow);
                if (feed.Heard && feed.Pcm is { } pcm && feed.PcmChannels == 2)
                    session.AddPcm(pcm, feed.PcmNow - frames, frames);
                else
                    session.AddPcm(silence, 0, frames);

                session.Render();
                if (Interlocked.Exchange(ref _snapshot, null) is { } snapshot)
                    snapshot.TrySetResult((GlContext.ReadPixels(width, height), width, height));
                context.SwapBuffers();
                Interlocked.Increment(ref _framesDrawn);
                GlContext.WaitForCompositor();
            }
        }
        catch (Exception ex)
        {
            Fail($"{ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            session?.Dispose();
            context?.Dispose();
        }
    }

    private void Fail(string reason)
    {
        PlaybackDiagnostics.Note($"visualizer: projectM not used: {reason}");
        Dispatcher.BeginInvoke(() => Failed?.Invoke(reason));
    }

    private const int WmSize = 0x0005;
    private const int WmEraseBackground = 0x0014;
    private const int WmSetCursor = 0x0020;
    private const int WmMouseMove = 0x0200;
    private const int WmLeftButtonDoubleClick = 0x0203;

    [DllImport("user32.dll")]
    private static extern IntPtr SetCursor(IntPtr cursor);
}
