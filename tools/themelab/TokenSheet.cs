using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AudioFool.Theming;

namespace ThemeLab;

/// <summary>
/// --window tokens: checks the central theme from inside the real app and draws a
/// specimen sheet of it.
/// <list type="number">
/// <item>Every token resource is looked up from the real MainWindow, which is how a
/// screen will reach it, and must resolve to the token's own value - not missing,
/// and not shadowed by an older key of the same name.</item>
/// <item>A XAML snippet written the way the screens will be written
/// (<c>{DynamicResource}</c>, <c>{theme:Token}</c>, <c>{theme:Thickness}</c>, in
/// elements, a style and a template) is parsed and its values printed.</item>
/// <item>Which font files WPF actually resolves for the font tokens, and whether a
/// weight is real or synthesised.</item>
/// <item>A PNG of every colour, text style, radius and shadow, drawn from the tokens.</item>
/// </list>
/// Nothing is shown on the desktop; the sheet is laid out without a window.
/// </summary>
internal static class TokenSheet
{
    public static int Run(Window main, string outPath, double scale)
    {
        var failures = 0;
        var tokens = TokenResources.Current ?? throw new InvalidOperationException("ThemeService.Apply() has not loaded the tokens.");

        // ------------------------------------------------ 1. lookups from MainWindow
        var dict = Application.Current.Resources.MergedDictionaries.Single(d => d.Contains("color.window.bg"));
        var keys = dict.Keys.Cast<object>().Select(k => (string)k).Order(StringComparer.Ordinal).ToList();
        var missing = keys.Where(k => main.TryFindResource(k) is null).ToList();
        var shadowed = keys.Where(k => main.TryFindResource(k) is { } v && !ReferenceEquals(v, dict[k]) && !Equals(v, dict[k])).ToList();
        Console.WriteLine($"token resources: {keys.Count}  resolved from MainWindow: {keys.Count - missing.Count}  "
            + $"missing: {missing.Count}  shadowed: {shadowed.Count}");
        foreach (var k in missing.Concat(shadowed))
            Console.WriteLine($"  PROBLEM {k}");
        failures += missing.Count + shadowed.Count;

        var byKind = keys.GroupBy(k => dict[k].GetType().Name).OrderBy(g => g.Key);
        Console.WriteLine("  by type: " + string.Join(", ", byKind.Select(g => $"{g.Key} {g.Count()}")));

        // ------------------------------------------------ 2. the way screens use them
        const string snippet = """
            <StackPanel xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                        xmlns:theme="clr-namespace:AudioFool.Theming;assembly=AudioFool">
                <Border x:Name="Panel"
                        Width="{theme:Token layout.artistsPanelOuterWidth}"
                        Height="{theme:Token artists.rowHeight}"
                        Background="{DynamicResource color.panel.bg}"
                        BorderBrush="{DynamicResource color.panel.border}"
                        BorderThickness="{theme:Token layout.panelBorder}"
                        CornerRadius="{theme:Token radius.surface}"
                        Padding="{theme:Thickness X=artists.rowPaddingX}"
                        Effect="{DynamicResource shadow.headerArt}">
                    <TextBlock x:Name="Label" Text="Metal Gear"
                               Style="{DynamicResource type.artistName}"
                               TextAlignment="{theme:Token songTable.columns.time.align}" />
                </Border>
                <Border x:Name="Styled">
                    <Border.Style>
                        <Style TargetType="Border">
                            <Setter Property="Margin" Value="{theme:Thickness X=songTable.listPaddingX, Y=songTable.listPaddingY}" />
                            <Setter Property="CornerRadius" Value="{theme:Token radius.row}" />
                            <Setter Property="Background" Value="{DynamicResource color.state.selectionBg}" />
                        </Style>
                    </Border.Style>
                </Border>
                <ContentControl x:Name="Templated">
                    <ContentControl.Template>
                        <ControlTemplate TargetType="ContentControl">
                            <Border x:Name="Chip" CornerRadius="{theme:Token radius.chip}"
                                    Height="{theme:Token statusBar.chipHeight}"
                                    Background="{DynamicResource color.status.chipBg}" />
                        </ControlTemplate>
                    </ContentControl.Template>
                </ContentControl>
                <Grid x:Name="Columns">
                    <Grid.ColumnDefinitions>
                        <ColumnDefinition Width="{theme:Token songTable.columns.time.width}" />
                    </Grid.ColumnDefinitions>
                </Grid>
                <DataGrid x:Name="Table">
                    <DataGrid.Columns>
                        <DataGridTextColumn Width="{theme:Token songTable.columns.bitrate.width}" />
                    </DataGrid.Columns>
                </DataGrid>
            </StackPanel>
            """;

        var probe = (StackPanel)XamlReader.Parse(snippet);
        probe.Measure(new Size(800, 800));
        probe.Arrange(new Rect(0, 0, 800, 800));
        probe.UpdateLayout();

        var panel = (Border)probe.FindName("Panel");
        var label = (TextBlock)probe.FindName("Label");
        var styled = (Border)probe.FindName("Styled");
        var templated = (ContentControl)probe.FindName("Templated");
        templated.ApplyTemplate();
        var chip = (Border)templated.Template.FindName("Chip", templated);
        var column = ((Grid)probe.FindName("Columns")).ColumnDefinitions[0];
        var tableColumn = ((DataGrid)probe.FindName("Table")).Columns[0];

        void Check(string what, object actual, object expected)
        {
            var ok = Equals(actual, expected);
            if (!ok) failures++;
            Console.WriteLine($"  {(ok ? "ok   " : "WRONG")} {what} = {Show(actual)}{(ok ? "" : $" (expected {Show(expected)})")}");
        }

        Console.WriteLine("XAML usage:");
        Check("Border.Width (Token, double)", panel.Width, 212.0);
        Check("Border.BorderThickness (Token -> Thickness)", panel.BorderThickness, new Thickness(1));
        Check("Border.CornerRadius (Token -> CornerRadius)", panel.CornerRadius, new CornerRadius(4));
        Check("Border.Padding (Thickness X=)", panel.Padding, new Thickness(10, 0, 10, 0));
        Check("Border.Background (DynamicResource brush)", ((SolidColorBrush)panel.Background).Color, Color.FromRgb(0xCA, 0xC7, 0xC3));
        Check("Border.Effect (DynamicResource shadow)", panel.Effect is System.Windows.Media.Effects.DropShadowEffect, true);
        Check("TextBlock style font size", label.FontSize, 13.5);
        Check("TextBlock style colour", ((SolidColorBrush)label.Foreground).Color, Color.FromRgb(0x22, 0x21, 0x1F));
        Check("TextBlock.TextAlignment (Token -> enum)", label.TextAlignment, TextAlignment.Right);
        Check("Style setter Margin (Thickness X=,Y=)", styled.Margin, new Thickness(8, 6, 8, 6));
        Check("Style setter CornerRadius (Token)", styled.CornerRadius, new CornerRadius(3));
        Check("Template CornerRadius (Token)", chip.CornerRadius, new CornerRadius(4));
        Check("Template Height (Token)", chip.Height, 26.0);
        Check("ColumnDefinition.Width (Token -> GridLength)", column.Width, new GridLength(44));
        Check("DataGridColumn.Width (Token -> DataGridLength)", tableColumn.Width.Value, 68.0);

        try
        {
            XamlReader.Parse("""<Border xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:theme="clr-namespace:AudioFool.Theming;assembly=AudioFool" Width="{theme:Token layout.noSuchToken}" />""");
            Console.WriteLine("  WRONG a misspelt token loaded without an error");
            failures++;
        }
        catch (XamlParseException e)
        {
            Console.WriteLine($"  ok    a misspelt token fails at load: {e.InnerException?.Message}");
        }

        // ---------------------------------------------------------------- 3. fonts
        Console.WriteLine("fonts:");
        var installed = Fonts.SystemFontFamilies.Select(f => f.Source).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var name in tokens.FontFamily)
            Console.WriteLine($"  \"{name}\" in WPF's system font list: {installed.Contains(name)}");
        var family = (FontFamily)dict["font.family"];
        foreach (var weight in tokens.TextStyles.Select(s => s.Weight).Distinct().Order())
        {
            var typeface = new Typeface(family, FontStyles.Normal, FontWeight.FromOpenTypeWeight(weight), FontStretches.Normal);
            var found = typeface.TryGetGlyphTypeface(out var glyphs);
            Console.WriteLine($"  font.family weight {weight}: "
                + (found
                    ? $"{Path.GetFileName(glyphs.FontUri.LocalPath)} face \"{glyphs.FamilyNames.Values.FirstOrDefault()} {glyphs.FaceNames.Values.FirstOrDefault()}\" "
                      + $"weight {glyphs.Weight.ToOpenTypeWeight()} {(typeface.IsBoldSimulated ? "SIMULATED bold" : "real")}"
                    : "no glyph typeface"));
            // What WPF draws with is decided per character through the family's
            // fallback chain; measure a string to see which face it lands on.
            var text = new FormattedText("Metal Gear 0123", CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                typeface, 100, Brushes.Black, 1);
            var single = new FormattedText("Metal Gear 0123", CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                new Typeface(new FontFamily(tokens.FontFamily[^1]), FontStyles.Normal, FontWeight.FromOpenTypeWeight(weight), FontStretches.Normal), 100, Brushes.Black, 1);
            Console.WriteLine($"    width at 100 px: {text.WidthIncludingTrailingWhitespace:0.0} vs \"{tokens.FontFamily[^1]}\" alone {single.WidthIncludingTrailingWhitespace:0.0}");
        }

        // ----------------------------------------------------------- 4. specimen
        var sheet = Specimen(tokens, dict);
        const double width = 1400;
        sheet.Measure(new Size(width, double.PositiveInfinity));
        sheet.Arrange(new Rect(0, 0, width, sheet.DesiredSize.Height));
        sheet.UpdateLayout();
        Render(sheet, outPath, width, Math.Ceiling(sheet.DesiredSize.Height), scale);
        Console.WriteLine($"specimen: {outPath} ({width} x {Math.Ceiling(sheet.DesiredSize.Height)})");

        Console.WriteLine(failures == 0 ? "RESULT: all checks passed" : $"RESULT: {failures} problem(s)");
        return failures == 0 ? 0 : 1;
    }

    private static string Show(object o) => o switch
    {
        double d => d.ToString(CultureInfo.InvariantCulture),
        _ => o.ToString() ?? "null",
    };

    private static FrameworkElement Specimen(AudioFool.Core.Theme.ThemeTokens tokens, ResourceDictionary d)
    {
        T R<T>(string key) => (T)d[key];
        TextBlock Text(string text, string style, double bottom = 0) =>
            new() { Text = text, Style = R<Style>(style), Margin = new Thickness(0, 0, 0, bottom) };
        TextBlock Heading(string text) =>
            new() { Text = text.ToUpperInvariant(), Style = R<Style>("type.panelHeader"), Margin = new Thickness(0, 24, 0, 10) };

        var root = new StackPanel { Margin = new Thickness(R<double>("layout.windowMarginX") * 2) };
        root.Children.Add(Text("Theme tokens", "type.headerTitle", 4));
        root.Children.Add(Text("Drawn from design/theme-tokens.json through the app's own resources. Nothing here is a screen.", "type.headerDetails"));

        // Colours, grouped as in the file. Translucent ones sit on the panel grey
        // they are laid over, with a white/black split behind to show the alpha.
        root.Children.Add(Heading("Colours"));
        foreach (var group in tokens.Colors.Keys.GroupBy(k => k.Split('.')[1]))
        {
            var row = new WrapPanel { Margin = new Thickness(0, 0, 0, 8) };
            row.Children.Add(new TextBlock { Text = group.Key, Width = 80, Style = R<Style>("type.albumMeta"), VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 8, 0, 0) });
            foreach (var key in group)
            {
                var c = tokens.Colors[key];
                var swatch = new Grid { Width = 44, Height = 32 };
                if (c.A < 255)
                {
                    swatch.Children.Add(new Border { Background = R<Brush>("color.panel.bg") });
                    swatch.Children.Add(new Border { Background = Brushes.White, Width = 22, HorizontalAlignment = HorizontalAlignment.Left, Opacity = 0.5 });
                }
                swatch.Children.Add(new Border
                {
                    Background = R<Brush>(key),
                    BorderBrush = R<Brush>("color.panel.border"),
                    BorderThickness = new Thickness(1),
                    CornerRadius = R<CornerRadius>("radius.row"),
                });
                var cell = new StackPanel { Orientation = Orientation.Horizontal, Width = 210, Margin = new Thickness(0, 0, 8, 6) };
                cell.Children.Add(swatch);
                var words = new StackPanel { Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
                words.Children.Add(new TextBlock { Text = key["color.".Length..], Style = R<Style>("type.tableCell"), Foreground = R<Brush>("color.text.primary") });
                words.Children.Add(new TextBlock { Text = c.A == 255 ? $"#{c.R:X2}{c.G:X2}{c.B:X2}" : $"#{c.R:X2}{c.G:X2}{c.B:X2} at {c.Alpha:0.###}", Style = R<Style>("type.albumMeta") });
                cell.Children.Add(words);
                row.Children.Add(cell);
            }
            root.Children.Add(row);
        }

        // Every text style, named with its size and weight, on the panel grey.
        root.Children.Add(Heading("Type"));
        var type = new Border
        {
            Background = R<Brush>("color.panel.bg"),
            BorderBrush = R<Brush>("color.panel.border"),
            BorderThickness = new Thickness(R<double>("layout.panelBorder")),
            CornerRadius = R<CornerRadius>("radius.surface"),
            Padding = new Thickness(16, 10, 16, 10),
        };
        var typeRows = new StackPanel();
        foreach (var s in tokens.TextStyles)
        {
            var row = new DockPanel { Margin = new Thickness(0, 4, 0, 4) };
            var tag = new TextBlock
            {
                Text = $"{s.Name}  {s.Size.ToString(CultureInfo.InvariantCulture)}/{s.Weight}"
                    + (s.LineHeight is { } lh ? $"  lh {lh.ToString(CultureInfo.InvariantCulture)}" : "")
                    + (s.MaxLines is { } ml ? $"  max {ml} lines" : "")
                    + (s.TabularFigures ? "  tabular" : "")
                    + (s.Uppercase ? "  upper" : "")
                    + (s.LetterSpacingEm is { } ls ? $"  +{ls.ToString(CultureInfo.InvariantCulture)} em" : ""),
                Width = 330,
                Style = R<Style>("type.albumMeta"),
                VerticalAlignment = VerticalAlignment.Top,
            };
            DockPanel.SetDock(tag, Dock.Left);
            row.Children.Add(tag);
            var sample = s.Name switch
            {
                "albumTitle" => "Metal Gear Solid 4: Guns of the Patriots Original Soundtrack (Limited Edition)",
                "trackNumber" or "timeLabel" => "1 2 10 21 · 1:02 2:43 11:11",
                "tableCell" => "320 kbps · 44.1 kHz · 1:11 · 0:00",
                _ => s.Uppercase ? "ARTISTS" : "Metal Gear Solid Main Theme",
            };
            var text = new TextBlock { Text = sample, Style = R<Style>(s.Key), HorizontalAlignment = HorizontalAlignment.Left };
            if (s.MaxLines is not null)
                text.Width = R<double>("layout.albumsPanelOuterWidth") - 90;
            row.Children.Add(text);
            typeRows.Children.Add(row);
        }
        type.Child = typeRows;
        root.Children.Add(type);

        // Radii and the outer shadows, each on its own tile.
        root.Children.Add(Heading("Radii and shadows"));
        var tiles = new WrapPanel();
        foreach (var key in tokens.Numbers.Keys.Where(k => k.StartsWith("radius.", StringComparison.Ordinal)))
            tiles.Children.Add(Tile(key, d, R<double>(key + ".value"), effect: null));
        foreach (var key in tokens.Shadows.Keys.Where(d.Contains))
            tiles.Children.Add(Tile(key, d, 0, (System.Windows.Media.Effects.Effect)d[key]));
        root.Children.Add(tiles);

        root.Children.Add(Text("Inset shadows have no WPF equivalent; they are published as colour + offset + blur for the templates to draw: "
            + string.Join(", ", tokens.Shadows.Where(s => s.Value.Any(l => l.Inset)).Select(s => s.Key["shadow.".Length..])), "type.albumMeta", 0));

        return new Border { Background = R<Brush>("color.window.bg"), Child = root };
    }

    private static FrameworkElement Tile(string key, ResourceDictionary d, double radius, System.Windows.Media.Effects.Effect? effect)
    {
        var box = new Border
        {
            Width = 96,
            Height = 64,
            Background = (Brush)d[effect is null ? "color.panel.bg" : "color.control.face"],
            BorderBrush = (Brush)d["color.art.border"],
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(radius),
            Effect = effect,
        };
        var cell = new StackPanel { Margin = new Thickness(0, 0, 28, 16) };
        cell.Children.Add(box);
        cell.Children.Add(new TextBlock
        {
            Text = key + (effect is null ? $" = {radius}" : ""),
            Style = (Style)d["type.albumMeta"],
            Margin = new Thickness(0, 12, 0, 0),
        });
        return cell;
    }

    private static void Render(FrameworkElement element, string path, double w, double h, double scale)
    {
        var dpi = 96 * scale;
        var rtb = new RenderTargetBitmap((int)Math.Round(w * scale), (int)Math.Round(h * scale), dpi, dpi, PixelFormats.Pbgra32);
        rtb.Render(element);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(rtb));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }
}
