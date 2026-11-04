using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;

namespace AudioFool.Formatting;

/// <summary>
/// Text with its web addresses made into links that open in the browser:
/// <c>formatting:LinkedText.Text="{Binding Body}"</c> on a <see cref="TextBlock"/>.
/// Genius's write-ups, answers and comments come as plain text, and an answer
/// is sometimes only a link ("Is there a live performance?" and a YouTube
/// address). The links take the window's <see cref="Hyperlink"/> style
/// (<c>theme.link</c>). Line breaks in the text are kept.
/// </summary>
public static partial class LinkedText
{
    public static readonly DependencyProperty TextProperty =
        DependencyProperty.RegisterAttached(
            "Text",
            typeof(string),
            typeof(LinkedText),
            new PropertyMetadata(null, OnTextChanged));

    public static void SetText(DependencyObject target, string? value) => target.SetValue(TextProperty, value);

    public static string? GetText(DependencyObject target) => (string?)target.GetValue(TextProperty);

    [GeneratedRegex(@"https?://[^\s<>""]+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Url();

    /// <summary>The text cut into plain parts and addresses, in order; an address's trailing full stop or bracket stays text.</summary>
    public static IEnumerable<(string Text, bool IsLink)> Split(string text)
    {
        var at = 0;
        foreach (Match m in Url().Matches(text))
        {
            var link = m.Value.TrimEnd('.', ',', ';', ':', '!', '?', ')', ']', '”', '’', '\'');
            if (link.Length <= "https://".Length)
                continue;

            if (m.Index > at)
                yield return (text[at..m.Index], false);
            yield return (link, true);
            at = m.Index + link.Length;
        }

        if (at < text.Length)
            yield return (text[at..], false);
    }

    private static void OnTextChanged(DependencyObject target, DependencyPropertyChangedEventArgs e)
    {
        if (target is not TextBlock block)
            return;

        block.Inlines.Clear();
        if (e.NewValue is not string text || text.Length == 0)
            return;

        foreach (var (part, isLink) in Split(text))
        {
            if (!isLink || !Uri.TryCreate(part, UriKind.Absolute, out var uri))
            {
                block.Inlines.Add(new Run(part));
                continue;
            }

            var link = new Hyperlink(new Run(part)) { NavigateUri = uri };
            link.RequestNavigate += (_, args) =>
            {
                Open(args.Uri.AbsoluteUri);
                args.Handled = true;
            };
            block.Inlines.Add(link);
        }
    }

    /// <summary>Opens an address in the default browser; a failure is ignored, as a click that did nothing.</summary>
    public static void Open(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
        }
    }
}
