using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Shapes;
using AudioFool;
using AudioFool.ViewModels;

namespace ThemeLab;

/// <summary>
/// --window statusclick (session 68): the status line and its dot open Statistics
/// on a single click, and hovering either underlines the line. The click is
/// checked against each MouseBinding's gesture (running the command would open
/// Statistics modally, and Windows could put it on screen); the hover by raising
/// MouseEnter and MouseLeave. Renders the window plain and hovered:
/// &lt;out&gt;-plain.png, &lt;out&gt;-hover.png.
/// </summary>
internal static partial class Program
{
    private static int RunStatusClick(MainWindow main, MainViewModel vm, string outPath, double w, double h, double scale)
    {
        var line = (TextBlock)main.FindName("StatusLine");
        var dot = (Ellipse)main.FindName("LibraryDot");
        vm.StatusText = "505 artists · 2,399 albums · 27,095 tracks · 734 GB";
        Pump();

        var allOk = true;
        void Check(bool ok, string what)
        {
            allOk &= ok;
            Console.WriteLine($"  {(ok ? "OK" : "WRONG")}: {what}");
        }

        MouseButtonEventArgs Press(int clicks, UIElement on)
        {
            var args = new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
            {
                RoutedEvent = UIElement.MouseDownEvent,
                Source = on,
            };
            typeof(MouseButtonEventArgs).GetProperty("ClickCount")!.SetValue(args, clicks);
            return args;
        }

        foreach (var (name, element) in new (string, UIElement)[] { ("line", line), ("dot", dot) })
        {
            var binding = element.InputBindings.OfType<MouseBinding>().Single();
            Check(binding.Command == vm.ShowStatisticsCommand, $"{name}: bound to Statistics");
            Check(binding.Gesture.Matches(element, Press(1, element)), $"{name}: a single click opens it");
            Check(element is FrameworkElement { Cursor: var c } && c == Cursors.Hand, $"{name}: hand pointer");
        }

        string Underline() => line.TextDecorations is { Count: > 0 } ? "underlined" : "plain";
        void Hover(UIElement on, bool enter) =>
            on.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, Environment.TickCount)
            {
                RoutedEvent = enter ? UIElement.MouseEnterEvent : UIElement.MouseLeaveEvent,
            });

        Check(Underline() == "plain", $"at rest: {Underline()}");
        var stem = System.IO.Path.ChangeExtension(outPath, null);
        Save(main, stem + "-plain.png", w, h, scale);

        Hover(line, enter: true);
        Pump();
        Check(Underline() == "underlined", $"over the line: {Underline()}");
        Save(main, stem + "-hover.png", w, h, scale);

        // From the line onto the dot and off again.
        Hover(dot, enter: true);
        Hover(line, enter: false);
        Check(Underline() == "underlined", $"over the dot: {Underline()}");
        Hover(dot, enter: false);
        Check(Underline() == "plain", $"off both: {Underline()}");

        Console.WriteLine(allOk ? "ALL OK" : "SOMETHING WRONG");
        return allOk ? 0 : 1;
    }
}
