using System.Windows;
using System.Windows.Media;

namespace AudioFool.Theming;

/// <summary>
/// The seekbar's waveform (the user's choice, 2026-10-06, mockup C): the
/// track's loudness as a filled shape mirrored about the middle, the played
/// part in <see cref="PlayedBrush"/> and the rest in <see cref="RestBrush"/>.
/// It takes the place of the 6 px groove while the track has levels; see
/// <see cref="SeekWaveform"/>. The split falls where the handle's centre would
/// be; the handle itself is hidden (the user's call, 2026-10-06), so the
/// teal and grey meeting is the only marker.
/// </summary>
public sealed class WaveformView : FrameworkElement
{
    public static readonly DependencyProperty LevelsProperty = DependencyProperty.Register(
        nameof(Levels), typeof(IReadOnlyList<float>), typeof(WaveformView),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, OnShapeChanged));

    public static readonly DependencyProperty MinimumProperty = DependencyProperty.Register(
        nameof(Minimum), typeof(double), typeof(WaveformView),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty MaximumProperty = DependencyProperty.Register(
        nameof(Maximum), typeof(double), typeof(WaveformView),
        new FrameworkPropertyMetadata(1.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(double), typeof(WaveformView),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender, OnValueChanged));

    /// <summary>
    /// True while the track plays and <see cref="Value"/>, in seconds, is
    /// advancing at one a second. The split then glides between the position
    /// timer's 250 ms updates instead of stepping on each one.
    /// </summary>
    public static readonly DependencyProperty IsPlayingProperty = DependencyProperty.Register(
        nameof(IsPlaying), typeof(bool), typeof(WaveformView),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender, OnGlideChanged));

    public static readonly DependencyProperty PlayedBrushProperty = DependencyProperty.Register(
        nameof(PlayedBrush), typeof(Brush), typeof(WaveformView),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty RestBrushProperty = DependencyProperty.Register(
        nameof(RestBrush), typeof(Brush), typeof(WaveformView),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>The height silence still gets, so the bar never breaks up.</summary>
    public static readonly DependencyProperty FloorProperty = DependencyProperty.Register(
        nameof(Floor), typeof(double), typeof(WaveformView),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender, OnShapeChanged));

    /// <summary>
    /// How far past the last real position the glide may run: a little over
    /// one timer tick, so a late tick stalls it briefly rather than letting it
    /// run on, and a held drag creeps no further than this.
    /// </summary>
    private const double MaxLeadSeconds = 0.4;

    private Geometry? _shape;
    private Size _shapeSize;

    private readonly System.Diagnostics.Stopwatch _sinceValue = System.Diagnostics.Stopwatch.StartNew();
    private bool _gliding;
    private double _drawnSplit = double.NaN;

    public WaveformView()
    {
        Loaded += (_, _) => UpdateGlide();
        Unloaded += (_, _) => UpdateGlide();
        IsVisibleChanged += (_, _) => UpdateGlide();
    }

    public bool IsPlaying { get => (bool)GetValue(IsPlayingProperty); set => SetValue(IsPlayingProperty, value); }

    public IReadOnlyList<float>? Levels { get => (IReadOnlyList<float>?)GetValue(LevelsProperty); set => SetValue(LevelsProperty, value); }
    public double Minimum { get => (double)GetValue(MinimumProperty); set => SetValue(MinimumProperty, value); }
    public double Maximum { get => (double)GetValue(MaximumProperty); set => SetValue(MaximumProperty, value); }
    public double Value { get => (double)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public Brush? PlayedBrush { get => (Brush?)GetValue(PlayedBrushProperty); set => SetValue(PlayedBrushProperty, value); }
    public Brush? RestBrush { get => (Brush?)GetValue(RestBrushProperty); set => SetValue(RestBrushProperty, value); }
    public double Floor { get => (double)GetValue(FloorProperty); set => SetValue(FloorProperty, value); }

    private static void OnShapeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var view = (WaveformView)d;
        view._shape = null;
        if (e.Property == LevelsProperty)
            view.UpdateGlide();
    }

    private static void OnValueChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((WaveformView)d)._sinceValue.Restart();

    private static void OnGlideChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var view = (WaveformView)d;
        view._sinceValue.Restart();
        view.UpdateGlide();
    }

    /// <summary>Follows the screen's refresh only while there's something gliding to draw.</summary>
    private void UpdateGlide()
    {
        var glide = IsPlaying && IsLoaded && IsVisible && Levels is { Count: > 0 };
        if (glide == _gliding)
            return;

        _gliding = glide;
        if (glide)
            CompositionTarget.Rendering += OnFrame;
        else
            CompositionTarget.Rendering -= OnFrame;
    }

    /// <summary>
    /// Redraws on every frame WPF composes while the split is moving, so it
    /// moves at the display's refresh rate (the user has a 144 Hz monitor). Only
    /// a split that hasn't moved at all (held at the end) is skipped.
    /// </summary>
    private void OnFrame(object? sender, EventArgs e)
    {
        if (SplitAt(RenderSize.Width) != _drawnSplit)
            InvalidateVisual();
    }

    private double SplitAt(double width)
    {
        var value = Value;
        if (IsPlaying)
            value += Math.Min(_sinceValue.Elapsed.TotalSeconds, MaxLeadSeconds);

        var span = Maximum - Minimum;
        var fraction = span > 0 ? Math.Clamp((value - Minimum) / span, 0, 1) : 0;
        return fraction * width;
    }

    protected override void OnRender(DrawingContext dc)
    {
        var size = RenderSize;
        if (Levels is not { Count: > 0 } levels || size.Width <= 0 || size.Height <= 0)
            return;

        if (_shape is null || _shapeSize != size)
        {
            _shape = Build(levels, size, Floor);
            _shapeSize = size;
        }

        var split = SplitAt(size.Width);
        _drawnSplit = split;

        dc.PushClip(new RectangleGeometry(new Rect(0, 0, split, size.Height)));
        dc.DrawGeometry(PlayedBrush, null, _shape);
        dc.Pop();

        dc.PushClip(new RectangleGeometry(new Rect(split, 0, size.Width - split, size.Height)));
        dc.DrawGeometry(RestBrush, null, _shape);
        dc.Pop();
    }

    /// <summary>
    /// The outline: along the top edge from left to right, then back along the
    /// bottom, one point per level at the middle of its column.
    /// </summary>
    private static StreamGeometry Build(IReadOnlyList<float> levels, Size size, double floor)
    {
        var middle = size.Height / 2;
        var reach = Math.Max(0, middle - (floor / 2));
        var column = size.Width / levels.Count;

        double HalfHeight(int i) => (floor / 2) + (Math.Clamp(levels[i], 0, 1) * reach);

        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            ctx.BeginFigure(new Point(0, middle - HalfHeight(0)), isFilled: true, isClosed: true);
            for (var i = 0; i < levels.Count; i++)
                ctx.LineTo(new Point((i + 0.5) * column, middle - HalfHeight(i)), isStroked: false, isSmoothJoin: false);
            ctx.LineTo(new Point(size.Width, middle - HalfHeight(levels.Count - 1)), false, false);

            ctx.LineTo(new Point(size.Width, middle + HalfHeight(levels.Count - 1)), false, false);
            for (var i = levels.Count - 1; i >= 0; i--)
                ctx.LineTo(new Point((i + 0.5) * column, middle + HalfHeight(i)), false, false);
            ctx.LineTo(new Point(0, middle + HalfHeight(0)), false, false);
        }

        geometry.Freeze();
        return geometry;
    }
}

/// <summary>
/// Gives a <c>theme.slider</c> a waveform: set <c>Levels</c> and the template
/// swaps its groove for a <see cref="WaveformView"/>. Null keeps the plain
/// groove, which is what a track shows until its levels are read, or when they
/// can't be (drive out, file unreadable).
/// </summary>
public static class SeekWaveform
{
    public static readonly DependencyProperty LevelsProperty = DependencyProperty.RegisterAttached(
        "Levels", typeof(IReadOnlyList<float>), typeof(SeekWaveform), new PropertyMetadata(null, OnLevelsChanged));

    /// <summary>True while <c>Levels</c> has something to draw; the template's trigger reads this.</summary>
    public static readonly DependencyProperty IsShownProperty = DependencyProperty.RegisterAttached(
        "IsShown", typeof(bool), typeof(SeekWaveform), new PropertyMetadata(false));

    /// <summary>True while playing, so the waveform's split glides between position updates.</summary>
    public static readonly DependencyProperty IsPlayingProperty = DependencyProperty.RegisterAttached(
        "IsPlaying", typeof(bool), typeof(SeekWaveform), new PropertyMetadata(false));

    public static bool GetIsPlaying(DependencyObject element) => (bool)element.GetValue(IsPlayingProperty);

    public static void SetIsPlaying(DependencyObject element, bool value) => element.SetValue(IsPlayingProperty, value);

    public static IReadOnlyList<float>? GetLevels(DependencyObject element) => (IReadOnlyList<float>?)element.GetValue(LevelsProperty);

    public static void SetLevels(DependencyObject element, IReadOnlyList<float>? value) => element.SetValue(LevelsProperty, value);

    public static bool GetIsShown(DependencyObject element) => (bool)element.GetValue(IsShownProperty);

    private static void OnLevelsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        d.SetValue(IsShownProperty, e.NewValue is IReadOnlyList<float> { Count: > 0 });
}
