using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using AudioFool.Core.Theme;
using AudioFool.Services;
using AudioFool.Theming;

namespace AudioFool;

/// <summary>
/// The About intro (session 64): the PlayStation's start-up, played over the
/// main window from the logo menu's About. Each frame shows the screens as
/// <see cref="IntroTimeline"/> has them at the time since the sound started; it
/// closes itself when the sound is over, or at a click or any key. The caller
/// pauses the music and frees the device for it.
/// </summary>
public partial class IntroWindow : Window
{
    /// <summary>How long the sound may take to open before the pictures start without it.</summary>
    private static readonly TimeSpan SoundWait = TimeSpan.FromSeconds(1);

    private readonly IntroTimeline _timeline = IntroTimeline.FromTokens(TokenResources.Current!.Numbers);
    private readonly MediaPlayer _player = new();
    private readonly Stopwatch _clock = new();
    private readonly DispatcherTimer _soundWait;
    private bool _started;

    public IntroWindow(Window owner, double volume)
    {
        InitializeComponent();
        OwnerActivation.Attach(this);
        SonyVersion.Text = PlayStationVersion.Text = VersionLine();

        Owner = owner;
        CoverOwner(owner);
        SourceInitialized += (_, _) => RoundLike(owner);

        _player.Volume = volume;
        _player.MediaOpened += (_, _) => Start(withSound: true);
        _player.MediaFailed += (_, _) => Start(withSound: false);
        _soundWait = new DispatcherTimer { Interval = SoundWait };
        _soundWait.Tick += (_, _) => Start(withSound: false);

        Loaded += (_, _) =>
        {
            _soundWait.Start();
            if (SoundFile() is { } file)
                _player.Open(new Uri(file));
            else
                Start(withSound: false);
        };

        PreviewMouseDown += (_, e) => { e.Handled = true; Close(); };
        PreviewKeyDown += (_, e) => { e.Handled = true; Close(); };
    }

    /// <summary>The screens as they are <paramref name="at"/> into the intro. ThemeLab renders through it too.</summary>
    internal void ShowAt(TimeSpan at)
    {
        var (sony, playStation) = _timeline.Opacities(at);
        SonyScreen.Opacity = sony;
        PlayStationScreen.Opacity = playStation;
    }

    /// <summary>"AudioFool v0.6.1": the exe's version, from the csproj's &lt;Version&gt;, so a release bump shows here.</summary>
    internal static string VersionLine() =>
        $"AudioFool v{typeof(IntroWindow).Assembly.GetName().Version?.ToString(3) ?? "?"}";

    private void Start(bool withSound)
    {
        _soundWait.Stop();
        if (_started)
            return;
        _started = true;

        if (withSound)
            _player.Play();

        _clock.Start();
        CompositionTarget.Rendering += OnRendering;
    }

    private void OnRendering(object? sender, EventArgs e)
    {
        var at = _clock.Elapsed;
        ShowAt(at);
        if (_timeline.IsOver(at))
            Close();
    }

    protected override void OnClosed(EventArgs e)
    {
        _soundWait.Stop();
        CompositionTarget.Rendering -= OnRendering;
        _player.Stop();
        _player.Close();
        base.OnClosed(e);
    }

    /// <summary>
    /// Exactly over what shows of the main window, maximised or not, in WPF's
    /// units on its monitor.
    /// </summary>
    private void CoverOwner(Window owner)
    {
        if (WindowPlacement.VisibleRectOf(owner) is not { } rect)
            return;

        var dpi = VisualTreeHelper.GetDpi(owner);
        Left = rect.Left / dpi.DpiScaleX;
        Top = rect.Top / dpi.DpiScaleY;
        Width = (rect.Right - rect.Left) / dpi.DpiScaleX;
        Height = (rect.Bottom - rect.Top) / dpi.DpiScaleY;
    }

    /// <summary>Round corners like the main window's, square when it is maximised (Windows squares those).</summary>
    private void RoundLike(Window owner)
    {
        var preference = owner.WindowState == WindowState.Maximized ? DWMWCP_DONOTROUND : DWMWCP_ROUND;
        DwmSetWindowAttribute(new WindowInteropHelper(this).Handle, DWMWA_WINDOW_CORNER_PREFERENCE, ref preference, sizeof(int));
    }

    /// <summary>
    /// The sound, embedded in the exe, as a file for MediaPlayer (which can't play
    /// from memory): written to the temp folder the first time, null if it can't be.
    /// </summary>
    private static string? SoundFile()
    {
        try
        {
            using var sound = typeof(IntroWindow).Assembly.GetManifestResourceStream("AudioFool.intro.mp3");
            if (sound is null)
                return null;

            var path = Path.Combine(Path.GetTempPath(), "AudioFool-intro.mp3");
            if (!File.Exists(path) || new FileInfo(path).Length != sound.Length)
            {
                using var file = File.Create(path);
                sound.CopyTo(file);
            }

            return path;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWCP_DONOTROUND = 1;
    private const int DWMWCP_ROUND = 2;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
