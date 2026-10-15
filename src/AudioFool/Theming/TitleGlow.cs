using System.Windows;
using System.Windows.Media;

namespace AudioFool.Theming;

/// <summary>
/// PS2 Dark's menu glow (PS2 Dark spec 7.1): a faint deep-blue haze at the top
/// centre of the title bar, like the PS2's system menu. A radial gradient centred
/// horizontally, its centre <see cref="CenterY"/> from the top edge (negative is
/// above it), <see cref="RadiusX"/> by <see cref="RadiusY"/>, painted over the
/// element's own box: <see cref="Color"/> at the centre, the same colour at 40% of
/// its alpha at 0.45, clear by 0.75. The colour is <c>color.window.titleGlow</c>,
/// transparent in Light, where nothing is drawn.
/// </summary>
public sealed class TitleGlow : FrameworkElement
{
    // The gradient's shape (spec 7.1); its colour and size are tokens.
    private const double MidStop = 0.45;
    private const double MidAlpha = 0.4;
    private const double EndStop = 0.75;

    public static readonly DependencyProperty ColorProperty = DependencyProperty.Register(
        nameof(Color), typeof(Color), typeof(TitleGlow),
        new FrameworkPropertyMetadata(Colors.Transparent, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty RadiusXProperty = DependencyProperty.Register(
        nameof(RadiusX), typeof(double), typeof(TitleGlow),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty RadiusYProperty = DependencyProperty.Register(
        nameof(RadiusY), typeof(double), typeof(TitleGlow),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty CenterYProperty = DependencyProperty.Register(
        nameof(CenterY), typeof(double), typeof(TitleGlow),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public Color Color
    {
        get => (Color)GetValue(ColorProperty);
        set => SetValue(ColorProperty, value);
    }

    public double RadiusX
    {
        get => (double)GetValue(RadiusXProperty);
        set => SetValue(RadiusXProperty, value);
    }

    public double RadiusY
    {
        get => (double)GetValue(RadiusYProperty);
        set => SetValue(RadiusYProperty, value);
    }

    /// <summary>The gradient's centre, in pixels down from the top edge; negative is above it.</summary>
    public double CenterY
    {
        get => (double)GetValue(CenterYProperty);
        set => SetValue(CenterYProperty, value);
    }

    protected override void OnRender(DrawingContext dc)
    {
        var c = Color;
        if (c.A == 0 || RadiusX <= 0 || RadiusY <= 0 || ActualWidth <= 0 || ActualHeight <= 0)
            return;

        // The fade ends in the same colour with no alpha, as CSS's "transparent"
        // does. WPF blends the colour channels too, so a clear black or white end
        // stop would tint the haze on its way out.
        var centre = new Point(ActualWidth / 2, CenterY);
        var brush = new RadialGradientBrush
        {
            MappingMode = BrushMappingMode.Absolute,
            Center = centre,
            GradientOrigin = centre,
            RadiusX = RadiusX,
            RadiusY = RadiusY,
            GradientStops =
            {
                new GradientStop(c, 0),
                new GradientStop(Color.FromArgb((byte)Math.Round(c.A * MidAlpha), c.R, c.G, c.B), MidStop),
                new GradientStop(Color.FromArgb(0, c.R, c.G, c.B), EndStop),
            },
        };
        brush.Freeze();
        dc.DrawRectangle(brush, null, new Rect(RenderSize));
    }
}
