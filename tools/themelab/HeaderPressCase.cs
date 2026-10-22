using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using AudioFool;

namespace ThemeLab;

/// <summary>
/// --window headerpress (session 68): a press on a letter of each Songs header
/// label. LetterSpacing draws those labels as one Run per letter, and a press on a
/// letter starts at the Run, which has no visual parent: the song table's press
/// handler threw "'System.Windows.Documents.Run' is not a Visual or Visual3D"
/// climbing from it. Raised on the Run as the mouse would, so the handler gets the
/// Run as its source; then the header is clicked, and the sort reported.
/// </summary>
internal static partial class Program
{
    private static int RunHeaderPress(MainWindow main)
    {
        var grid = (DataGrid)main.FindName("TrackGrid");
        var headers = Descendants<DataGridColumnHeader>(grid).Where(h => h.Column is not null).ToList();
        var allOk = headers.Count > 0;

        foreach (var header in headers)
        {
            var run = Descendants<TextBlock>(header).SelectMany(t => t.Inlines.OfType<Run>()).FirstOrDefault();
            if (run is null)
            {
                Console.WriteLine($"  {header.Column.Header}: no letter-spaced label");
                continue;
            }

            // What the old climb did with this source.
            var oldClimb = "fine";
            try { VisualTreeHelper.GetParent(run); }
            catch (InvalidOperationException) { oldClimb = "throws"; }

            try
            {
                run.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
                {
                    RoutedEvent = UIElement.PreviewMouseLeftButtonDownEvent,
                });
                run.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
                {
                    RoutedEvent = UIElement.PreviewMouseLeftButtonUpEvent,
                });

                // The header's own click, which sorts.
                typeof(ButtonBase).GetMethod("OnClick", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                    .Invoke(header, null);
                Pump();
                Console.WriteLine($"  OK: press on '{run.Text}' of {header.Column.Header} (old climb {oldClimb}); sorted {header.Column.SortDirection?.ToString() ?? "no"}");
            }
            catch (Exception ex)
            {
                allOk = false;
                Console.WriteLine($"  WRONG: press on '{run.Text}' of {header.Column.Header}: {ex.GetType().Name}: {ex.Message}");
            }
        }

        Console.WriteLine(allOk ? "ALL OK" : "SOMETHING WRONG");
        main.Close();
        return allOk ? 0 : 1;
    }
}
