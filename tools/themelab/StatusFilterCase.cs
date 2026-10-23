using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using AudioFool;
using AudioFool.Core.Library;
using AudioFool.ViewModels;

namespace ThemeLab;

/// <summary>
/// --window statusfilter (2026-10-23): the bare × after the status text that
/// clears a Statistics filter. Hidden with no filter; shown right after the text
/// with one, after a save's message too; a long line is trimmed and the × stays
/// in the status bar; hover turns it text.primary; a click clears the filter and
/// brings back the line from before. Renders &lt;out&gt;-off, -on, -saved, -long
/// and -hover .png.
/// </summary>
internal static partial class Program
{
    private static int RunStatusFilter(MainWindow main, MainViewModel vm, string outPath, double w, double h, double scale)
    {
        var line = (TextBlock)main.FindName("StatusLine");
        var clear = (Button)main.FindName("StatusClearFilter");
        var bar = (FrameworkElement)main.FindName("StatusBar");
        var stem = System.IO.Path.ChangeExtension(outPath, null);

        // The real library (read only), so the line shows real totals: the
        // sample library never reaches the status line's counts.
        LoadRealLibrary(vm);
        var describe = typeof(MainViewModel).GetMethod("DescribeStatus", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        string Totals() => (string)describe.Invoke(vm, [Activator.CreateInstance(describe.GetParameters()[0].ParameterType)])!;
        vm.StatusText = Totals();
        Settle(300);

        var allOk = true;
        void Check(bool ok, string what)
        {
            allOk &= ok;
            Console.WriteLine($"  {(ok ? "OK" : "WRONG")}: {what}");
        }

        double Left(FrameworkElement e) => e.TranslatePoint(new Point(0, 0), bar).X;

        var before = vm.StatusText;
        Check(clear.Visibility == Visibility.Collapsed, $"no filter: × {clear.Visibility}");
        Save(main, stem + "-off.png", w, h, scale);

        vm.LibraryFilter = new TrackFilter("Embedded PNG covers", t => t.FileSize % 2 == 0);
        Settle(300);
        Check(clear.Visibility == Visibility.Visible, $"filter on: × {clear.Visibility}, line '{vm.StatusText}'");
        var gap = Left(clear) - (Left(line) + line.ActualWidth);
        Check(Math.Abs(gap) < 0.5, $"× right after the text: gap {gap:0.##} px");
        var refresh = (FrameworkElement)main.FindName("RefreshButton");
        Check(clear.ActualWidth == refresh.ActualWidth && clear.ActualHeight == refresh.ActualHeight,
            $"hit area {clear.ActualWidth}×{clear.ActualHeight}, as Refresh");
        var name = System.Windows.Automation.AutomationProperties.GetName(clear);
        Check(name == "Clear filter: Embedded PNG covers", $"name '{name}'");
        Check(clear.ToolTip is null, "no tooltip");
        Check(!IsWithin(clear, line), "not part of the line, so a click doesn't open Statistics");
        Check(clear.Foreground == main.FindResource("color.text.muted"), "at rest: text.muted");
        var filtered = vm.StatusText;
        Save(main, stem + "-on.png", w, h, scale);

        vm.StatusText = "Saved tags for 14 track(s). · 0 left: Embedded PNG covers";
        Settle(200);
        gap = Left(clear) - (Left(line) + line.ActualWidth);
        Check(Math.Abs(gap) < 0.5, $"after a save: gap {gap:0.##} px");
        Save(main, stem + "-saved.png", w, h, scale);

        vm.StatusText = string.Join(" · ", Enumerable.Repeat("Saved tags for 14 track(s). 0 left: Embedded PNG covers", 8));
        Settle(200);
        // The left part of the status bar: Refresh, the dot, then the line and the ×.
        var column = (FrameworkElement)VisualTreeHelper.GetParent(VisualTreeHelper.GetParent(line));
        var columnRight = Left(column) + column.ActualWidth;
        var clearRight = Left(clear) + clear.ActualWidth;
        Check(clear.Visibility == Visibility.Visible && clearRight <= columnRight + 0.5,
            $"long line: × ends at {clearRight:0} of {columnRight:0} px");
        Check(IsTrimmed(line), $"long line: the text is trimmed to {line.ActualWidth:0} px");
        Save(main, stem + "-long.png", w, h, scale);

        vm.StatusText = filtered;
        Settle(200);
        var isMouseOverKey = (DependencyPropertyKey)typeof(UIElement)
            .GetField("IsMouseOverPropertyKey", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(null)!;
        clear.SetValue(isMouseOverKey, true);
        Settle(200);
        Check(clear.Foreground == main.FindResource("color.text.primary"), "hover: text.primary");
        Check(line.TextDecorations is not { Count: > 0 }, "hover on the ×: the line isn't underlined");
        Save(main, stem + "-hover.png", w, h, scale);
        clear.ClearValue(isMouseOverKey);

        clear.Command.Execute(null);
        Settle(300);
        Check(!vm.IsFiltered, "click: filter cleared");
        Check(clear.Visibility == Visibility.Collapsed, $"click: × {clear.Visibility}");
        Check(vm.StatusText == before && before == Totals() && before.Contains(" artists · "), $"click: line back to '{vm.StatusText}'");

        Console.WriteLine(allOk ? "ALL OK" : "SOMETHING WRONG");
        return allOk ? 0 : 1;
    }

    private static bool IsWithin(DependencyObject element, DependencyObject ancestor)
    {
        for (var e = element; e is not null; e = VisualTreeHelper.GetParent(e))
        {
            if (e == ancestor)
                return true;
        }

        return false;
    }

    /// <summary>Whether the text needs more width than it has, so the ellipsis shows.</summary>
    private static bool IsTrimmed(TextBlock text)
    {
        var typeface = new Typeface(text.FontFamily, text.FontStyle, text.FontWeight, text.FontStretch);
        var formatted = new FormattedText(text.Text, System.Globalization.CultureInfo.CurrentCulture,
            text.FlowDirection, typeface, text.FontSize, Brushes.Black, VisualTreeHelper.GetDpi(text).PixelsPerDip);
        return formatted.WidthIncludingTrailingWhitespace > text.ActualWidth + 0.5;
    }
}
