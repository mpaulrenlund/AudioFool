using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using AudioFool.Core.Analysis;
using AudioFool.Services;
using AudioFool.Theming;
using AudioFool.ViewModels;
using Wpf.Ui.Controls;
using TextBlock = System.Windows.Controls.TextBlock;

namespace AudioFool;

/// <summary>
/// One track's spectrogram and AudioFool's opinion of its quality. Opened from
/// the track grid's "Analyze…" and not modal: it can sit beside the main window,
/// and several can be open at once to compare tracks. Closing it stops an
/// analysis still running.
/// </summary>
public partial class AnalysisWindow : FluentWindow
{
    private readonly AnalysisViewModel _viewModel;
    private readonly CancellationTokenSource _cancel = new();

    public AnalysisWindow(AnalysisViewModel viewModel, Window owner)
    {
        _viewModel = viewModel;
        DataContext = viewModel;

        InitializeComponent();

        Owner = owner;
        Legend.Background = SpectrogramImage.LegendBrush(TokenResources.Current!);

        viewModel.PropertyChanged += ViewModel_PropertyChanged;
        PictureFrame.SizeChanged += (_, _) => DrawAxes();
        SizeChanged += (_, _) => KeepOnScreen();

        Loaded += (_, _) =>
        {
            PlaceOverOwner(owner);
            DrawAxes();
            _ = viewModel.RunAsync(_cancel.Token);
        };
    }

    protected override void OnClosed(EventArgs e)
    {
        _cancel.Cancel();
        _viewModel.PropertyChanged -= ViewModel_PropertyChanged;
        base.OnClosed(e);
    }

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(AnalysisViewModel.Analysis) or nameof(AnalysisViewModel.Opinion))
            DrawAxes();
    }

    /// <summary>
    /// Centred over the main window, each further analysis window stepped down
    /// and right by a title bar's height so they don't stack exactly.
    /// </summary>
    private void PlaceOverOwner(Window owner)
    {
        var others = Application.Current.Windows.OfType<AnalysisWindow>().Count(w => w != this && w.IsLoaded);
        var step = TokenResources.Current!.Numbers["layout.titleBarHeight"] * (others % 6);

        Left = owner.Left + ((owner.ActualWidth - ActualWidth) / 2) + step;
        Top = owner.Top + ((owner.ActualHeight - ActualHeight) / 2) + step;
        KeepOnScreen();
    }

    /// <summary>The opinion arrives after the window opens and makes it taller.</summary>
    private void KeepOnScreen()
    {
        if (!IsLoaded)
            return;

        var work = SystemParameters.WorkArea;
        Left = Math.Clamp(Left, work.Left, Math.Max(work.Left, work.Right - ActualWidth));
        Top = Math.Clamp(Top, work.Top, Math.Max(work.Top, work.Bottom - ActualHeight));
    }

    /// <summary>
    /// Labels the frequency axis, the time axis and the colour scale, and marks
    /// the cutoff, all placed by the picture's current size.
    /// </summary>
    private void DrawAxes()
    {
        FrequencyAxis.Children.Clear();
        TimeAxis.Children.Clear();
        LegendAxis.Children.Clear();
        Overlay.Children.Clear();

        var tokens = TokenResources.Current!;
        var border = tokens.Numbers["layout.panelBorder"];
        var height = PictureFrame.ActualHeight - (2 * border);
        var width = PictureFrame.ActualWidth - (2 * border);
        if (height <= 0 || width <= 0)
            return;

        // The colour scale is known before any analysis.
        var floor = tokens.Numbers["analysis.floorDb"];
        var top = tokens.Numbers["analysis.topDb"];
        var dbStep = tokens.Numbers["analysis.legendStepDb"];
        for (var db = top; db >= floor; db -= dbStep)
        {
            var y = border + ((top - db) / (top - floor) * height);
            AddLabel(LegendAxis, FormatDb(db), y, AxisSide.Left);
        }

        if (_viewModel.Analysis is not { } analysis)
            return;

        var nyquist = analysis.SampleRate / 2.0;
        var step = NiceStep(nyquist / 1000, Math.Max(2, (int)(height / tokens.Numbers["analysis.freqLabelSpacing"]))) * 1000;
        for (var hz = 0.0; hz <= nyquist + 1; hz += step)
        {
            var y = border + height - (hz / nyquist * height);
            AddLabel(FrequencyAxis, hz == 0 ? "0" : QualityOpinion.Khz(hz), y, AxisSide.Right);
        }

        var seconds = analysis.Duration.TotalSeconds;
        if (seconds > 0)
        {
            var tick = NiceStep(seconds, Math.Max(2, (int)(width / tokens.Numbers["analysis.timeLabelSpacing"])));
            for (var t = 0.0; t <= seconds + 0.001; t += tick)
                AddTimeLabel(Formatting.Display.Time(TimeSpan.FromSeconds(t)), border + (t / seconds * width), width);
        }

        if (_viewModel.Opinion?.CutoffHz is { } cutoff)
            MarkCutoff(cutoff, nyquist, width, height);
    }

    /// <summary>A dashed line across the picture at the cutoff, labelled at its right end.</summary>
    private void MarkCutoff(double cutoff, double nyquist, double width, double height)
    {
        var tokens = TokenResources.Current!;
        var brush = (Brush)FindResource("color.spectrogram.marker");
        var thickness = tokens.Numbers["analysis.markerThickness"];
        var dash = tokens.Numbers["analysis.markerDash"];

        // Whole pixels plus half the stroke, so a 1 px line lands on one row.
        var y = Math.Round(height - (cutoff / nyquist * height)) + (thickness / 2);

        Overlay.Children.Add(new Line
        {
            X1 = 0,
            X2 = width,
            Y1 = y,
            Y2 = y,
            Stroke = brush,
            StrokeThickness = thickness,
            StrokeDashArray = [dash / thickness, dash / thickness],
            SnapsToDevicePixels = true,
        });

        var label = new TextBlock
        {
            Text = QualityOpinion.Khz(cutoff),
            Style = (Style)FindResource("AxisLabel"),
            Foreground = brush,
        };
        label.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var gap = tokens.Numbers["analysis.axisGap"];
        Canvas.SetLeft(label, width - label.DesiredSize.Width - gap);
        Canvas.SetTop(label, Math.Max(0, y - label.DesiredSize.Height - (gap / 2)));
        Overlay.Children.Add(label);
    }

    private enum AxisSide { Left, Right }

    /// <summary>A label centred on <paramref name="y"/>, against the picture's side of its canvas.</summary>
    private void AddLabel(Canvas canvas, string text, double y, AxisSide align)
    {
        var label = new TextBlock { Text = text, Style = (Style)FindResource("AxisLabel") };
        label.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var size = label.DesiredSize;

        Canvas.SetLeft(label, align == AxisSide.Right ? canvas.ActualWidth - size.Width : 0);
        Canvas.SetTop(label, y - (size.Height / 2));
        canvas.Children.Add(label);
    }

    /// <summary>Centred under its tick, but kept within the picture's width.</summary>
    private void AddTimeLabel(string text, double x, double width)
    {
        var label = new TextBlock { Text = text, Style = (Style)FindResource("AxisLabel") };
        label.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var w = label.DesiredSize.Width;

        Canvas.SetLeft(label, Math.Clamp(x - (w / 2), 0, Math.Max(0, width - w)));
        Canvas.SetTop(label, 0);
        TimeAxis.Children.Add(label);
    }

    /// <summary>A round step (1, 2, 5 × a power of ten) giving at most about <paramref name="count"/> ticks.</summary>
    private static double NiceStep(double range, int count)
    {
        var raw = range / Math.Max(1, count);
        var power = Math.Pow(10, Math.Floor(Math.Log10(raw)));
        foreach (var m in new[] { 1, 2, 5, 10 })
        {
            if (m * power >= raw)
                return m * power;
        }

        return 10 * power;
    }

    /// <summary>A proper minus sign, as the footer's peak uses.</summary>
    private static string FormatDb(double db) =>
        db.ToString("0", CultureInfo.InvariantCulture).Replace("-", "−") + " dB";

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (e.Key is Key.Escape)
        {
            Close();
            e.Handled = true;
            return;
        }

        base.OnPreviewKeyDown(e);
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
