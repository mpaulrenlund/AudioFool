using System.ComponentModel;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using Microsoft.Win32;
using Wpf.Ui.Controls;
using AudioFool.Core.Analysis;
using AudioFool.Core.Playback;
using AudioFool.Core.Settings;
using AudioFool.Services;
using AudioFool.Theming;
using AudioFool.ViewModels;

namespace AudioFool;

/// <summary>
/// The visualizer (session 58): for now a placeholder, a bar per band of what is
/// being heard, so the timing can be judged before projectM is tried here.
/// <para>
/// Its own window, not owned by the main one, so it stays up when AudioFool is
/// minimised; the main window closes it on the way out. Opening it turns the
/// engine's tap on and closing it turns it off. Each frame reads the window
/// centred on what is heard (<see cref="VisualizerFeed"/>) and scales the bars:
/// no layout, and the bar heights are taken from boxed values made once, so a
/// frame allocates nothing of ours.
/// </para>
/// <para>
/// Double-click the picture or Alt+Enter: fullscreen on the monitor it is on,
/// title bar hidden, the cursor hidden after a few seconds still and the display
/// kept awake while music plays; again, or Escape, to come back. Escape in a
/// window, or Ctrl+Shift+F, closes it. Up and Down change the intensity, shown
/// as a number that fades. The main window's playback shortcuts work here too.
/// </para>
/// </summary>
public partial class VisualizerWindow : FluentWindow
{
    /// <summary>Bar heights in steps of 1/1024, boxed once: setting a double property would box every frame.</summary>
    private static readonly object[] Heights = Enumerable.Range(0, 1025).Select(i => (object)(i / 1024.0)).ToArray();

    private readonly MainViewModel _viewModel;
    private readonly AudioEngine _engine;
    private readonly Window _main;
    private readonly Func<Key, ModifierKeys, bool, bool> _playbackShortcut;
    private readonly VisualizerFeed _feed;
    private readonly ScaleTransform[] _bars;
    private readonly int[] _shown;
    private readonly double _floorDb;
    private readonly double _topDb;
    private readonly TimeSpan _levelShow;
    private readonly TimeSpan _levelFade;
    private readonly DispatcherTimer _cursorTimer;

    private bool _rendering;
    private TimeSpan _lastFrame = TimeSpan.MinValue;
    private bool _locked;
    private bool _fullscreen;
    private WindowBounds? _restoreTo;
    private PixelRect _covering;
    private bool _keepingAwake;
    private Point _lastMouse = new(double.NaN, double.NaN);

    public VisualizerWindow(MainViewModel viewModel, AudioEngine engine, Window main,
        Func<Key, ModifierKeys, bool, bool> playbackShortcut)
    {
        _viewModel = viewModel;
        _engine = engine;
        _main = main;
        _playbackShortcut = playbackShortcut;

        InitializeComponent();

        var n = TokenResources.Current!.Numbers;
        var bands = (int)n["visualizer.bands"];
        _floorDb = n["visualizer.barFloorDb"];
        _topDb = n["visualizer.barTopDb"];
        _levelShow = TimeSpan.FromMilliseconds(n["visualizer.levelShowMs"]);
        _levelFade = TimeSpan.FromMilliseconds(n["visualizer.levelFadeMs"]);

        _feed = VisualizerFeed.For(engine, bands);
        _feed.Intensity = viewModel.VisualizerIntensity;

        // One bar per band, the full height of the picture, scaled down from the
        // bottom; the gap split either side so the outer bars sit a half gap in.
        var halfGap = n["visualizer.barGap"] / 2;
        Bars.Columns = bands;
        _bars = new ScaleTransform[bands];
        _shown = new int[bands];
        for (var b = 0; b < bands; b++)
        {
            var scale = new ScaleTransform(1, 0);
            var bar = new Rectangle
            {
                Margin = new Thickness(halfGap, 0, halfGap, 0),
                RenderTransformOrigin = new Point(0.5, 1),
                RenderTransform = scale,
                IsHitTestVisible = false,
            };
            bar.SetResourceReference(Shape.FillProperty, "color.visualizer.bar");
            Bars.Children.Add(bar);
            _bars[b] = scale;
        }

        _cursorTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(n["visualizer.cursorHideMs"]) };
        _cursorTimer.Tick += (_, _) =>
        {
            _cursorTimer.Stop();
            if (_fullscreen)
                Cursor = Cursors.None;
        };

        SourceInitialized += (_, _) => Place();
        Loaded += (_, _) =>
        {
            _engine.TapEnabled = true;
            UpdateRendering();
        };
        IsVisibleChanged += (_, _) => UpdateRendering();
        StateChanged += (_, _) => UpdateRendering();
        Picture.MouseLeftButtonDown += Picture_MouseLeftButtonDown;
        MouseMove += OnMouseMoved;
        SystemEvents.SessionSwitch += OnSessionSwitch;
        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
    }

    public bool IsFullscreen => _fullscreen;

    /// <summary>The analysis the bars are drawn from, for ThemeLab.</summary>
    public VisualizerFeed Feed => _feed;

    /// <summary>Ctrl+Shift+F, the shortcut that opens it, closes it from here too (as Ctrl+D does the art viewer).</summary>
    internal static bool ClosesOn(Key key, ModifierKeys modifiers, bool isRepeat) =>
        key is Key.F && modifiers == (ModifierKeys.Control | ModifierKeys.Shift) && !isRepeat;

    // ------------------------------------------------------------ placement

    /// <summary>Where it last was if that's still on a screen, else centred on the main window.</summary>
    private void Place()
    {
        if (_viewModel.SavedVisualizerBounds is { } bounds && WindowPlacement.Apply(this, bounds))
            return;
        CentreOnMain();
    }

    private void CentreOnMain()
    {
        var width = double.IsNaN(Width) ? ActualWidth : Width;
        var height = double.IsNaN(Height) ? ActualHeight : Height;
        Left = _main.Left + (_main.ActualWidth - width) / 2;
        Top = _main.Top + (_main.ActualHeight - height) / 2;
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);
        if (e.Cancel)
            return;

        // Fullscreen, it is remembered where it will come back to.
        if ((_fullscreen ? _restoreTo : WindowPlacement.Capture(this)) is { } bounds)
            _viewModel.SaveVisualizerBounds(bounds);
    }

    protected override void OnClosed(EventArgs e)
    {
        if (_rendering)
            CompositionTarget.Rendering -= OnRendering;
        _rendering = false;
        _engine.TapEnabled = false;
        KeepAwake(false);
        _cursorTimer.Stop();
        SystemEvents.SessionSwitch -= OnSessionSwitch;
        SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
        base.OnClosed(e);
    }

    // ------------------------------------------------------------ the frame

    /// <summary>Draws while it can be seen: not minimised, not hidden, the PC not locked.</summary>
    private void UpdateRendering()
    {
        var wanted = IsVisible && WindowState != WindowState.Minimized && !_locked;
        if (wanted == _rendering)
            return;

        _rendering = wanted;
        _lastFrame = TimeSpan.MinValue;
        if (wanted)
            CompositionTarget.Rendering += OnRendering;
        else
            CompositionTarget.Rendering -= OnRendering;
    }

    private void OnRendering(object? sender, EventArgs e)
    {
        // Rendering can come more than once a frame; the time says which frame.
        var time = ((RenderingEventArgs)e).RenderingTime;
        if (time == _lastFrame)
            return;
        var seconds = _lastFrame == TimeSpan.MinValue ? 1.0 / 60 : (time - _lastFrame).TotalSeconds;
        _lastFrame = time;

        var playing = _engine.State == PlaybackState.Playing;
        _feed.Next(seconds, playing);
        KeepAwake(_fullscreen && playing);

        if (_feed.Spectrum is not { } spectrum)
            return;

        var range = _topDb - _floorDb;
        for (var b = 0; b < _bars.Length && b < spectrum.Bands.Length; b++)
        {
            var step = (int)Math.Round(Math.Clamp((spectrum.Bands[b] - _floorDb) / range, 0, 1) * 1024);
            if (step == _shown[b])
                continue;
            _shown[b] = step;
            _bars[b].SetValue(ScaleTransform.ScaleYProperty, Heights[step]);
        }
    }

    // ------------------------------------------------------------ fullscreen

    public void ToggleFullscreen()
    {
        if (_fullscreen)
            LeaveFullscreen();
        else
            EnterFullscreen();
    }

    /// <summary>
    /// Covers the whole monitor it is on, taskbar included, which Windows takes as
    /// fullscreen. In pixels, through Win32, so a monitor of another DPI is covered
    /// exactly. What it comes back to is kept first, maximised or not.
    /// </summary>
    private void EnterFullscreen()
    {
        if (WindowPlacement.MonitorOf(this) is not { } monitor)
            return;

        _restoreTo = WindowPlacement.Capture(this);
        _fullscreen = true;
        _covering = monitor;
        ShowAsFullscreen(true);
        if (WindowState != WindowState.Normal)
            WindowState = WindowState.Normal;
        WindowPlacement.Cover(this, monitor);
        _cursorTimer.Start();
    }

    private void LeaveFullscreen()
    {
        _fullscreen = false;
        _cursorTimer.Stop();
        Cursor = null;
        KeepAwake(false);
        ShowAsFullscreen(false);
        if (_restoreTo is not { } bounds || !WindowPlacement.Apply(this, bounds))
            CentreOnMain();
    }

    /// <summary>
    /// The look of fullscreen, apart from the size: no title bar, square corners,
    /// no resizing, and no border from Windows. ThemeLab renders with it too.
    /// </summary>
    public void ShowAsFullscreen(bool on)
    {
        Bar.Visibility = on ? Visibility.Collapsed : Visibility.Visible;
        WindowCornerPreference = on ? WindowCornerPreference.DoNotRound : WindowCornerPreference.Round;
        ResizeMode = on ? ResizeMode.NoResize : ResizeMode.CanResize;

        // Windows 11 draws a 1 px border round a window; none round a fullscreen one.
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd != IntPtr.Zero)
        {
            var colour = on ? DwmColourNone : DwmColourDefault;
            _ = DwmSetWindowAttribute(hwnd, DwmwaBorderColour, ref colour, sizeof(uint));
        }
    }

    /// <summary>
    /// A monitor came or went. Fullscreen on one that has gone (the dock unplugged),
    /// it comes back to a window on a screen that's still there; a window left off
    /// every screen is brought back over the main window.
    /// </summary>
    private void OnDisplaySettingsChanged(object? sender, EventArgs e) => Dispatcher.BeginInvoke(() =>
    {
        if (_fullscreen)
        {
            if (!WindowPlacement.Monitors().Contains(_covering))
                LeaveFullscreen();
        }
        else if (WindowPlacement.Capture(this) is { } bounds && !bounds.IsReachableOn(WindowPlacement.WorkAreas()))
        {
            CentreOnMain();
        }
    });

    private void OnSessionSwitch(object? sender, SessionSwitchEventArgs e) => Dispatcher.BeginInvoke(() =>
    {
        if (e.Reason == SessionSwitchReason.SessionLock)
            _locked = true;
        else if (e.Reason == SessionSwitchReason.SessionUnlock)
            _locked = false;
        UpdateRendering();
    });

    /// <summary>Keeps the display from sleeping, from this thread, while asked to.</summary>
    private void KeepAwake(bool on)
    {
        if (on == _keepingAwake)
            return;
        _keepingAwake = on;
        _ = SetThreadExecutionState(on ? EsContinuous | EsDisplayRequired : EsContinuous);
    }

    // ------------------------------------------------------------ input

    private void Picture_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount != 2)
            return;
        ToggleFullscreen();
        e.Handled = true;
    }

    /// <summary>A move of the mouse shows the cursor again; still for a few seconds, fullscreen hides it.</summary>
    private void OnMouseMoved(object sender, MouseEventArgs e)
    {
        // WPF also raises a move when what is under a still cursor changes.
        var at = e.GetPosition(this);
        if (Math.Abs(at.X - _lastMouse.X) < 2 && Math.Abs(at.Y - _lastMouse.Y) < 2)
            return;
        _lastMouse = at;

        if (!_fullscreen)
            return;
        Cursor = null;
        _cursorTimer.Stop();
        _cursorTimer.Start();
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        var modifiers = Keyboard.Modifiers;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;

        if (key == Key.Enter && modifiers == ModifierKeys.Alt)
        {
            if (!e.IsRepeat)
                ToggleFullscreen();
            e.Handled = true;
        }
        else if (key == Key.Escape && modifiers == ModifierKeys.None)
        {
            if (_fullscreen)
                LeaveFullscreen();
            else
                Close();
            e.Handled = true;
        }
        else if (ClosesOn(key, modifiers, e.IsRepeat))
        {
            Close();
            e.Handled = true;
        }
        else if (key is Key.Up or Key.Down && modifiers == ModifierKeys.None)
        {
            StepIntensity(key == Key.Up ? 1 : -1);
            e.Handled = true;
        }
        else if (_playbackShortcut(key, modifiers, e.IsRepeat))
        {
            e.Handled = true;
        }

        base.OnPreviewKeyDown(e);
    }

    /// <summary>One step calmer or livelier, saved, and the new level shown for a moment.</summary>
    public void StepIntensity(int step)
    {
        _feed.Intensity += step;
        _viewModel.VisualizerIntensity = _feed.Intensity;

        LevelText.Text = _feed.Intensity.ToString(CultureInfo.CurrentCulture);
        LevelText.BeginAnimation(OpacityProperty, null);
        LevelText.Opacity = 1;
        LevelText.BeginAnimation(OpacityProperty, new DoubleAnimation(1, 0, _levelFade) { BeginTime = _levelShow });
    }

    // ------------------------------------------------------------ Win32

    private const uint EsContinuous = 0x80000000;
    private const uint EsDisplayRequired = 0x00000002;
    private const int DwmwaBorderColour = 34;
    private const uint DwmColourNone = 0xFFFFFFFE;
    private const uint DwmColourDefault = 0xFFFFFFFF;

    [DllImport("kernel32.dll")]
    private static extern uint SetThreadExecutionState(uint flags);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref uint value, int size);
}
