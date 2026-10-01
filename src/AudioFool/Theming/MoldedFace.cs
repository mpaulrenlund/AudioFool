using System.Windows;
using System.Windows.Media;

namespace AudioFool.Theming;

/// <summary>
/// The round face of a transport button (spec 6.7): a filled circle with a 1 px
/// border and the molded look of <c>shadow.controlButton</c> /
/// <c>shadow.playButton</c>. WPF has no inset shadow, so each inset layer is
/// drawn as the CSS one would fall: the circle minus itself moved by the
/// layer's offset. <c>inset 0 -3px</c> leaves a crescent along the bottom,
/// <c>inset 0 1px</c> a highlight along the top. The outer layer is the
/// token's DropShadowEffect, set as this element's Effect by the template.
/// </summary>
public sealed class MoldedFace : FrameworkElement
{
    public static readonly DependencyProperty FaceProperty = DependencyProperty.Register(
        nameof(Face), typeof(Brush), typeof(MoldedFace),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty StrokeProperty = DependencyProperty.Register(
        nameof(Stroke), typeof(Brush), typeof(MoldedFace),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty StrokeThicknessProperty = DependencyProperty.Register(
        nameof(StrokeThickness), typeof(double), typeof(MoldedFace),
        new FrameworkPropertyMetadata(1.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ShadowProperty = DependencyProperty.Register(
        nameof(Shadow), typeof(string), typeof(MoldedFace),
        new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.AffectsRender));

    public Brush? Face
    {
        get => (Brush?)GetValue(FaceProperty);
        set => SetValue(FaceProperty, value);
    }

    public Brush? Stroke
    {
        get => (Brush?)GetValue(StrokeProperty);
        set => SetValue(StrokeProperty, value);
    }

    public double StrokeThickness
    {
        get => (double)GetValue(StrokeThicknessProperty);
        set => SetValue(StrokeThicknessProperty, value);
    }

    /// <summary>The shadow token whose inset layers to draw, e.g. "shadow.controlButton".</summary>
    public string Shadow
    {
        get => (string)GetValue(ShadowProperty);
        set => SetValue(ShadowProperty, value);
    }

    protected override void OnRender(DrawingContext dc)
    {
        var size = Math.Min(ActualWidth, ActualHeight);
        if (size <= 0)
            return;

        var centre = new Point(ActualWidth / 2, ActualHeight / 2);
        var border = StrokeThickness;

        dc.DrawEllipse(Face, null, centre, size / 2, size / 2);

        // CSS draws inset shadows inside the border, so clip them to the inner circle.
        var inner = new EllipseGeometry(centre, size / 2 - border, size / 2 - border);
        for (var n = 1; Shadow.Length > 0 && TryFindResource($"{Shadow}.inset{n}") is Brush brush; n++)
        {
            var x = (double)FindResource($"{Shadow}.inset{n}.x");
            var y = (double)FindResource($"{Shadow}.inset{n}.y");
            var moved = new EllipseGeometry(new Point(centre.X + x, centre.Y + y), inner.RadiusX, inner.RadiusY);
            dc.DrawGeometry(brush, null, new CombinedGeometry(GeometryCombineMode.Exclude, inner, moved));
        }

        if (Stroke is not null && border > 0)
            dc.DrawEllipse(null, new Pen(Stroke, border), centre, (size - border) / 2, (size - border) / 2);
    }
}
