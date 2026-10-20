using System.ComponentModel;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls.Primitives;
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
using AudioFool.Core.Visualizer;
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

    // The projectM trial: projectM in the picture while it runs, the bars otherwise.
    private readonly PresetLibrary _presets = new(PresetLibrary.DefaultFolder);
    private readonly double _levelMargin;
    private ProjectMHost? _projectM;
    private Popup? _levelPopup;
    private System.Windows.Controls.TextBlock? _popupText;
    private System.Drawing.Point _lastCursor = new(int.MinValue, int.MinValue);

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
        _levelMargin = n["visualizer.levelMargin"];

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
            if (!_fullscreen)
                return;
            Cursor = Cursors.None;
            if (_projectM is not null)
                _projectM.HideCursor = true;
        };

        SourceInitialized += (_, _) => Place();
        Loaded += (_, _) =>
        {
            _engine.TapEnabled = true;
            UpdateRendering();
            StartProjectM();
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

    /// <summary>projectM while it runs in the picture (the trial), for ThemeLab.</summary>
    public ProjectMHost? ProjectM => _projectM;

    /// <summary>What B does: projectM or the bars. For ThemeLab.</summary>
    public void SwitchPicture() => ToggleProjectM();

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
        StopProjectM();
        if (_levelPopup is not null)
            _levelPopup.IsOpen = false;
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
        if (_projectM is not null)
            _projectM.Paused = !wanted;
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
        KeepAwake(_fullscreen && playing);

        // projectM reads its own sound, on its own thread.
        if (_projectM is not null)
            return;

        _feed.Next(seconds, playing);
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
        if (_projectM is not null)
            _projectM.HideCursor = false;
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
        CursorMoved();
    }

    private void CursorMoved()
    {
        if (!_fullscreen)
            return;
        Cursor = null;
        if (_projectM is not null)
            _projectM.HideCursor = false;
        _cursorTimer.Stop();
        _cursorTimer.Start();
    }

    /// <summary>
    /// A move over projectM's window, which WPF doesn't see. Windows sends a move
    /// when nothing moved too (a window appearing under the cursor), so the cursor's
    /// position decides.
    /// </summary>
    private void ProjectMMouseMoved()
    {
        if (!GetCursorPos(out var at) || (Math.Abs(at.X - _lastCursor.X) < 2 && Math.Abs(at.Y - _lastCursor.Y) < 2))
            return;
        _lastCursor = at;
        CursorMoved();
    }

    // ------------------------------------------------------------ projectM (the trial)

    /// <summary>Whether projectM can run here: its DLL beside the exe and the presets downloaded.</summary>
    private bool ProjectMAvailable =>
        System.IO.File.Exists(System.IO.Path.Combine(AppContext.BaseDirectory, "libprojectM-4-4.dll")) && System.IO.Directory.Exists(_presets.PresetsFolder);

    /// <summary>projectM in the picture, over the bars; if it can't start, the bars stay.</summary>
    private void StartProjectM()
    {
        if (_projectM is not null || !ProjectMAvailable)
            return;

        var host = new ProjectMHost(_engine) { Intensity = _feed.Intensity };
        host.Failed += _ => StopProjectM();
        host.DoubleClicked += ToggleFullscreen;
        host.MouseMoved += ProjectMMouseMoved;
        Picture.Children.Add(host);
        Bars.Visibility = Visibility.Hidden;
        _projectM = host;
        UpdateRendering();
    }

    private void StopProjectM()
    {
        if (_projectM is not { } host)
            return;

        _projectM = null;
        Picture.Children.Remove(host);
        host.Dispose();
        Bars.Visibility = Visibility.Visible;
        _lastFrame = TimeSpan.MinValue;
    }

    /// <summary>B: projectM or the bars, to compare.</summary>
    private void ToggleProjectM()
    {
        if (_projectM is not null)
            StopProjectM();
        else
            StartProjectM();
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
        else if (key == Key.B && modifiers == ModifierKeys.None)
        {
            if (!e.IsRepeat)
                ToggleProjectM();
            e.Handled = true;
        }
        else if (_projectM is { } projectM && modifiers == ModifierKeys.None && key is Key.Right or Key.Left or Key.L or Key.Delete)
        {
            // The trial's preset keys: next, previous, stay on this one, never again.
            if (!e.IsRepeat)
            {
                if (key == Key.Right)
                    projectM.Next();
                else if (key == Key.Left)
                    projectM.Previous();
                else if (key == Key.L)
                    projectM.ToggleLock(locked => ShowLevel(locked ? "Locked" : "Unlocked"));
                else
                {
                    projectM.SkipCurrent(_presets);
                    ShowLevel("Skipped");
                }
            }
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
        if (_projectM is not null)
            _projectM.Intensity = _feed.Intensity;

        ShowLevel(_feed.Intensity.ToString(CultureInfo.CurrentCulture));
    }

    /// <summary>
    /// Shows <paramref name="text"/> in the bottom-right corner for a moment. Over
    /// projectM's window WPF can't draw, so there it goes in a popup, its own little
    /// window, closed once the text has faded.
    /// </summary>
    private void ShowLevel(string text)
    {
        if (_projectM is null)
        {
            LevelText.Text = text;
            Fade(LevelText);
            return;
        }

        if (_levelPopup is null)
        {
            _popupText = new System.Windows.Controls.TextBlock { IsHitTestVisible = false };
            _popupText.SetResourceReference(StyleProperty, "type.status");
            _popupText.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, "color.visualizer.level");
            _levelPopup = new Popup
            {
                Child = _popupText,
                AllowsTransparency = true,
                PlacementTarget = Picture,
                Placement = PlacementMode.Relative,
                IsHitTestVisible = false,
                Focusable = false,
            };
        }

        _popupText!.Text = text;
        _popupText.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        _levelPopup.HorizontalOffset = Picture.ActualWidth - _popupText.DesiredSize.Width - _levelMargin;
        _levelPopup.VerticalOffset = Picture.ActualHeight - _popupText.DesiredSize.Height - _levelMargin;
        _levelPopup.IsOpen = true;

        var fade = Fade(_popupText);
        var popup = _levelPopup;
        fade.Completed += (_, _) =>
        {
            if (ReferenceEquals(_popupText?.Text, text))
                popup.IsOpen = false;
        };
    }

    /// <summary>Full opacity now, faded out after the show time.</summary>
    private DoubleAnimation Fade(UIElement element)
    {
        var fade = new DoubleAnimation(1, 0, _levelFade) { BeginTime = _levelShow };
        element.BeginAnimation(OpacityProperty, null);
        element.Opacity = 1;
        element.BeginAnimation(OpacityProperty, fade);
        return fade;
    }

    // ------------------------------------------------------------ Win32

    private const uint EsContinuous = 0x80000000;
    private const uint EsDisplayRequired = 0x00000002;
    private const int DwmwaBorderColour = 34;
    private const uint DwmColourNone = 0xFFFFFFFE;
    private const uint DwmColourDefault = 0xFFFFFFFF;

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out System.Drawing.Point point);

    [DllImport("kernel32.dll")]
    private static extern uint SetThreadExecutionState(uint flags);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref uint value, int size);
}
