using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AudioFool;
using AudioFool.Core.Art;
using AudioFool.Core.Equalizer;
using AudioFool.Core.Settings;
using AudioFool.Core.Theme;
using AudioFool.Theming;
using AudioFool.ViewModels;
using CommunityToolkit.Mvvm.ComponentModel;

namespace ThemeLab;

/// <summary>
/// --window eqpanel --w 1720 --h 1080 [--theme dark] [--dpi 1.25] [--switch dark]
/// [--eqfile &lt;.peace&gt;] [--compareto &lt;full.png&gt;] (session 80): the album header's EQ panel.
/// The preset (the test fixture IER-M9 ChatGPT.peace unless --eqfile) is put in use by
/// reflection, as ChooseEqualizer saves settings; nothing is saved and Peace is never
/// started (the panel's Click is never raised). Checks: hidden with the EQ off; full
/// mode's width, band columns, dots, name, preamp, fit beside the title and inside the
/// art's height; the scale's labels on the graph's three lines; the curve's colour,
/// the theme's accent.playback or the playing cover's fitted as the seekbar's, through
/// a switch and back; window widths where compact and hidden start; a playlist
/// header; CanOpenEqualizerInPeace and the hand pointer; an edit saved to the file
/// taken up (a copy in scratch), a flat one not; the accessible name; nothing
/// clipped. --compareto: the panel's region of -full.png against another render's
/// (a live switch against a fresh start). Renders &lt;out&gt;-off/-full/-cover/-compact/
/// -hidden/-playlist/-edited.png; "ALL OK".
/// </summary>
internal static partial class Program
{
    private static int RunEqPanel(MainWindow main, MainViewModel vm, string outPath, double w, double h, double scale)
    {
        var args = Environment.GetCommandLineArgs();
        var stem = System.IO.Path.ChangeExtension(outPath, null);
        var allOk = true;
        void Check(bool ok, string what)
        {
            allOk &= ok;
            Console.WriteLine($"  {(ok ? "OK" : "WRONG")}: {what}");
        }

        var flags = BindingFlags.NonPublic | BindingFlags.Instance;
        var raise = typeof(ObservableObject).GetMethod("OnPropertyChanged", flags, [typeof(string)])!;
        var changed = typeof(MainViewModel).GetMethod("OnEqualizerChanged", flags)!;
        EqPreset? InUse() => (EqPreset?)typeof(MainViewModel).GetField("_equalizerPreset", flags)!.GetValue(vm);
        void SetEq(EqPreset? preset, string? folder)
        {
            typeof(MainViewModel).GetField("_equalizerPreset", flags)!.SetValue(vm, preset);
            typeof(MainViewModel).GetField("_equalizerFolder", flags)!.SetValue(vm, folder);
            changed.Invoke(vm, null);
            main.UpdateLayout();
            Pump();
        }

        var panel = (Button)main.FindName("EqualizerPanel");
        var headerGrid = (Grid)main.FindName("HeaderGrid");
        var nameText = (TextBlock)main.FindName("EqualizerName");
        var scaleGrid = (Grid)main.FindName("EqualizerScale");
        var view = (EqGraphView)main.FindName("EqualizerGraphView");
        var bands = (ItemsControl)main.FindName("EqualizerBands");
        var freqs = (EqFrequencyAxis)main.FindName("EqualizerFrequencies");
        var seek = (Slider)main.FindName("SeekBar");
        var headerArtist = (TextBlock)main.FindName("HeaderArtist");
        var titleColumn = (FrameworkElement)((FrameworkElement)headerArtist.Parent).Parent;
        var albumHeader = (Grid)titleColumn.Parent;
        var art = (FrameworkElement)albumHeader.Children[0];
        var app = Application.Current;

        Rect Box(FrameworkElement e) => e.TransformToAncestor(main).TransformBounds(new Rect(e.RenderSize));
        string R(Rect r) => $"({r.Left:0.##},{r.Top:0.##} {r.Width:0.##}x{r.Height:0.##})";
        double Tok(string key) => (double)app.FindResource(key);
        Int32Rect Px(Rect r, int by = 0) => new(
            (int)Math.Floor(r.Left * scale) - by, (int)Math.Floor(r.Top * scale) - by,
            (int)Math.Ceiling(r.Width * scale) + 2 * by, (int)Math.Ceiling(r.Height * scale) + 2 * by);
        var magenta = _backdrop == Color.FromRgb(0xFF, 0x00, 0xFF);
        int Backdrop(EqShot shot)
        {
            var n = 0;
            for (var y = 0; y < shot.H; y++)
                for (var x = 0; x < shot.W; x++)
                    if (shot.At(x, y) == _backdrop) n++;
            return n;
        }
        EqShot Render(string name, double width)
        {
            var path = $"{stem}-{name}.png";
            Save(main, path, width, h, scale);
            var shot = LoadShot(path);
            if (magenta)
                Check(Backdrop(shot) == 0, $"[{name}] magenta pixels: {Backdrop(shot)}");
            return shot;
        }

        Pump();
        Console.WriteLine($"theme: {ThemeService.Current}; dpi scale {scale}; Peace's editor: {MainViewModel.PeaceExecutable ?? "not installed"}");
        Console.WriteLine($"header: '{vm.AlbumHeaderTitle}' shown {vm.ShowsAlbumHeader}; HeaderGrid {R(Box(headerGrid))}");

        // ---------------------------------------------------------- EQ off
        vm.NowPlayingCoverColor = null;
        SetEq(null, null);
        var offHeader = Box(headerGrid);
        Check(panel.Visibility == Visibility.Collapsed && vm.EqualizerGraph is null, $"EQ off: panel {panel.Visibility}, graph {(vm.EqualizerGraph is null ? "null" : "set")}");
        if (_switchTo is null)
            Render("off", w);

        // ---------------------------------------------------------- the preset
        var fixture = Arg(args, "--eqfile")
            ?? Path.Combine(AppContext.BaseDirectory, @"..\..\..\..\..\..\tests\AudioFool.Core.Tests\TestData\eq\IER-M9 ChatGPT.peace");
        fixture = Path.GetFullPath(fixture);
        var preset = PeacePresetReader.Read(fixture);
        Console.WriteLine($"preset: '{preset.Name}' from {fixture}; preamp {preset.PreampDb}; {preset.Bands.Count} bands; supported {preset.IsSupported}");
        SetEq(preset, AppSettings.EqualizerInPeace);
        var graph = vm.EqualizerGraph!;

        // --switch: drawn once in the start theme, then switched live.
        if (_switchTo is not null)
        {
            var throwaway = new RenderTargetBitmap((int)Math.Round(w * scale), (int)Math.Round(h * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
            throwaway.Render(main);
            SwitchIfAsked();
            main.UpdateLayout();
            Pump();
        }

        // ---------------------------------------------------------- 1. full mode
        Console.WriteLine("1. full mode:");
        var header = Box(headerGrid);
        var pBox = Box(panel);
        var artBox = Box(art);
        var titleBox = Box(titleColumn);
        Console.WriteLine($"  header {R(header)}, art {R(artBox)}, title column {R(titleBox)}, panel {R(pBox)}, graph {R(Box(view))}");
        Check(panel.Visibility == Visibility.Visible, $"panel {panel.Visibility}");
        Check(Math.Abs(panel.ActualWidth - Tok("eqPanel.width")) < 0.01, $"width {panel.ActualWidth:0.##} = eqPanel.width {Tok("eqPanel.width")}");
        Check(!view.Compact, $"Compact {view.Compact}");
        var grid = FindAll<UniformGrid>(bands).FirstOrDefault();
        var columns = grid?.Children.Count ?? 0;
        var columnWidth = grid is null || columns == 0 ? 0 : grid.ActualWidth / columns;
        Check(bands.Visibility == Visibility.Visible && bands.Items.Count == 13 && columns == 13,
            $"band columns: {columns} ({bands.Items.Count} items), {columnWidth:0.##} px each (min {Tok("eqPanel.minColumnWidth")})");
        Check(graph.Dots.Count == 13, $"dots: {graph.Dots.Count}");
        Check(freqs.Visibility == Visibility.Collapsed, $"compact frequency labels {freqs.Visibility}");
        Check(pBox.Bottom <= artBox.Bottom + 0.01, $"panel bottom {pBox.Bottom:0.##} <= art bottom {artBox.Bottom:0.##} ({artBox.Bottom - pBox.Bottom:0.##} to spare)");
        Check(Math.Abs(header.Height - offHeader.Height) < 0.01, $"header height {header.Height:0.##} = with the EQ off {offHeader.Height:0.##}");
        Check(titleBox.Right <= pBox.Left + 0.01, $"title column right {titleBox.Right:0.##} <= panel left {pBox.Left:0.##} (gap {pBox.Left - titleBox.Right:0.##}; title column {titleBox.Width:0.##} wide)");
        Check(pBox.Right <= header.Right + 0.01 && Math.Abs(header.Right - pBox.Right - Tok("albumHeader.paddingX")) < 0.01,
            $"panel right {pBox.Right:0.##}, header right {header.Right:0.##} (inset {header.Right - pBox.Right:0.##})");
        Check(Math.Abs(pBox.Top - artBox.Top) < 0.01, $"panel top {pBox.Top:0.##} = art top {artBox.Top:0.##}");
        Check(nameText.Text == "IER-M9 ChatGPT" && graph.Name == "IER-M9 ChatGPT", $"name '{nameText.Text}'");
        var nameNeed = new FormattedText(nameText.Text, System.Globalization.CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            new Typeface(nameText.FontFamily, nameText.FontStyle, nameText.FontWeight, nameText.FontStretch), nameText.FontSize, Brushes.Black, 1).WidthIncludingTrailingWhitespace;
        Check(nameNeed <= nameText.ActualWidth + 0.5, $"name not trimmed: needs {nameNeed:0.#}, has {nameText.ActualWidth:0.#}");
        var preampText = FindAll<TextBlock>(panel).FirstOrDefault(t => t.Text == graph.Preamp);
        Check(preampText is not null && graph.Preamp == "Preamp −5.1 dB", $"preamp text '{graph.Preamp}' shown {preampText is not null}");
        var clipped = FindAll<TextBlock>(bands).Where(t => t.DesiredSize.Width > columnWidth + 0.01).Select(t => t.Text).ToList();
        Check(clipped.Count == 0, $"band labels fit their columns{(clipped.Count > 0 ? ": too wide " + string.Join(", ", clipped) : "")}");
        Console.WriteLine($"  bands: {string.Join(" ", graph.Bands.Select(b => b.Frequency + "/" + b.Gain))}; range ±{graph.RangeDb}");

        // ---------------------------------------------------------- 2. scale labels
        Console.WriteLine("2. scale labels:");
        var labels = scaleGrid.Children.OfType<TextBlock>().ToList();
        double[] lines = [view.InsetY, view.ActualHeight / 2, view.ActualHeight - view.InsetY];
        for (var i = 0; i < 3; i++)
        {
            var centre = labels[i].TranslatePoint(new Point(0, labels[i].ActualHeight / 2), view).Y;
            Check(Math.Abs(centre - lines[i]) <= 1.5, $"'{labels[i].Text}' centre {centre:0.##} vs line {lines[i]:0.##} (off {centre - lines[i]:+0.##;-0.##;0})");
        }

        // ---------------------------------------------------------- 3. colour
        Console.WriteLine("3. curve colour:");
        var full = Render("full", w);
        var panelRect = Px(pBox, 1);
        {
            // The labels' ink against the lines as drawn (whole pixel + 0.5).
            var bg = (Color)app.FindResource("color.panel.bg.value");
            var graphTop = Box(view).Top;
            for (var i = 0; i < 3; i++)
            {
                var ink = full.Ink(Px(Box(labels[i])), bg);
                var inkCentre = (ink.Y + ink.Height / 2.0) / scale - graphTop;
                var drawn = Math.Round(lines[i]) + 0.5;   // EqGraphView's Crisp, in its own units
                Check(Math.Abs(inkCentre - drawn) <= 1.5, $"2. '{labels[i].Text}' ink centre {inkCentre:0.##} vs drawn line {drawn:0.##} (off {inkCentre - drawn:+0.##;-0.##;0}; ink {ink.Height} px tall)");
            }
        }
        Console.WriteLine($"  panel pixel rect {panelRect.X},{panelRect.Y} {panelRect.Width}x{panelRect.Height}");
        if (Arg(args, "--compareto") is { } other)
        {
            var reference = LoadShot(other);
            var d = full.Diff(reference, panelRect);
            Check(d == 0, $"11. panel region against {Path.GetFileName(other)}: {d} pixels differ");
        }

        Color DotPixel(EqShot shot, int i)
        {
            var at = view.TranslatePoint(DotAt(view, graph, graph.Dots[i]), main);
            return shot.At((int)Math.Floor(at.X * scale), (int)Math.Floor(at.Y * scale));
        }
        bool Colour(string state, EqShot shot, Color? cover)
        {
            var ok = true;
            var themeBrush = (SolidColorBrush)app.FindResource("color.accent.playback");
            var curve = view.CurveBrush as SolidColorBrush;
            var seekFill = seek.Foreground as SolidColorBrush;
            Color expected;
            if (cover is { } c)
            {
                var dark = ThemeService.Current == AppTheme.Dark;
                expected = FromArgb(CoverColor.Accent(Argb(c), Argb((Color)app.FindResource("color.accent.playback.value")), dark,
                    Tok("playbackBar.coverAccent.lightnessRange"), Tok("playbackBar.coverAccent.greyOnDark")));
                expected.A = themeBrush.Color.A;
                var same = curve is not null && seekFill is not null && curve.Color.R == seekFill.Color.R && curve.Color.G == seekFill.Color.G && curve.Color.B == seekFill.Color.B;
                ok &= curve?.Color == expected && same;
                Check(curve?.Color == expected && same, $"[{state}] curve brush {Hex(curve?.Color)} = fitted {Hex(expected)} = seekbar fill {Hex(seekFill?.Color)}");
            }
            else
            {
                expected = themeBrush.Color;
                ok &= ReferenceEquals(curve, themeBrush);
                Check(ReferenceEquals(curve, themeBrush), $"[{state}] curve brush is the theme's accent.playback {Hex(themeBrush.Color)} (seekbar {Hex(seekFill?.Color)})");
            }
            if (shot is not null)
            {
                var wrong = Enumerable.Range(0, graph.Dots.Count).Select(i => DotPixel(shot, i))
                    .Where(p => p.R != expected.R || p.G != expected.G || p.B != expected.B).Select(p => Hex(p)).ToList();
                ok &= wrong.Count == 0;
                Check(wrong.Count == 0, $"[{state}] 13 dot centres drawn {Hex(Color.FromRgb(expected.R, expected.G, expected.B))}{(wrong.Count > 0 ? "; wrong: " + string.Join(" ", wrong) : "")}");
            }
            return ok;
        }

        Colour("no cover", full, null);
        var red = CoverAccent.MainColorOf(CoverDeepRed());
        vm.NowPlayingCoverColor = red;
        Pump();
        var coverShot = Render("cover", w);
        Colour("red cover", coverShot, red);
        var startTheme = ThemeService.Current;
        var otherTheme = startTheme == AppTheme.Dark ? AppTheme.Light : AppTheme.Dark;
        ThemeService.Switch(otherTheme);
        main.UpdateLayout();
        Pump();
        Colour($"red cover, switched to {otherTheme}", null!, red);
        ThemeService.Switch(startTheme);
        main.UpdateLayout();
        Pump();
        var back = Render("cover-back", w);
        Colour($"red cover, back to {startTheme}", back, red);
        Check(coverShot.Diff(back, panelRect) == 0, $"panel region after the round trip = before ({coverShot.Diff(back, panelRect)} pixels differ)");
        vm.NowPlayingCoverColor = null;
        Pump();
        Colour("cover gone", null!, null);

        // ---------------------------------------------------------- 9. accessible name
        Console.WriteLine("9. accessible name:");
        var peer = UIElementAutomationPeer.CreatePeerForElement(panel);
        Check(AutomationProperties.GetName(panel) == graph.AutomationText && peer.GetName() == graph.AutomationText,
            $"peer {peer.GetAutomationControlType()} '{peer.GetName()}' (keyboard focusable {peer.IsKeyboardFocusable()})");

        // ---------------------------------------------------------- 7. Peace
        Console.WriteLine("7. opening in Peace:");
        Check(vm.CanOpenEqualizerInPeace == (MainViewModel.PeaceExecutable is not null) && panel.Cursor == (vm.CanOpenEqualizerInPeace ? Cursors.Hand : null),
            $"Peace folder preset: CanOpenEqualizerInPeace {vm.CanOpenEqualizerInPeace}, cursor {panel.Cursor?.ToString() ?? "null"}");
        SetEq(preset, AppSettings.EqualizerInOwnFolder);
        Check(!vm.CanOpenEqualizerInPeace && panel.Cursor is null, $"own folder preset: CanOpenEqualizerInPeace {vm.CanOpenEqualizerInPeace}, cursor {panel.Cursor?.ToString() ?? "null"}");
        SetEq(preset, AppSettings.EqualizerInPeace);

        // ---------------------------------------------------------- 10. clipping (any dpi)
        Console.WriteLine("10. fit:");
        var gains = FindAll<TextBlock>(bands).Where(t => graph.Bands.Any(b => b.Gain == t.Text) && !graph.Bands.Any(b => b.Frequency == t.Text)).ToList();
        var bandsBox = Box(bands);
        var short_ = FindAll<TextBlock>(panel).Where(t => t.IsVisible && (t.RenderSize.Height + 0.01 < t.DesiredSize.Height || LayoutInformation.GetLayoutClip(t) is not null)).Select(t => t.Text).ToList();
        Check(short_.Count == 0, $"no text in the panel squeezed or clipped{(short_.Count > 0 ? ": " + string.Join(", ", short_) : "")}");
        var clips = FindAll<FrameworkElement>(panel).Prepend(panel).Concat(new FrameworkElement[] { headerGrid })
            .Where(e => e.IsVisible && LayoutInformation.GetLayoutClip(e) is not null)
            .Select(e => $"{e.GetType().Name}#{e.Name} {LayoutInformation.GetLayoutClip(e).Bounds}").ToList();
        Check(clips.Count == 0, $"no layout clip on the panel, anything in it or the header grid{(clips.Count > 0 ? ": " + string.Join("; ", clips) : "")}");
        // Within a device pixel: at 125% layout rounding moves boxes by up to 0.8 of a unit.
        Check(bandsBox.Bottom <= Box(panel).Bottom + 1 / scale + 0.01 && Box(panel).Bottom <= artBox.Bottom + 0.01 && Box(panel).Bottom <= header.Bottom,
            $"gain row bottom {bandsBox.Bottom:0.##} <= panel bottom {Box(panel).Bottom:0.##} <= art bottom {artBox.Bottom:0.##} <= header bottom {header.Bottom:0.##}");
        var ground = (Color)app.FindResource("color.panel.bg.value");
        var inkRows = full.Ink(Px(bandsBox), ground);
        Check(!inkRows.IsEmpty && inkRows.Y + inkRows.Height <= (int)Math.Ceiling(bandsBox.Bottom * scale) && inkRows.Y + inkRows.Height < (int)Math.Floor(header.Bottom * scale),
            $"band labels' ink {inkRows.X},{inkRows.Y} {inkRows.Width}x{inkRows.Height} px ends at {inkRows.Y + inkRows.Height}, inside the labels' box (bottom {bandsBox.Bottom * scale:0.#}) and the header (bottom {header.Bottom * scale:0.#})");
        Check(gains.Count == 13, $"gain texts found: {gains.Count}");

        // ---------------------------------------------------------- 8. an edit in the file
        Console.WriteLine("8. an edit saved to the file:");
        var scratch = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(outPath))!, "eqedit-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(scratch);
        var copy = Path.Combine(scratch, "IER-M9 ChatGPT.peace");
        File.Copy(fixture, copy);
        var fromCopy = PeacePresetReader.Read(copy);
        SetEq(fromCopy, AppSettings.EqualizerInPeace);
        var before = vm.EqualizerGraph!;
        var beforeShot = Render("edit-before", w);
        var text = File.ReadAllText(copy);
        File.WriteAllText(copy, Regex.Replace(text, @"(?m)^Gain1=.*$", "Gain1=5"));
        vm.TakeUpEqualizerEditFromFile();
        main.UpdateLayout();
        Pump();
        var after = vm.EqualizerGraph!;
        Check(!ReferenceEquals(after, before) && after.Bands[0].GainDb == 5 && after.Bands[0].Gain == "+5.0" && ReferenceEquals(InUse(), after.Preset),
            $"new graph {(ReferenceEquals(after, before) ? "no" : "yes")}, 10 Hz gain {before.Bands[0].Gain} -> {after.Bands[0].Gain}");
        var editedShot = Render("edited", w);
        var graphRect = Px(Box(view));
        Check(beforeShot.Diff(editedShot, graphRect) > 0, $"graph area redrawn: {beforeShot.Diff(editedShot, graphRect)} pixels differ");
        var edited = InUse();
        File.WriteAllText(copy, Regex.Replace(Regex.Replace(text, @"(?m)^Gain(\d+)=.*$", "Gain$1=0"), @"(?m)^PreAmp=.*\r?\n", ""));
        Console.WriteLine($"  flat file reads as flat: {PeacePresetReader.Read(copy).IsFlat}");
        vm.TakeUpEqualizerEditFromFile();
        Pump();
        Check(ReferenceEquals(InUse(), edited) && ReferenceEquals(vm.EqualizerGraph, after), "a flat file: the previous preset stays");
        var engine = (AudioFool.Core.Playback.AudioEngine)typeof(MainViewModel).GetField("_engine", flags)!.GetValue(vm)!;
        engine.Equalizer = null;
        try { Directory.Delete(scratch, true); } catch (IOException) { }
        SetEq(preset, AppSettings.EqualizerInPeace);
        graph = vm.EqualizerGraph!;

        // ---------------------------------------------------------- 4. narrow
        Console.WriteLine("4. narrow windows:");
        (double Header, bool Shown, bool Compact, double Width) At(double width)
        {
            main.Width = width;
            main.UpdateLayout();
            Pump();
            return (headerGrid.ActualWidth, panel.Visibility == Visibility.Visible, view.Compact, panel.Visibility == Visibility.Visible ? panel.ActualWidth : 0);
        }
        string Mode((double Header, bool Shown, bool Compact, double Width) s) => !s.Shown ? "hidden" : s.Compact ? "compact" : "full";

        var minWidth = Math.Max(main.MinWidth, 400);
        Console.WriteLine($"  window MinWidth {main.MinWidth}");
        var steps = new List<(double Window, string Mode, double Header, double Width)>();
        string? lastMode = null;
        double lastWindow = w;
        var transitions = new List<string>();
        for (var width = Math.Round(w); width >= minWidth; width -= 10)
        {
            var s = At(width);
            var mode = Mode(s);
            if (lastMode is not null && mode != lastMode)
            {
                // Refine to the pixel: the narrowest window still in lastMode.
                double edge = lastWindow;
                for (var x = lastWindow - 1; x > width; x--)
                {
                    var t = At(x);
                    if (Mode(t) != lastMode) break;
                    edge = x;
                }
                var e = At(edge);
                var f = At(edge - 1);
                transitions.Add($"{lastMode} down to window {edge} (header {e.Header:0.##}, panel {e.Width:0.##}); {Mode(f)} from {edge - 1} (header {f.Header:0.##}, panel {f.Width:0.##})");
            }
            steps.Add((width, mode, s.Header, s.Width));
            lastMode = mode;
            lastWindow = width;
        }
        foreach (var t in transitions) Console.WriteLine($"  {t}");
        var modes = steps.Select(s => s.Mode).Distinct().ToList();
        Check(modes.Count == transitions.Count + 1 && modes.SequenceEqual(new[] { "full", "compact", "hidden" }.Take(modes.Count)),
            $"modes from wide to narrow, window {w} to {steps[^1].Window} (header {steps[0].Header:0.##} to {steps[^1].Header:0.##}): {string.Join(" -> ", modes)}");
        if (!modes.Contains("hidden"))
            Console.WriteLine($"  hidden: not reached at the default panel widths (window MinWidth {main.MinWidth})");

        // Narrower headers than the window allows: the albums panel dragged wider, as
        // its splitter does, at the window's narrowest.
        var panelsGrid = (Grid)((FrameworkElement)main.FindName("ArtistsPanel")).Parent;
        var albumsColumn = panelsGrid.ColumnDefinitions[2];
        var songsColumn = panelsGrid.ColumnDefinitions[4];
        var albumsWidth = albumsColumn.Width;
        var narrowest = steps[^1].Window;
        var dragged = new List<(double Albums, string Mode, double Header, double Width)>();
        for (var extra = 0; extra <= 900; extra += 10)
        {
            albumsColumn.Width = new GridLength(albumsWidth.Value + extra);
            var s = At(narrowest);
            dragged.Add((albumsColumn.ActualWidth, Mode(s), s.Header, s.Width));
            if (dragged.Count > 1 && Math.Abs(dragged[^1].Header - dragged[^2].Header) < 0.01)
                break;
        }
        var lastShown = dragged.LastOrDefault(d => d.Mode != "hidden");
        var firstHidden = dragged.FirstOrDefault(d => d.Mode == "hidden");
        Console.WriteLine($"  albums panel dragged wider at window {narrowest}: songs column MinWidth {songsColumn.MinWidth:0.##}; header {dragged[0].Header:0.##} -> {dragged[^1].Header:0.##}; "
            + $"last shown: albums {lastShown.Albums:0.##}, header {lastShown.Header:0.##}, panel {lastShown.Width:0.##} {lastShown.Mode}; "
            + (firstHidden.Mode is null ? "never hidden" : $"hidden from albums {firstHidden.Albums:0.##}, header {firstHidden.Header:0.##}"));
        Check(dragged.All(d => d.Mode == "hidden" || d.Width >= Tok("eqPanel.compactMinWidth")), "every dragged width follows the rule");
        albumsColumn.Width = albumsWidth;
        At(w);

        // The hidden mode, which the songs column's minimum keeps out of reach in the
        // app: the header grid given a width by hand, either side of the edge.
        var edgeHeader = 3 * Tok("albumHeader.paddingX") + Tok("albumHeader.artSize") + Tok("albumHeader.artGap") + Tok("eqPanel.titleRoom") + Tok("eqPanel.compactMinWidth");
        foreach (var forced in new[] { edgeHeader, edgeHeader - 1 })
        {
            headerGrid.Width = forced;
            main.UpdateLayout();
            Pump();
            var shownNow = panel.Visibility == Visibility.Visible;
            Check(shownNow == (forced >= edgeHeader), $"header forced to {forced}: panel {(shownNow ? $"shown, {panel.ActualWidth:0.##} wide, compact {view.Compact}" : "hidden")}");
            if (!shownNow)
                Render("hidden", w);
        }
        headerGrid.Width = double.NaN;
        main.UpdateLayout();
        Pump();

        // Within each mode: header width against the rule.
        var ruleBreaks = new List<string>();
        foreach (var s in steps)
        {
            var available = s.Header - 3 * Tok("albumHeader.paddingX") - Tok("albumHeader.artSize") - Tok("albumHeader.artGap") - Tok("eqPanel.titleRoom");
            var want = Math.Min(Tok("eqPanel.width"), available);
            var expectMode = want < Tok("eqPanel.compactMinWidth") ? "hidden"
                : (want - Tok("eqPanel.axisWidth") - Tok("eqPanel.axisGap")) / 13 < Tok("eqPanel.minColumnWidth") ? "compact" : "full";
            if (expectMode != s.Mode || (s.Mode != "hidden" && Math.Abs(s.Width - want) > 0.01))
                ruleBreaks.Add($"{s.Window}: {s.Mode} {s.Width:0.##} vs {expectMode} {want:0.##}");
        }
        Check(ruleBreaks.Count == 0, $"every step follows the rule ({steps.Count} widths){(ruleBreaks.Count > 0 ? ": " + string.Join("; ", ruleBreaks.Take(5)) : "")}");

        // A compact render in the middle of its range, and a hidden one.
        var compactWindows = steps.Where(s => s.Mode == "compact").Select(s => s.Window).ToList();
        if (compactWindows.Count > 0)
        {
            var cw = compactWindows[compactWindows.Count / 2];
            var cs = At(cw);
            Console.WriteLine($"  compact render at window {cw}: header {cs.Header:0.##}, panel {cs.Width:0.##}");
            Check(cs.Compact && freqs.Visibility == Visibility.Visible && bands.Visibility == Visibility.Collapsed
                  && Math.Abs(view.ActualHeight - Tok("eqPanel.compactGraphHeight")) < 0.01,
                $"compact: frequencies {freqs.Visibility}, band numbers {bands.Visibility}, graph height {view.ActualHeight:0.##}");
            var fb = Box(freqs);
            var labelBoxes = freqs.Children.OfType<TextBlock>().Select(t => (t.Text, Box: Box(t))).ToList();
            Check(labelBoxes.All(l => l.Box.Left >= fb.Left - 0.01 && l.Box.Right <= fb.Right + 0.01) && labelBoxes.Count == 3,
                $"frequency labels inside the panel: {string.Join(", ", labelBoxes.Select(l => $"{l.Text} {l.Box.Left - fb.Left:0.#}..{l.Box.Right - fb.Left:0.#}"))} of {fb.Width:0.#}");
            var cp = Box(panel);
            var ct = Box(titleColumn);
            var ca = Box(art);
            Check(cp.Bottom <= ca.Bottom + 0.01 && ct.Right <= cp.Left + 0.01 && Math.Abs(Box(headerGrid).Height - offHeader.Height) < 0.01,
                $"compact fit: panel bottom {cp.Bottom:0.##} <= art bottom {ca.Bottom:0.##}; title right {ct.Right:0.##} <= panel left {cp.Left:0.##}; header height {Box(headerGrid).Height:0.##}");
            Colour("compact", null!, null);
            Render("compact", cw);
        }
        var hiddenWindow = steps.Where(s => s.Mode == "hidden").Select(s => s.Window).FirstOrDefault();
        if (hiddenWindow > 0)
        {
            var hs = At(hiddenWindow);
            Check(!hs.Shown, $"hidden at window {hiddenWindow}: header {hs.Header:0.##}");
            Render("hidden", hiddenWindow);
        }
        At(w);
        main.Width = w;
        main.UpdateLayout();
        Pump();

        // ---------------------------------------------------------- 6. playlist header
        Console.WriteLine("6. playlist header:");
        vm.IsPlaylistMode = true;
        Pump();
        if (vm.SelectedPlaylist is null && vm.Playlists.Count > 0)
            vm.SelectedPlaylist = vm.Playlists[0];
        main.UpdateLayout();
        Pump();
        var playlistArt = (FrameworkElement)main.FindName("PlaylistArt");
        var pl = Box(panel);
        Console.WriteLine($"  playlist '{vm.PlaylistHeaderTitle}', ShowsPlaylistHeader {vm.ShowsPlaylistHeader}, album header {vm.ShowsAlbumHeader}");
        Check(vm.ShowsPlaylistHeader && panel.Visibility == Visibility.Visible && !view.Compact && Math.Abs(panel.ActualWidth - Tok("eqPanel.width")) < 0.01
              && pl.Bottom <= Box(playlistArt).Bottom + 0.01 && Box((FrameworkElement)main.FindName("PlaylistHeaderTitle")).Right <= pl.Left + 0.01,
            $"playlist: panel {panel.Visibility} {R(pl)}, art bottom {Box(playlistArt).Bottom:0.##}");
        Render("playlist", w);
        vm.IsPlaylistMode = false;
        main.UpdateLayout();
        Pump();
        Check(vm.ShowsAlbumHeader && panel.Visibility == Visibility.Visible, $"back on the album: panel {panel.Visibility}");

        // ---------------------------------------------------------- 5. off again
        SetEq(null, null);
        Check(panel.Visibility == Visibility.Collapsed, $"5. EQ off again: panel {panel.Visibility}");

        Console.WriteLine(allOk ? "ALL OK" : "SOMETHING WRONG");
        return allOk ? 0 : 1;
    }

    /// <summary>Where EqGraphView draws <paramref name="p"/>, in its own coordinates (its OnRender's arithmetic).</summary>
    private static Point DotAt(EqGraphView view, EqGraph graph, EqGraphPoint p)
    {
        var top = view.InsetY;
        var bottom = view.ActualHeight - view.InsetY;
        var zero = (top + bottom) / 2;
        var perDb = (bottom - top) / 2 / graph.RangeDb;
        var across = view.ActualWidth - 2 * view.InsetX;
        return new Point(view.InsetX + p.X * across, zero - p.Db * perDb);
    }
}
