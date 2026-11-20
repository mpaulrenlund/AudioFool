using System.Diagnostics;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using AudioFool;
using AudioFool.Core.Updates;
using AudioFool.ViewModels;

namespace ThemeLab;

/// <summary>
/// --window update [--theme dark] [--dpi 1.25] (2026-11-20): the About row's Refresh
/// button that checks for updates. Renders the logo menu's drop-down without opening
/// it (as --menushot) to &lt;out&gt;-menu.png, and the two dialogs the check shows,
/// "update available" (&lt;out&gt;-available.png) and "no new version"
/// (&lt;out&gt;-none.png), with the app's own words. Checks: the button sits at the
/// About row's right, inside it, centred on the text; its glyph is text.muted; it is
/// bound to CheckForUpdatesCommand and named for screen readers; About's own command
/// is unchanged; the OK-only dialog has one visible button. Then asks GitHub for
/// real (one request) and prints the answer, and checks the start-up's
/// --after-update handling with a short-lived process. "ALL OK".
/// </summary>
internal static partial class Program
{
    private static int RunUpdate(MainWindow main, MainViewModel vm, string outPath, double scale)
    {
        var stem = System.IO.Path.ChangeExtension(System.IO.Path.GetFullPath(outPath), null);
        var allOk = true;
        void Check(bool ok, string what)
        {
            allOk &= ok;
            Console.WriteLine($"  {(ok ? "OK" : "WRONG")}: {what}");
        }

        // ------------------------------------------------------------- the menu
        var logo = (MenuItem)main.FindName("LogoMenuItem");
        var button = (Button)main.FindName("CheckUpdatesButton");
        if (FindFirst<Popup>(logo)?.Child is not FrameworkElement drop)
        {
            Console.WriteLine("  WRONG: the logo menu's drop-down wasn't found");
            return 1;
        }

        for (var i = 0; i < 2; i++)
        {
            drop.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            drop.Arrange(new Rect(drop.DesiredSize));
            drop.UpdateLayout();
            Settle(200);
        }

        Save(drop, stem + "-menu.png", Math.Max(1, drop.ActualWidth), Math.Max(1, drop.ActualHeight), scale);
        Console.WriteLine($"menu: {drop.ActualWidth:0}x{drop.ActualHeight:0} -> {stem}-menu.png");

        MenuItem? about = null;
        for (DependencyObject? n = button; n is not null; n = VisualTreeHelper.GetParent(n) ?? LogicalTreeHelper.GetParent(n))
        {
            if (n is MenuItem m && !ReferenceEquals(m, logo))
            {
                about = m;
                break;
            }
        }

        Check(about is not null, "the button is inside a menu row");
        if (about is not null)
        {
            var row = about.TransformToAncestor(drop).TransformBounds(new Rect(0, 0, about.ActualWidth, about.ActualHeight));
            var b = button.TransformToAncestor(drop).TransformBounds(new Rect(0, 0, button.ActualWidth, button.ActualHeight));
            var text = ((DockPanel)about.Header).Children.OfType<TextBlock>().First();
            var t = text.TransformToAncestor(drop).TransformBounds(new Rect(0, 0, text.ActualWidth, text.ActualHeight));
            Console.WriteLine($"  About row {row.Left:0.#},{row.Top:0.#} {row.Width:0.#}x{row.Height:0.#}; "
                + $"button {b.Left:0.#},{b.Top:0.#} {b.Width:0.#}x{b.Height:0.#}; text {t.Left:0.#}..{t.Right:0.#}");

            var others = logo.Items.OfType<MenuItem>().Where(m => !ReferenceEquals(m, about)).ToList();
            var widest = others.Max(m => m.ActualWidth);
            Check(Math.Abs(about.ActualWidth - widest) < 0.5, $"the About row is as wide as the others ({about.ActualWidth:0.#} vs {widest:0.#})");
            Check(b.Right <= row.Right && b.Top >= row.Top - 0.5 && b.Bottom <= row.Bottom + 0.5, "the button is inside the row");
            var rightGap = row.Right - b.Right;
            Console.WriteLine($"  right gap: {rightGap:0.#} px (row edge to button edge)");
            Check(b.Left - t.Right >= AudioFool.Theming.TokenResources.Current!.Numbers["popup.menuIconButtonGap"] - 0.5, $"at least popup.menuIconButtonGap after the text ({b.Left - t.Right:0.#})");
            var bCentre = (b.Top + b.Bottom) / 2;
            var tCentre = (t.Top + t.Bottom) / 2;
            Check(Math.Abs(bCentre - tCentre) <= 1, $"centred on the text ({bCentre - tCentre:+0.#;-0.#;0})");
            Check(Math.Abs(b.Width - AudioFool.Theming.TokenResources.Current!.Numbers["popup.menuIconButton"]) < 0.5 && Math.Abs(b.Height - AudioFool.Theming.TokenResources.Current!.Numbers["popup.menuIconButton"]) < 0.5,
                  $"popup.menuIconButton square ({b.Width:0.#}x{b.Height:0.#})");

        }

        var glyphBrush = (button.Content as Wpf.Ui.Controls.SymbolIcon)?.Foreground as SolidColorBrush;
        var mutedBrush = Application.Current.TryFindResource("color.text.muted") as SolidColorBrush;
        Check(glyphBrush is not null && mutedBrush is not null && glyphBrush.Color == mutedBrush.Color,
              $"glyph colour {glyphBrush?.Color} = color.text.muted {mutedBrush?.Color}");
        Check(ReferenceEquals(button.Command, vm.CheckForUpdatesCommand), "bound to CheckForUpdatesCommand");
        Check(System.Windows.Automation.AutomationProperties.GetName(button) == "Check for updates", "named \"Check for updates\"");
        Check(!button.Focusable, "not focusable (the menu's arrow keys stay on rows)");
        Check(about is not null && ReferenceEquals(about.Command, vm.ShowAboutCommand), "the row still opens About");

        // ---------------------------------------------------------- the dialogs
        var flags = BindingFlags.NonPublic | BindingFlags.Static;
        var available = (string)typeof(MainViewModel).GetMethod("UpdateAvailableMessage", flags)!.Invoke(null, ["v0.12.0", true])!;
        var none = (string)typeof(MainViewModel).GetMethod("NoUpdateMessage", flags)!.Invoke(null, null)!;
        Console.WriteLine($"  available: \"{available.Replace("\n", "\\n")}\"");
        Console.WriteLine($"  none:      \"{none}\"");

        var confirm = (PromptWindow)Activator.CreateInstance(typeof(PromptWindow), BindingFlags.NonPublic | BindingFlags.Instance, null,
            [main, "Check for Updates", available, "Install", null, null], null)!;
        RenderDialog(confirm, stem + "-available.png");

        var inform = (PromptWindow)typeof(PromptWindow).GetMethod("CreateInform", flags)!.Invoke(null, [main, "Check for Updates", none])!;
        var visibleButtons = FindAll<Button>(inform).Count(x => x.IsVisible);
        RenderDialog(inform, stem + "-none.png");
        Check(visibleButtons == 1, $"the no-update dialog has one button ({visibleButtons})");

        void RenderDialog(Window dialog, string path)
        {
            dialog.ShowActivated = false;
            dialog.ShowInTaskbar = false;
            dialog.Loaded += (_, _) => { dialog.Left = -20000; dialog.Top = -20000; };
            dialog.Show();
            Settle(300);
            Save(dialog, path, dialog.ActualWidth, dialog.ActualHeight, scale);
            Console.WriteLine($"  rendered {System.IO.Path.GetFileName(path)} ({dialog.ActualWidth:0}x{dialog.ActualHeight:0})");
            visibleButtons = FindAll<Button>(dialog).Count(x => x.IsVisible && x.Parent is StackPanel);
            dialog.Close();
        }

        // ------------------------------------------------------ GitHub, for real
        try
        {
            var latest = new UpdateCheck().GetLatestAsync().GetAwaiter().GetResult();
            var current = typeof(MainViewModel).Assembly.GetName().Version!;
            Console.WriteLine(latest is null
                ? "  GitHub: no release with a zip"
                : $"  GitHub: latest {latest.Tag}, {latest.ZipName}, {latest.ZipSize:N0} bytes; this build {current.ToString(3)}; newer: {UpdateCheck.IsNewer(latest.Version, current)}");
            Check(latest is not null, "GitHub's latest release was read");
        }
        catch (UpdateCheckException ex)
        {
            Check(false, $"GitHub check: {ex.Message}");
        }

        // ------------------------------------------------- --after-update at start
        var gone = MainViewModel.TakeAfterUpdateArgs(["--after-update", "999999", @"C:\a song.flac"]);
        Check(gone.SequenceEqual([@"C:\a song.flac"]), $"the update's arguments are taken out ({string.Join(" | ", gone)})");

        using var shortLived = Process.Start(new ProcessStartInfo("ping", "-n 3 127.0.0.1")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
        })!;
        var watch = Stopwatch.StartNew();
        var rest = MainViewModel.TakeAfterUpdateArgs(["--after-update", shortLived.Id.ToString()]);
        watch.Stop();
        Check(shortLived.HasExited && rest.Length == 0, $"waits for the old copy to end ({watch.ElapsedMilliseconds} ms, exited {shortLived.HasExited})");

        Console.WriteLine(allOk ? "ALL OK" : "SOMETHING WRONG");
        return allOk ? 0 : 1;
    }
}
