using System.ComponentModel;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using AudioFool;
using AudioFool.Core.Equalizer;
using AudioFool.Core.Settings;
using AudioFool.Core.Theme;
using AudioFool.ViewModels;
using CommunityToolkit.Mvvm.ComponentModel;

namespace ThemeLab;

/// <summary>
/// --window equalizer [--theme dark] [--eqpreset "IER-M9 ChatGPT"] (session 79): the
/// status bar's EQ chip and its preset menu. Measures the chip against
/// Bit-Perfect's (height, gaps, face, border, corners, dot, label) off and on,
/// reads its accessible name, and builds the menu through the window's own
/// OpenEqualizerMenu with the preset in use and with the EQ off. The menu's popup
/// is never opened: as the chip's ContextMenu is set, its popup is made and the
/// popup's IsOpen binding cut, so "IsOpen = true" opens no window (the case stops
/// if WPF's internals don't look as expected). The menu is then laid out and drawn
/// on its own, and over the window where it would open. The on state is set by
/// reflection, as ChooseEqualizer saves settings. Then a live switch to the other
/// theme and back, with the chip's region cropped for comparison with a fresh
/// start. Peace's presets are read, never written. Renders &lt;out&gt;-off.png,
/// -on.png, -menu-on.png, -menu-off.png, -menu-over.png, -chip-*.png crops; "ALL OK".
/// </summary>
internal static partial class Program
{
    private static int RunEqualizer(MainWindow main, MainViewModel vm, string outPath, double w, double h, double scale, string presetName)
    {
        var stem = System.IO.Path.ChangeExtension(outPath, null);
        var allOk = true;
        void Check(bool ok, string what)
        {
            allOk &= ok;
            Console.WriteLine($"  {(ok ? "OK" : "WRONG")}: {what}");
        }

        var chip = (ButtonBase)main.FindName("EqualizerChip");
        var bitPerfect = (ButtonBase)main.FindName("BitPerfectToggle");
        var themeButton = (ButtonBase)main.FindName("ThemeButton");
        var lastFm = (ButtonBase)main.FindName("LastFmIndicator");
        var statusBar = (Grid)main.FindName("StatusBar");
        var eqDot = (Ellipse)main.FindName("EqualizerDot");
        var bpDot = (Ellipse)main.FindName("BitPerfectDot");
        var eqLabel = FindAll<TextBlock>(chip).First();
        var bpLabel = FindAll<TextBlock>(bitPerfect).First();
        Pump();

        Rect Box(FrameworkElement e) => e.TransformToAncestor(main).TransformBounds(new Rect(e.RenderSize));
        string R(Rect r) => $"({r.Left:0.##},{r.Top:0.##} {r.Width:0.##}x{r.Height:0.##})";
        Color Token(string key) => ((SolidColorBrush)main.FindResource(key)).Color;

        // ---------------------------------------------------------- geometry
        Console.WriteLine($"theme: {ThemeService.Current}");
        var bar = Box(statusBar);
        var eq = Box(chip);
        var bp = Box(bitPerfect);
        var th = Box(themeButton);
        Console.WriteLine($"status bar {R(bar)}; Last.fm {(lastFm.IsVisible ? R(Box(lastFm)) : "hidden")}; Theme {R(th)}; EQ {R(eq)}; Bit-Perfect {R(bp)} (enabled {bitPerfect.IsEnabled})");
        Check(Math.Abs(eq.Height - bp.Height) < 0.01 && Math.Abs(eq.Height - 26) < 0.01, $"EQ height {eq.Height:0.##} = Bit-Perfect {bp.Height:0.##} = 26");
        Check(Math.Abs(eq.Top - bp.Top) < 0.01, $"EQ top {eq.Top:0.##} = Bit-Perfect top {bp.Top:0.##}");
        var gapThemeEq = eq.Left - th.Right;
        var gapEqBp = bp.Left - eq.Right;
        Check(Math.Abs(gapThemeEq - 10) < 0.01, $"gap Theme→EQ {gapThemeEq:0.##} px");
        Check(Math.Abs(gapEqBp - 10) < 0.01, $"gap EQ→Bit-Perfect {gapEqBp:0.##} px");
        if (lastFm.IsVisible)
        {
            var lf = Box(lastFm);
            Check(Math.Abs(th.Left - lf.Right - 10) < 0.01, $"gap Last.fm→Theme {th.Left - lf.Right:0.##} px");
        }
        Console.WriteLine($"  Bit-Perfect right {bp.Right:0.##}, status bar content right {bar.Right:0.##} (margin {statusBar.Margin})");
        Check(th.Right <= eq.Left && eq.Right <= bp.Left, "order Theme, EQ, Bit-Perfect, left to right");
        Check(Grid.GetColumn(chip) == 4 && Grid.GetColumn(bitPerfect) == 5 && Grid.GetColumn(themeButton) == 3, "columns Theme 3, EQ 4, Bit-Perfect 5");

        Check(eqDot.ActualWidth == 7 && eqDot.ActualHeight == 7 && bpDot.ActualWidth == 7, $"dot {eqDot.ActualWidth}x{eqDot.ActualHeight} (Bit-Perfect {bpDot.ActualWidth}x{bpDot.ActualHeight})");
        Check(eqDot.StrokeThickness == bpDot.StrokeThickness && Equals(Describe(eqDot.Stroke), Describe(bpDot.Stroke)),
            $"dot ring {Describe(eqDot.Stroke)} {eqDot.StrokeThickness} = Bit-Perfect's");
        Check(Math.Abs(Box(eqDot).Left - eq.Left - (Box(bpDot).Left - bp.Left)) < 0.01, $"dot inset {Box(eqDot).Left - eq.Left:0.##} = Bit-Perfect {Box(bpDot).Left - bp.Left:0.##}");
        Check(Math.Abs(Box(eqLabel).Left - Box(eqDot).Right - (Box(bpLabel).Left - Box(bpDot).Right)) < 0.01,
            $"dot→label gap {Box(eqLabel).Left - Box(eqDot).Right:0.##} = Bit-Perfect {Box(bpLabel).Left - Box(bpDot).Right:0.##}");
        Check(Math.Abs(eq.Right - Box(eqLabel).Right - (bp.Right - Box(bpLabel).Right)) < 0.01,
            $"label→right edge {eq.Right - Box(eqLabel).Right:0.##} = Bit-Perfect {bp.Right - Box(bpLabel).Right:0.##}");

        var sameText = eqLabel.FontFamily.Source == bpLabel.FontFamily.Source && eqLabel.FontSize == bpLabel.FontSize
            && eqLabel.FontWeight == bpLabel.FontWeight && eqLabel.FontStyle == bpLabel.FontStyle
            && Describe(eqLabel.Foreground) == Describe(bpLabel.Foreground) && ReferenceEquals(eqLabel.Style, bpLabel.Style);
        Check(sameText, $"label text style: {eqLabel.FontFamily.Source} {eqLabel.FontSize} {eqLabel.FontWeight} {Describe(eqLabel.Foreground)} "
            + $"vs Bit-Perfect {bpLabel.FontFamily.Source} {bpLabel.FontSize} {bpLabel.FontWeight} {Describe(bpLabel.Foreground)}");
        Check(Math.Abs(Box(eqLabel).Top - eq.Top - (Box(bpLabel).Top - bp.Top)) < 0.01 && Math.Abs(eqLabel.ActualHeight - bpLabel.ActualHeight) < 0.01,
            $"label box top in chip {Box(eqLabel).Top - eq.Top:0.##} h {eqLabel.ActualHeight:0.##} = Bit-Perfect {Box(bpLabel).Top - bp.Top:0.##} h {bpLabel.ActualHeight:0.##}");

        var eqFace = (Border)chip.Template.FindName("Chip", chip);
        var bpFace = (Border)bitPerfect.Template.FindName("Chip", bitPerfect);
        Check(Describe(eqFace.Background) == Describe(bpFace.Background) && Describe(eqFace.BorderBrush) == Describe(bpFace.BorderBrush)
              && eqFace.CornerRadius == bpFace.CornerRadius && eqFace.BorderThickness == bpFace.BorderThickness,
            $"face {Describe(eqFace.Background)} border {Describe(eqFace.BorderBrush)} {eqFace.BorderThickness} radius {eqFace.CornerRadius} = Bit-Perfect's");
        Check(((SolidColorBrush)eqFace.Background).Color == Token("color.status.chipBg") && ((SolidColorBrush)eqFace.BorderBrush).Color == Token("color.status.chipBorder"),
            "face and border = status.chipBg / status.chipBorder");

        // ---------------------------------------------------------- pixels, off then on
        var flags = BindingFlags.NonPublic | BindingFlags.Instance;
        var raise = typeof(ObservableObject).GetMethod("OnPropertyChanged", flags, [typeof(string)])!;
        void SetEq(EqPreset? preset, string? folder)
        {
            typeof(MainViewModel).GetField("_equalizerPreset", flags)!.SetValue(vm, preset);
            typeof(MainViewModel).GetField("_equalizerFolder", flags)!.SetValue(vm, folder);
            raise.Invoke(vm, [nameof(MainViewModel.IsEqualizerOn)]);
            raise.Invoke(vm, [nameof(MainViewModel.EqualizerAutomationName)]);
            Pump();
        }

        string Name() =>
            $"{AutomationProperties.GetName(chip)} / peer '{UIElementAutomationPeer.CreatePeerForElement(chip).GetName()}'";

        var menuPresets = vm.LoadEqualizerMenu();
        Console.WriteLine($"Peace presets offered ({menuPresets.Peace.Count}): {string.Join(" | ", menuPresets.Peace.Select(p => p.Name))}");
        Console.WriteLine($"AudioFool folder presets offered ({menuPresets.Own.Count}): {string.Join(" | ", menuPresets.Own.Select(p => p.Name))}");
        var chosen = menuPresets.Peace.FirstOrDefault(p => p.Name == presetName) ?? menuPresets.Peace.FirstOrDefault()
            ?? new EqPreset(presetName, -3, [], [], EqPresetFormat.Peace);
        Console.WriteLine($"preset used for the on state: {chosen.Name}");

        // The chip as each state draws it, with the chip's region kept for the switch.
        var chipRect = new Int32Rect((int)Math.Floor(eq.Left * scale) - 1, (int)Math.Floor(eq.Top * scale) - 1,
            (int)Math.Ceiling(eq.Width * scale) + 3, (int)Math.Ceiling(eq.Height * scale) + 3);
        var pixelW = (int)Math.Round(w * scale);

        void MeasureChip(string state, Color dotToken)
        {
            var path = $"{stem}-{state}.png";
            Save(main, path, w, h, scale);
            var shot = LoadShot(path);
            Crop(shot, chipRect, $"{stem}-chip-{ThemeService.Current}-{state}.png".ToLowerInvariant());

            int X(double v) => (int)Math.Round(v * scale);
            // Face: a pixel left of the dot, mid-height; border: the top edge's middle.
            var eqFacePx = shot.At(X(eq.Left + 3), X(eq.Top + eq.Height / 2));
            var bpFacePx = shot.At(X(bp.Left + 3), X(bp.Top + bp.Height / 2));
            var eqEdgePx = shot.At(X(eq.Left + eq.Width / 2), X(eq.Top));
            var bpEdgePx = shot.At(X(bp.Left + bp.Width / 2), X(bp.Top));
            Check(eqFacePx == bpFacePx && eqFacePx == Token("color.status.chipBg"), $"[{state}] face pixel {Hex(eqFacePx)} = Bit-Perfect {Hex(bpFacePx)} = chipBg {Hex(Token("color.status.chipBg"))}");
            Check(eqEdgePx == bpEdgePx && eqEdgePx == Token("color.status.chipBorder"), $"[{state}] border pixel {Hex(eqEdgePx)} = Bit-Perfect {Hex(bpEdgePx)} = chipBorder {Hex(Token("color.status.chipBorder"))}");

            // Corners: the 5x5 block at each corner, EQ's against Bit-Perfect's.
            var cornerDiff = 0;
            foreach (var (cx, cy) in new[] { (0, 0), (1, 0), (0, 1), (1, 1) })
                for (var dy = 0; dy < 5; dy++)
                    for (var dx = 0; dx < 5; dx++)
                    {
                        int Px(Rect r) => cx == 0 ? X(r.Left) + dx : X(r.Right) - 1 - dx;
                        int Py(Rect r) => cy == 0 ? X(r.Top) + dy : X(r.Bottom) - 1 - dy;
                        if (shot.At(Px(eq), Py(eq)) != shot.At(Px(bp), Py(bp)))
                            cornerDiff++;
                    }
            Check(cornerDiff == 0, $"[{state}] corners: {cornerDiff} of 100 corner pixels differ from Bit-Perfect's");

            // Dot: its centre and its ink box.
            var eqDotBox = Box(eqDot);
            var bpDotBox = Box(bpDot);
            var centre = shot.At(X(eqDotBox.Left + eqDotBox.Width / 2), X(eqDotBox.Top + eqDotBox.Height / 2));
            Check(centre == dotToken, $"[{state}] dot centre {Hex(centre)} = token {Hex(dotToken)}");
            var eqInk = shot.Ink(Inflate(eqDotBox, 2, scale), eqFacePx);
            var bpInk = shot.Ink(Inflate(bpDotBox, 2, scale), bpFacePx);
            Check(eqInk.Width == bpInk.Width && eqInk.Height == bpInk.Height && eqInk.Y - X(eq.Top) == bpInk.Y - X(bp.Top),
                $"[{state}] dot ink {eqInk.Width}x{eqInk.Height} at chip y+{eqInk.Y - X(eq.Top)} = Bit-Perfect {bpInk.Width}x{bpInk.Height} at y+{bpInk.Y - X(bp.Top)}");

            // Label: ink box, vertical room above and below against Bit-Perfect's.
            var eqText = shot.Ink(Inflate(Box(eqLabel), 1, scale), eqFacePx);
            var bpText = shot.Ink(Inflate(Box(bpLabel), 1, scale), bpFacePx);
            var eqAbove = eqText.Y - X(eq.Top);
            var bpAbove = bpText.Y - X(bp.Top);
            var eqBelow = X(eq.Bottom) - (eqText.Y + eqText.Height);
            var bpBelow = X(bp.Bottom) - (bpText.Y + bpText.Height);
            Console.WriteLine($"  [{state}] label ink: EQ {eqText.Width}x{eqText.Height} above {eqAbove} below {eqBelow}; Bit-Perfect {bpText.Width}x{bpText.Height} above {bpAbove} below {bpBelow}");
            // "Bit-Perfect"'s f and t rise above the capitals and EQ's Q dips below the
            // line, so the whole boxes differ; the first capital of each (E, B) is
            // compared: cap top and baseline.
            var eqCap = shot.Ink(new Int32Rect(eqText.X, eqText.Y - 1, 5, eqText.Height + 2), eqFacePx);
            var bpCap = shot.Ink(new Int32Rect(bpText.X, bpText.Y - 1, 5, bpText.Height + 2), bpFacePx);
            Console.WriteLine($"  [{state}] first capital: E top {eqCap.Y - X(eq.Top)} bottom {eqCap.Y + eqCap.Height - X(eq.Top)}; "
                + $"B top {bpCap.Y - X(bp.Top)} bottom {bpCap.Y + bpCap.Height - X(bp.Top)} (chip height {X(eq.Height)})");
            Check(eqCap.Y - X(eq.Top) == bpCap.Y - X(bp.Top) && eqCap.Height == bpCap.Height,
                $"[{state}] cap top and baseline level with Bit-Perfect's: room above {eqCap.Y - X(eq.Top)}, below {X(eq.Bottom) - eqCap.Y - eqCap.Height}");
            var darkest = shot.Extreme(Inflate(Box(eqLabel), 1, scale), eqFacePx);
            var bpDarkest = shot.Extreme(Inflate(Box(bpLabel), 1, scale), bpFacePx);
            Console.WriteLine($"  [{state}] label strongest ink: EQ {Hex(darkest)}, Bit-Perfect {Hex(bpDarkest)}, text.status {Hex(Token("color.text.status"))}");
            Check(darkest == bpDarkest, $"[{state}] label ink colour matches Bit-Perfect's");
            Console.WriteLine($"  [{state}] accessible name: {Name()}");
        }

        SetEq(null, null);
        Check(!vm.IsEqualizerOn && AutomationProperties.GetName(chip) == "Equalizer: off", $"off: name '{AutomationProperties.GetName(chip)}'");
        MeasureChip("off", Token("color.status.eqOff"));
        SetEq(chosen, AppSettings.EqualizerInPeace);
        Check(vm.IsEqualizerOn && AutomationProperties.GetName(chip) == $"Equalizer: {chosen.Name}", $"on: name '{AutomationProperties.GetName(chip)}'");
        MeasureChip("on", Token("color.status.eqOn"));
        Check(Token("color.status.eqOff") == Token("color.status.bitPerfectOff") && Token("color.status.eqOn") == Token("color.status.bitPerfectOn"),
            $"tokens eqOff {Hex(Token("color.status.eqOff"))} / eqOn {Hex(Token("color.status.eqOn"))} = bitPerfectOff/On");

        // ---------------------------------------------------------- the menu
        var reference = new ContextMenu();
        reference.Items.Add(new MenuItem { Header = "Edit Tags…" });
        reference.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        reference.Arrange(new Rect(reference.DesiredSize));
        reference.UpdateLayout();
        var refBox = FindAll<Border>(reference).First(b => b.BorderThickness.Left > 0);

        void Menu(string state, string checkedHeader)
        {
            Console.WriteLine($"menu, {state}:");
            var menu = OpenEqualizerMenuClosed(main, chip);
            var popup = (Popup)typeof(ContextMenu).GetField("_parentPopup", flags)!.GetValue(menu)!;
            Check(!popup.IsOpen && PresentationSource.FromVisual(menu) is null, $"no popup window opened (menu IsOpen={menu.IsOpen}, popup IsOpen={popup.IsOpen})");
            Check(menu.Placement == PlacementMode.Top && ReferenceEquals(menu.PlacementTarget, chip), $"placement {menu.Placement} on {(menu.PlacementTarget as FrameworkElement)?.Name}: opens above the chip");

            menu.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            menu.Arrange(new Rect(menu.DesiredSize));
            menu.UpdateLayout();
            menu.RaiseEvent(new RoutedEventArgs(ContextMenu.OpenedEvent, menu));   // as opening does: MenuCorners rounds the highlights
            Settle(200);
            menu.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            menu.Arrange(new Rect(menu.DesiredSize));
            menu.UpdateLayout();

            var entries = menu.Items.Cast<object>().Select(i => i switch
            {
                MenuItem m => $"{AccessText(m.Header)}{(m.IsChecked ? " [x]" : "")}",
                Separator => "----",
                _ => i.ToString() ?? "",
            }).ToList();
            Console.WriteLine($"  items: {string.Join(" | ", entries)}");

            var expected = new List<string> { "Off", "----" };
            expected.AddRange(menuPresets.Peace.Select(p => p.Name));
            if (menuPresets.Own.Count > 0)
            {
                expected.Add("----");
                expected.AddRange(menuPresets.Own.Select(p => p.Name));
            }
            expected.Add("----");
            expected.Add("Open EQ Folder…");
            if (MainViewModel.PeaceExecutable is not null)
                expected.Add("Open Peace…");
            Console.WriteLine($"  Peace's editor: {MainViewModel.PeaceExecutable ?? "not installed"}");
            Check(entries.Select(e => e.Replace(" [x]", "")).SequenceEqual(expected), "items in order: Off, line, Peace's, [line, own], line, Open EQ Folder…, Open Peace… (when installed)");
            var checkedItems = menu.Items.OfType<MenuItem>().Where(m => m.IsChecked).Select(m => AccessText(m.Header)).ToList();
            Check(checkedItems.SequenceEqual([checkedHeader]), $"checked: {string.Join(", ", checkedItems)} (expected {checkedHeader})");

            // The check's part: an element in the checked item's template that is
            // shown there and not in an unchecked one (the pixels are checked below).
            var items = menu.Items.OfType<MenuItem>().ToList();
            var on = items.First(m => m.IsChecked);
            var off = items.First(m => !m.IsChecked);
            var shown = VisibleParts(on).Except(VisibleParts(off)).ToList();
            Console.WriteLine($"  parts shown on '{AccessText(on.Header)}' and not on '{AccessText(off.Header)}': {string.Join(", ", shown)}");

            var box = FindAll<Border>(menu).First(b => b.BorderThickness.Left > 0);
            Check(Describe(menu.Background) == Describe(reference.Background) && ((SolidColorBrush)menu.Background).Color == Token("color.panel.bg"),
                $"menu bg {Describe(menu.Background)} = panel.bg, = other context menus");
            Check(Describe(menu.BorderBrush) == Describe(reference.BorderBrush) && ((SolidColorBrush)menu.BorderBrush).Color == Token("color.panel.border"),
                $"menu border {Describe(menu.BorderBrush)} = panel.border, = other context menus");
            Check(box.CornerRadius == refBox.CornerRadius && box.CornerRadius == (CornerRadius)main.FindResource("radius.surface"),
                $"menu corners {box.CornerRadius} = radius.surface = other context menus {refBox.CornerRadius}");
            var foregrounds = items.Select(m => Describe(m.Foreground)).Distinct().ToList();
            Check(foregrounds.Count == 1 && ((SolidColorBrush)items[0].Foreground).Color == Token("color.text.primary"),
                $"item text {string.Join(", ", foregrounds)} = text.primary (other menus' items: {Describe(((MenuItem)reference.Items[0]).Foreground)})");

            // Clipping: each header's text needs no more room than it has, inside the box.
            var widest = 0.0;
            var widestName = "";
            var clipped = new List<string>();
            foreach (var item in items)
            {
                var text = FindAll<TextBlock>(item).FirstOrDefault(t => t.Text == AccessText(item.Header) && Shown(t, item));
                if (text is null)
                {
                    clipped.Add($"{AccessText(item.Header)} (no text found)");
                    continue;
                }
                var need = new FormattedText(text.Text, System.Globalization.CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                    new Typeface(text.FontFamily, text.FontStyle, text.FontWeight, text.FontStretch), text.FontSize, Brushes.Black, 1).WidthIncludingTrailingWhitespace;
                var at = text.TransformToAncestor(menu).TransformBounds(new Rect(text.RenderSize));
                if (need > widest)
                    (widest, widestName) = (need, text.Text);
                if (need > text.ActualWidth + 0.5 || at.Right > menu.ActualWidth - box.BorderThickness.Right - box.Padding.Right + 0.01)
                    clipped.Add($"{text.Text} (needs {need:0.#}, has {text.ActualWidth:0.#}, right {at.Right:0.#} of {menu.ActualWidth:0.#})");
            }
            Check(clipped.Count == 0, $"no text clipped{(clipped.Count > 0 ? ": " + string.Join("; ", clipped) : "")}; longest '{widestName}' {widest:0.#} px in a {menu.ActualWidth:0.#}x{menu.ActualHeight:0.#} menu");

            var menuPath = $"{stem}-menu-{state}.png";
            Save(menu, menuPath, Math.Max(1, menu.ActualWidth), Math.Max(1, menu.ActualHeight), scale);

            // The check as drawn. WPF-UI gives every checkable row a box (as the
            // Libraries menu's folders have) and shows the tick (CheckBoxIcon) in the
            // checked one only. Open EQ Folder isn't a choice: no box, a folder icon
            // in its place (as the Libraries menu's Add Folder… has).
            var drawn = LoadShot(menuPath);
            Console.WriteLine($"  menu png {drawn.W}x{drawn.H}; edge pixel {Hex(drawn.At(0, drawn.H / 2))} (panel.border {Hex(Token("color.panel.border"))}), inside {Hex(drawn.At(3, drawn.H / 2))}");
            var inked = InkLeftOfText(menu, drawn, Token("color.panel.bg"), scale);
            var ticked = items.Where(m => VisibleParts(m).Contains("TextBlock#CheckBoxIcon")).Select(m => AccessText(m.Header)).ToList();
            Check(ticked.Count == 1 && ticked[0] == checkedHeader, $"tick shown only on {checkedHeader}: on {string.Join("; ", ticked)}");
            var actions = items.SkipWhile(m => AccessText(m.Header) != "Open EQ Folder…").ToList();
            Check(inked.Count == items.Count && actions.Count >= 1 && actions.All(m => !m.IsCheckable && m.Icon is not null),
                $"a box on every choice and an icon on {string.Join(", ", actions.Select(m => $"'{AccessText(m.Header)}'"))} (marks on {inked.Count} of {items.Count} rows)");
            Console.WriteLine($"  where each row's mark and text sit: {string.Join("; ", inked.Where(i => i.StartsWith("Off (") || actions.Any(m => i.StartsWith(AccessText(m.Header) + " ("))))}");

            // Where it opens: its bottom on the chip's top, its left on the chip's left.
            var windowPath = $"{stem}-{state}.png";
            var over = new DrawingVisual();
            using (var dc = over.RenderOpen())
            {
                dc.DrawImage(LoadFrame(windowPath), new Rect(0, 0, w, h));
                dc.DrawImage(LoadFrame(menuPath), new Rect(eq.Left, eq.Top - menu.ActualHeight, menu.ActualWidth, menu.ActualHeight));
            }
            var rtb = new RenderTargetBitmap(pixelW, (int)Math.Round(h * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
            rtb.Render(over);
            WritePng(rtb, $"{stem}-menu-over-{state}.png");
            menu.IsOpen = false;
            Pump();
        }

        Menu("on", chosen.Name);

        // An edit made in Peace, taken up as the menu opens: a fresh read with the
        // same sound changes nothing; one with a different preamp is played (the
        // engine gets it) and becomes the one in use; one no longer offered leaves
        // the one in use as it was. No file is touched: the reads are made up.
        {
            var engine = (AudioFool.Core.Playback.AudioEngine)typeof(MainViewModel).GetField("_engine", flags)!.GetValue(vm)!;
            EqPreset InUse() => (EqPreset)typeof(MainViewModel).GetField("_equalizerPreset", flags)!.GetValue(vm)!;
            engine.Equalizer = chosen.IsSupported ? chosen : null;

            var same = chosen with { Bands = [.. chosen.Bands] };
            vm.TakeUpEqualizerEdit(new MainViewModel.EqualizerMenu([same], []));
            Check(ReferenceEquals(InUse(), chosen), "a fresh read with the same sound: the preset in use is kept");

            var edited = chosen with { PreampDb = chosen.PreampDb - 1.5, Bands = [.. chosen.Bands] };
            vm.TakeUpEqualizerEdit(new MainViewModel.EqualizerMenu([edited], []));
            Check(ReferenceEquals(InUse(), edited) && ReferenceEquals(engine.Equalizer, edited),
                $"an edit (preamp {chosen.PreampDb:0.0} → {edited.PreampDb:0.0} dB) is taken up and played");

            vm.TakeUpEqualizerEdit(new MainViewModel.EqualizerMenu([], []));
            Check(ReferenceEquals(InUse(), edited), "a preset no longer offered: the one in use keeps playing");

            engine.Equalizer = null;
            SetEq(chosen, AppSettings.EqualizerInPeace);
        }

        // Diagnostic: the same item template with IsChecked alone (as the EQ menu
        // sets it) and with IsCheckable too (as the Libraries submenu does).
        var probe = new ContextMenu();
        probe.Items.Add(new MenuItem { Header = "IsChecked only", IsChecked = true });
        probe.Items.Add(new MenuItem { Header = "IsCheckable and IsChecked", IsCheckable = true, IsChecked = true });
        for (var pass = 0; pass < 2; pass++)
        {
            probe.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            probe.Arrange(new Rect(probe.DesiredSize));
            probe.UpdateLayout();
            Settle(100);
        }
        Save(probe, $"{stem}-menu-checkprobe.png", probe.ActualWidth, probe.ActualHeight, scale);
        var probeInk = InkLeftOfText(probe, LoadShot($"{stem}-menu-checkprobe.png"), Token("color.panel.bg"), scale);
        Console.WriteLine($"check probe: ink left of the text on: {(probeInk.Count == 0 ? "none" : string.Join("; ", probeInk))}");
        SetEq(null, null);
        Menu("off", "Off");

        // ---------------------------------------------------------- live switch
        var start = ThemeService.Current;
        var other = start == AppTheme.Dark ? AppTheme.Light : AppTheme.Dark;
        ThemeService.Switch(other);
        main.UpdateLayout();
        Pump();
        Console.WriteLine($"switched to {ThemeService.Current}:");
        foreach (var (state, preset) in new[] { ("off", (EqPreset?)null), ("on", chosen) })
        {
            SetEq(preset, preset is null ? null : AppSettings.EqualizerInPeace);
            var path = $"{stem}-switched-{state}.png";
            Save(main, path, w, h, scale);
            Crop(LoadShot(path), chipRect, $"{stem}-chip-switchedto{ThemeService.Current}-{state}.png".ToLowerInvariant());
            var dot = (SolidColorBrush)eqDot.Fill;
            Check(dot.Color == Token(preset is null ? "color.status.eqOff" : "color.status.eqOn"), $"[{state}] dot fill {Hex(dot.Color)} follows");
        }

        ThemeService.Switch(start);
        main.UpdateLayout();
        Pump();
        Console.WriteLine($"back to {ThemeService.Current}:");
        foreach (var (state, preset) in new[] { ("off", (EqPreset?)null), ("on", chosen) })
        {
            SetEq(preset, preset is null ? null : AppSettings.EqualizerInPeace);
            var path = $"{stem}-back-{state}.png";
            Save(main, path, w, h, scale);
            var back = LoadShot(path);
            var first = LoadShot($"{stem}-{state}.png");
            Check(first.Diff(back, chipRect) == 0, $"[{state}] chip region after the round trip = at start ({first.Diff(back, chipRect)} pixels differ)");
        }

        Console.WriteLine($"chip crop rect (px): {chipRect.X},{chipRect.Y} {chipRect.Width}x{chipRect.Height}");
        Console.WriteLine(allOk ? "ALL OK" : "SOMETHING WRONG");
        return allOk ? 0 : 1;
    }

    /// <summary>
    /// Calls the window's own OpenEqualizerMenu, but cuts the popup's IsOpen binding
    /// the moment the menu is handed to the chip, before "IsOpen = true", so no
    /// popup window ever opens. Throws, before anything opens, if WPF's internals
    /// aren't as expected.
    /// </summary>
    private static ContextMenu OpenEqualizerMenuClosed(MainWindow main, ButtonBase chip)
    {
        var flags = BindingFlags.NonPublic | BindingFlags.Instance;
        var hookup = typeof(ContextMenu).GetMethod("HookupParentPopup", flags)
            ?? throw new InvalidOperationException("ContextMenu.HookupParentPopup not found: not opening the menu");
        var popupField = typeof(ContextMenu).GetField("_parentPopup", flags)
            ?? throw new InvalidOperationException("ContextMenu._parentPopup not found: not opening the menu");

        ContextMenu? captured = null;
        void OnMenuSet(object? sender, EventArgs e)
        {
            if (chip.ContextMenu is not { } menu || ReferenceEquals(menu, captured))
                return;
            if (popupField.GetValue(menu) is null)
                hookup.Invoke(menu, null);
            var popup = popupField.GetValue(menu) as Popup
                ?? throw new InvalidOperationException("no popup after HookupParentPopup: not opening the menu");
            if (BindingOperations.GetBindingExpression(popup, Popup.IsOpenProperty) is null)
                throw new InvalidOperationException("the popup's IsOpen isn't bound to the menu: not opening it");
            BindingOperations.ClearBinding(popup, Popup.IsOpenProperty);
            popup.IsOpen = false;
            captured = menu;
        }

        var descriptor = DependencyPropertyDescriptor.FromProperty(FrameworkElement.ContextMenuProperty, chip.GetType());
        descriptor.AddValueChanged(chip, OnMenuSet);
        try
        {
            typeof(MainWindow).GetMethod("OpenEqualizerMenu", flags)!.Invoke(main, null);
        }
        finally
        {
            descriptor.RemoveValueChanged(chip, OnMenuSet);
        }

        return captured ?? throw new InvalidOperationException("OpenEqualizerMenu set no menu on the chip");
    }

    private static string AccessText(object header) => header is string s ? s.Replace("__", "_") : header?.ToString() ?? "";

    /// <summary>The named template parts drawn in an item (visible, with some size).</summary>
    private static IEnumerable<string> VisibleParts(MenuItem item) =>
        FindAll<FrameworkElement>(item)
            .Where(e => Shown(e, item) && e.ActualWidth > 0 && e.ActualHeight > 0)
            .Select(e => $"{e.GetType().Name}#{e.Name}")
            .Where(n => !n.EndsWith('#'));

    /// <summary>The items of <paramref name="menu"/> with ink left of their text (a check mark), as drawn in <paramref name="drawn"/>.</summary>
    private static List<string> InkLeftOfText(ContextMenu menu, EqShot drawn, Color ground, double scale)
    {
        var inked = new List<string>();
        foreach (var item in menu.Items.OfType<MenuItem>())
        {
            var text = FindAll<TextBlock>(item).First(t => t.Text == AccessText(item.Header) && Shown(t, item));
            var row = item.TransformToAncestor(menu).TransformBounds(new Rect(item.RenderSize));
            var textLeft = text.TransformToAncestor(menu).TransformBounds(new Rect(text.RenderSize)).Left;
            var area = new Int32Rect((int)Math.Ceiling(row.Left * scale) + 2, (int)Math.Ceiling(row.Top * scale),
                (int)Math.Floor((textLeft - row.Left) * scale) - 4, (int)Math.Floor(row.Height * scale));
            var ink = area.Width > 0 ? drawn.Ink(area, ground) : Int32Rect.Empty;
            if (!ink.IsEmpty)
                inked.Add($"{AccessText(item.Header)} ({ink.Width}x{ink.Height} at x {ink.X}, text at {textLeft * scale:0})");
        }
        return inked;
    }

    /// <summary>
    /// Visible, opaque, all the way up to <paramref name="root"/>. IsVisible can't
    /// say: it is false for anything not in a shown window, as this menu never is.
    /// </summary>
    private static bool Shown(FrameworkElement e, DependencyObject root)
    {
        for (DependencyObject? node = e; node is not null && !ReferenceEquals(node, root); node = VisualTreeHelper.GetParent(node))
            if (node is UIElement u && (u.Visibility != Visibility.Visible || u.Opacity == 0))
                return false;
        return true;
    }

    private static Int32Rect Inflate(Rect r, int by, double scale) =>
        new((int)Math.Floor(r.Left * scale) - by, (int)Math.Floor(r.Top * scale) - by,
            (int)Math.Ceiling(r.Width * scale) + 2 * by, (int)Math.Ceiling(r.Height * scale) + 2 * by);

    private static string Hex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

    private static BitmapFrame LoadFrame(string path)
    {
        using var stream = File.OpenRead(path);
        return BitmapFrame.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
    }

    private static void WritePng(BitmapSource bitmap, string path)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    private static EqShot LoadShot(string path)
    {
        var bgra = new FormatConvertedBitmap(LoadFrame(path), PixelFormats.Bgra32, null, 0);
        var px = new byte[bgra.PixelWidth * bgra.PixelHeight * 4];
        bgra.CopyPixels(px, bgra.PixelWidth * 4, 0);
        return new EqShot(bgra, px, bgra.PixelWidth, bgra.PixelHeight);
    }

    private static void Crop(EqShot shot, Int32Rect rect, string path) => WritePng(new CroppedBitmap(shot.Source, rect), path);

    private sealed record EqShot(BitmapSource Source, byte[] Px, int W, int H)
    {
        public Color At(int x, int y)
        {
            var i = (y * W + x) * 4;
            return Color.FromRgb(Px[i + 2], Px[i + 1], Px[i]);
        }

        private static int Distance(Color a, Color b) => Math.Abs(a.R - b.R) + Math.Abs(a.G - b.G) + Math.Abs(a.B - b.B);

        /// <summary>The bounding box of pixels that differ from <paramref name="ground"/> by more than 60.</summary>
        public Int32Rect Ink(Int32Rect area, Color ground)
        {
            int left = int.MaxValue, top = int.MaxValue, right = -1, bottom = -1;
            for (var y = area.Y; y < area.Y + area.Height; y++)
                for (var x = area.X; x < area.X + area.Width; x++)
                    if (Distance(At(x, y), ground) > 60)
                    {
                        left = Math.Min(left, x); top = Math.Min(top, y);
                        right = Math.Max(right, x); bottom = Math.Max(bottom, y);
                    }
            return right < 0 ? Int32Rect.Empty : new Int32Rect(left, top, right - left + 1, bottom - top + 1);
        }

        /// <summary>The pixel furthest from <paramref name="ground"/>: the text's own colour where a stroke is solid.</summary>
        public Color Extreme(Int32Rect area, Color ground)
        {
            var best = ground;
            for (var y = area.Y; y < area.Y + area.Height; y++)
                for (var x = area.X; x < area.X + area.Width; x++)
                    if (Distance(At(x, y), ground) > Distance(best, ground))
                        best = At(x, y);
            return best;
        }

        public int Diff(EqShot other, Int32Rect area)
        {
            var n = 0;
            for (var y = area.Y; y < area.Y + area.Height; y++)
                for (var x = area.X; x < area.X + area.Width; x++)
                    if (At(x, y) != other.At(x, y))
                        n++;
            return n;
        }
    }
}
