using System.ComponentModel;
using System.Windows;
using System.Windows.Media;
using System.Windows.Shell;
using AudioFool.ViewModels;
using Microsoft.Win32;

namespace AudioFool.Services;

/// <summary>
/// The Previous / Play-Pause / Next / Stop buttons Windows shows under the window's
/// thumbnail when you hover the taskbar icon.
/// <para>
/// The glyphs are drawn here rather than taken from WPF-UI's symbol font, because a
/// thumb button wants an <see cref="ImageSource"/>. WPF renders a
/// <see cref="DrawingImage"/> at the system small-icon size for the current DPI, so
/// they stay sharp at any scaling. Their colour follows the taskbar's own light or
/// dark mode, not the app theme: the flyout is drawn by the shell.
/// </para>
/// </summary>
public sealed class TaskbarControls : IDisposable
{
    // 16-unit canvas; WPF scales it to the real icon size.
    private const string PreviousGlyph = "M3,3 H5 V13 H3 Z M13,3 L6,8 L13,13 Z";
    private const string PlayGlyph = "M4,2.5 L13,8 L4,13.5 Z";
    private const string PauseGlyph = "M4,3 H7 V13 H4 Z M9,3 H12 V13 H9 Z";
    private const string NextGlyph = "M3,3 L10,8 L3,13 Z M11,3 H13 V13 H11 Z";
    private const string StopGlyph = "M4,4 H12 V12 H4 Z";

    private readonly MainViewModel _viewModel;
    private readonly ThumbButtonInfo _previous;
    private readonly ThumbButtonInfo _playPause;
    private readonly ThumbButtonInfo _next;
    private readonly ThumbButtonInfo _stop;

    private ImageSource? _playImage;
    private ImageSource? _pauseImage;

    public TaskbarControls(Window window, MainViewModel viewModel)
    {
        _viewModel = viewModel;

        _previous = new ThumbButtonInfo { Command = viewModel.PreviousCommand, Description = "Previous" };
        _playPause = new ThumbButtonInfo { Command = viewModel.TogglePlayCommand };
        _next = new ThumbButtonInfo { Command = viewModel.NextCommand, Description = "Next" };
        _stop = new ThumbButtonInfo { Command = viewModel.StopCommand, Description = "Stop" };

        window.TaskbarItemInfo = new TaskbarItemInfo
        {
            ThumbButtonInfos = { _previous, _playPause, _next, _stop },
        };

        ApplyGlyphs();

        _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
    }

    public void Dispose()
    {
        _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.IsPlaying))
            UpdatePlayPause();
    }

    /// <summary>Switching Windows between light and dark mode arrives as General.</summary>
    private void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category == UserPreferenceCategory.General)
            Application.Current?.Dispatcher.BeginInvoke(ApplyGlyphs);
    }

    private void ApplyGlyphs()
    {
        var brush = TaskbarIsLight() ? Brushes.Black : Brushes.White;

        _previous.ImageSource = Glyph(PreviousGlyph, brush);
        _next.ImageSource = Glyph(NextGlyph, brush);
        _stop.ImageSource = Glyph(StopGlyph, brush);
        _playImage = Glyph(PlayGlyph, brush);
        _pauseImage = Glyph(PauseGlyph, brush);

        UpdatePlayPause();
    }

    private void UpdatePlayPause()
    {
        _playPause.ImageSource = _viewModel.IsPlaying ? _pauseImage : _playImage;
        _playPause.Description = _viewModel.IsPlaying ? "Pause" : "Play";
    }

    private static DrawingImage Glyph(string data, Brush brush)
    {
        var group = new DrawingGroup();

        // An invisible full-canvas square keeps the padding: a DrawingImage is
        // sized to its content's bounds, which would stretch every glyph edge to edge.
        group.Children.Add(new GeometryDrawing(Brushes.Transparent, null, new RectangleGeometry(new Rect(0, 0, 16, 16))));
        group.Children.Add(new GeometryDrawing(brush, null, Geometry.Parse(data)));

        var image = new DrawingImage(group);
        image.Freeze();
        return image;
    }

    /// <summary>
    /// The taskbar has its own mode, separate from apps
    /// ("Choose your default Windows mode"). Missing value means dark, the Windows 11 default.
    /// </summary>
    private static bool TaskbarIsLight()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        return key?.GetValue("SystemUsesLightTheme") is int value && value != 0;
    }
}
