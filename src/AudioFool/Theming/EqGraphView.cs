using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using AudioFool.Core.Equalizer;

namespace AudioFool.Theming;

/// <summary>
/// The album header's EQ graph (session 80, the user's design, mock-up A): the
/// preset's response from 10 Hz to 20 kHz (<see cref="EqGraph"/>), a dashed line at
/// the scale's top and bottom, a solid one at 0 dB, and a dot at each band. Compact
/// (the panel too narrow for the band numbers, mock-up D): no dots, the area
/// between the curve and 0 dB shaded instead.
/// <para>
/// Every brush is a dependency property set by <c>DynamicResource</c>, so a theme
/// switch, or the playing cover's colour arriving through <c>theme:CoverAccent</c>,
/// draws it again; nothing is held from an earlier lookup. The curve keeps clear of
/// the edges by a dot's radius, so the first and last dots aren't cut;
/// <see cref="EqFrequencyAxis"/> uses the same inset.
/// </para>
/// </summary>
public sealed class EqGraphView : FrameworkElement
{
    public static readonly DependencyProperty GraphProperty = Register<EqGraph?>(nameof(Graph), null);
    public static readonly DependencyProperty CompactProperty = Register(nameof(Compact), false);
    public static readonly DependencyProperty CurveBrushProperty = Register<Brush?>(nameof(CurveBrush), null);
    public static readonly DependencyProperty GridBrushProperty = Register<Brush?>(nameof(GridBrush), null);
    public static readonly DependencyProperty ZeroBrushProperty = Register<Brush?>(nameof(ZeroBrush), null);
    public static readonly DependencyProperty InsetYProperty = Register(nameof(InsetY), 0.0);
    public static readonly DependencyProperty CurveThicknessProperty = Register(nameof(CurveThickness), 1.0);
    public static readonly DependencyProperty DotRadiusProperty = Register(nameof(DotRadius), 2.0);
    public static readonly DependencyProperty GridDashProperty = Register(nameof(GridDash), 2.0);
    public static readonly DependencyProperty GridGapProperty = Register(nameof(GridGap), 3.0);
    public static readonly DependencyProperty FillOpacityProperty = Register(nameof(FillOpacity), 0.0);

    private static DependencyProperty Register<T>(string name, T fallback) =>
        DependencyProperty.Register(name, typeof(T), typeof(EqGraphView),
            new FrameworkPropertyMetadata(fallback, FrameworkPropertyMetadataOptions.AffectsRender));

    public EqGraph? Graph { get => (EqGraph?)GetValue(GraphProperty); set => SetValue(GraphProperty, value); }
    public bool Compact { get => (bool)GetValue(CompactProperty); set => SetValue(CompactProperty, value); }
    public Brush? CurveBrush { get => (Brush?)GetValue(CurveBrushProperty); set => SetValue(CurveBrushProperty, value); }
    public Brush? GridBrush { get => (Brush?)GetValue(GridBrushProperty); set => SetValue(GridBrushProperty, value); }
    public Brush? ZeroBrush { get => (Brush?)GetValue(ZeroBrushProperty); set => SetValue(ZeroBrushProperty, value); }

    /// <summary>How far in from the top and bottom the scale's ends sit: half an axis label, so the labels centre on them.</summary>
    public double InsetY { get => (double)GetValue(InsetYProperty); set => SetValue(InsetYProperty, value); }
    public double CurveThickness { get => (double)GetValue(CurveThicknessProperty); set => SetValue(CurveThicknessProperty, value); }
    public double DotRadius { get => (double)GetValue(DotRadiusProperty); set => SetValue(DotRadiusProperty, value); }
    public double GridDash { get => (double)GetValue(GridDashProperty); set => SetValue(GridDashProperty, value); }
    public double GridGap { get => (double)GetValue(GridGapProperty); set => SetValue(GridGapProperty, value); }
    public double FillOpacity { get => (double)GetValue(FillOpacityProperty); set => SetValue(FillOpacityProperty, value); }

    /// <summary>The room left clear at each side: a dot's radius, plus half the curve's line.</summary>
    public double InsetX => DotRadius + CurveThickness / 2;

    protected override void OnRender(DrawingContext dc)
    {
        var width = ActualWidth;
        var height = ActualHeight;
        if (Graph is not { } graph || width <= 2 * InsetX || height <= 2 * InsetY)
            return;

        var top = InsetY;
        var bottom = height - InsetY;
        var zero = (top + bottom) / 2;
        var perDb = (bottom - top) / 2 / graph.RangeDb;
        var left = InsetX;
        var across = width - 2 * InsetX;
        Point At(EqGraphPoint p) => new(left + p.X * across, zero - p.Db * perDb);

        // Lines on whole pixels plus a half, so a 1 px line is one crisp row.
        static double Crisp(double y) => Math.Round(y) + 0.5;
        if (GridBrush is { } grid)
        {
            var dashed = Frozen(new Pen(grid, 1) { DashStyle = new DashStyle([GridDash, GridGap], 0) });
            dc.DrawLine(dashed, new Point(0, Crisp(top)), new Point(width, Crisp(top)));
            dc.DrawLine(dashed, new Point(0, Crisp(bottom)), new Point(width, Crisp(bottom)));
        }
        if (ZeroBrush is { } zeroBrush)
            dc.DrawLine(Frozen(new Pen(zeroBrush, 1)), new Point(0, Crisp(zero)), new Point(width, Crisp(zero)));

        if (CurveBrush is not { } curveBrush)
            return;

        var curve = new StreamGeometry();
        using (var g = curve.Open())
        {
            g.BeginFigure(At(graph.Curve[0]), isFilled: false, isClosed: false);
            g.PolyLineTo(graph.Curve.Skip(1).Select(At).ToList(), isStroked: true, isSmoothJoin: true);
        }
        curve.Freeze();

        if (Compact && FillOpacity > 0)
        {
            var area = new StreamGeometry();
            using (var g = area.Open())
            {
                g.BeginFigure(new Point(left, zero), isFilled: true, isClosed: true);
                g.PolyLineTo(graph.Curve.Select(At).Append(new Point(left + across, zero)).ToList(), isStroked: false, isSmoothJoin: false);
            }
            area.Freeze();
            dc.PushOpacity(FillOpacity);
            dc.DrawGeometry(curveBrush, null, area);
            dc.Pop();
        }

        var pen = Frozen(new Pen(curveBrush, CurveThickness) { LineJoin = PenLineJoin.Round, StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round });
        dc.DrawGeometry(null, pen, curve);

        if (!Compact)
            foreach (var dot in graph.Dots)
                dc.DrawEllipse(curveBrush, null, At(dot), DotRadius, DotRadius);
    }

    private static T Frozen<T>(T f) where T : Freezable
    {
        f.Freeze();
        return f;
    }
}

/// <summary>
/// The compact EQ graph's frequency labels: each child centred under its
/// <c>Hz</c> on <see cref="EqGraph"/>'s log axis, inset as <see cref="EqGraphView"/>
/// draws it, and kept inside the panel.
/// </summary>
public sealed class EqFrequencyAxis : Panel
{
    public static readonly DependencyProperty HzProperty = DependencyProperty.RegisterAttached(
        "Hz", typeof(double), typeof(EqFrequencyAxis),
        new FrameworkPropertyMetadata(EqGraph.LowestHz, FrameworkPropertyMetadataOptions.AffectsParentArrange));

    public static readonly DependencyProperty InsetXProperty = DependencyProperty.Register(
        nameof(InsetX), typeof(double), typeof(EqFrequencyAxis),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsArrange));

    public static double GetHz(DependencyObject d) => (double)d.GetValue(HzProperty);
    public static void SetHz(DependencyObject d, double value) => d.SetValue(HzProperty, value);

    public double InsetX { get => (double)GetValue(InsetXProperty); set => SetValue(InsetXProperty, value); }

    protected override Size MeasureOverride(Size available)
    {
        var height = 0.0;
        foreach (UIElement child in InternalChildren)
        {
            child.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            height = Math.Max(height, child.DesiredSize.Height);
        }
        return new Size(double.IsInfinity(available.Width) ? 0 : available.Width, height);
    }

    protected override Size ArrangeOverride(Size final)
    {
        var across = Math.Max(0, final.Width - 2 * InsetX);
        foreach (UIElement child in InternalChildren)
        {
            var size = child.DesiredSize;
            var centre = InsetX + EqGraph.Position(GetHz(child)) * across;
            var x = Math.Clamp(centre - size.Width / 2, 0, Math.Max(0, final.Width - size.Width));
            child.Arrange(new Rect(new Point(x, 0), size));
        }
        return final;
    }
}
