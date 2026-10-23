using System.Windows;
using System.Windows.Interop;
using AudioFool.Services;

namespace ThemeLab;

/// <summary>
/// --window dialogplacement (session 71): where a dialog opened from the main
/// window goes, on every monitor, with windows that are given a handle and a
/// place but never shown. Maximised (the bug: dialogs centred on the restore
/// position and kept to the primary monitor), the dialog is centred on the main
/// window's monitor; normal, it stays on the main window's monitor.
/// </summary>
internal static partial class Program
{
    private static int RunDialogPlacement()
    {
        var areas = WindowPlacement.WorkAreas();
        Console.WriteLine($"work areas: {string.Join(" | ", areas.Select(a => $"({a.Left},{a.Top})-({a.Right},{a.Bottom})"))}");

        var allOk = true;
        void Check(bool ok, string what)
        {
            allOk &= ok;
            Console.WriteLine($"  {(ok ? "OK" : "WRONG")}: {what}");
        }

        Window Hidden(double width, double height)
        {
            var w = new Window { ShowActivated = false, ShowInTaskbar = false, Width = width, Height = height, Left = 0, Top = 0 };
            new WindowInteropHelper(w).EnsureHandle();
            return w;
        }

        foreach (var (area, i) in areas.Select((a, i) => (a, i)))
        {
            // Maximised, its Left and Top are where it would restore to (40, 30 in),
            // which the old code centred on.
            var owner = Hidden(1200, 700);
            WindowPlacement.Apply(owner, new AudioFool.Core.Settings.WindowBounds(area.Left + 40, area.Top + 30, 1200, 700, Maximized: true));

            var work = DialogPlacement.WorkAreaOf(owner);
            var dialog = Hidden(600, 400);
            DialogPlacement.CentreOverOwner(dialog, owner, 600, 400);
            var centreX = dialog.Left + 300;
            var centreY = dialog.Top + 200;
            Check(Math.Abs(centreX - (work.Left + work.Width / 2)) < 1 && Math.Abs(centreY - (work.Top + work.Height / 2)) < 1,
                $"monitor {i + 1}, main window maximised: dialog centred at ({centreX:0},{centreY:0}) on its work area {work}");

            // Normal, near the right edge: centred over it as far as its monitor allows.
            owner.WindowState = WindowState.Normal;
            WindowPlacement.Apply(owner, new AudioFool.Core.Settings.WindowBounds(area.Right - 500, area.Top + 100, 1200, 700, Maximized: false));
            work = DialogPlacement.WorkAreaOf(owner);
            DialogPlacement.CentreOverOwner(dialog, owner, 600, 400);
            Check(dialog.Left >= work.Left - 0.5 && dialog.Left + 600 <= work.Right + 0.5 && dialog.Top >= work.Top - 0.5 && dialog.Top + 400 <= work.Bottom + 0.5,
                $"monitor {i + 1}, main window normal against the right edge: dialog at ({dialog.Left:0},{dialog.Top:0}) inside {work}");

            dialog.Close();
            owner.Close();
        }

        Console.WriteLine(allOk ? "ALL OK" : "SOMETHING WRONG");
        return allOk ? 0 : 1;
    }
}
