using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;

namespace AudioFool.Formatting;

/// <summary>
/// Letter spacing for short, static labels.
/// <para>
/// WPF has no tracking property - not on <see cref="TextBlock"/>, not on
/// <c>Run</c>, not through <c>Typography</c> - so spacing has to be put into the
/// text itself. Setting <c>LetterSpacing.Spacer</c> to a thin or hair space
/// interleaves that character between every letter, and the unspaced original is
/// kept as the element's automation name so a screen reader still reads
/// "ARTISTS" rather than spelling it out.
/// </para>
/// <para>
/// Because the spacing lives in <see cref="TextBlock.Text"/>, this is only for
/// labels whose text never changes after load: it applies once the element is
/// loaded and again whenever the spacer changes (which is what a theme switch
/// does), but it does not watch <c>Text</c>. The pane headers it exists for are
/// literals in MainWindow.xaml.
/// </para>
/// </summary>
public static class LetterSpacing
{
    public static readonly DependencyProperty SpacerProperty =
        DependencyProperty.RegisterAttached(
            "Spacer",
            typeof(string),
            typeof(LetterSpacing),
            new PropertyMetadata(null, OnSpacerChanged));

    public static void SetSpacer(DependencyObject target, string? value) =>
        target.SetValue(SpacerProperty, value);

    public static string? GetSpacer(DependencyObject target) =>
        (string?)target.GetValue(SpacerProperty);

    /// <summary>
    /// The text as authored, captured before the first rewrite so switching
    /// themes can put it back rather than spacing the spaced version again.
    /// </summary>
    private static readonly DependencyProperty UnspacedProperty =
        DependencyProperty.RegisterAttached(
            "Unspaced", typeof(string), typeof(LetterSpacing));

    private static void OnSpacerChanged(DependencyObject target, DependencyPropertyChangedEventArgs e)
    {
        if (target is not TextBlock label)
            return;

        // The style setter can land before the Text literal has been parsed, in
        // which case there is nothing to space yet. Loaded is the first moment
        // both are certain to be in place.
        if (label.IsLoaded)
        {
            Apply(label);
            return;
        }

        label.Loaded -= OnLoaded;
        label.Loaded += OnLoaded;
    }

    private static void OnLoaded(object sender, RoutedEventArgs e)
    {
        var label = (TextBlock)sender;
        label.Loaded -= OnLoaded;
        Apply(label);
    }

    private static void Apply(TextBlock label)
    {
        // Inline content (a <Run> sequence, a LineBreak) would be flattened by
        // writing Text, so leave anything richer than a plain string alone.
        if (label.Inlines.Count > 1)
            return;

        if (label.GetValue(UnspacedProperty) is not string unspaced)
        {
            unspaced = label.Text;
            label.SetValue(UnspacedProperty, unspaced);
        }

        var spacer = GetSpacer(label);

        if (string.IsNullOrEmpty(spacer) || unspaced.Length < 2)
        {
            label.Text = unspaced;
            return;
        }

        label.Text = string.Join(spacer, unspaced.ToCharArray());

        // Only fill in a name the caller has not given one, so an explicit
        // AutomationProperties.Name in the markup still wins.
        if (AutomationProperties.GetName(label).Length == 0)
            AutomationProperties.SetName(label, unspaced);
    }

    // ------------------------------------------------------------ em spacing

    /// <summary>
    /// Tracking in em, as CSS <c>letter-spacing</c> gives it: <c>Em × FontSize</c>
    /// pixels between letters. The PS1 theme's <c>type.*.letterSpacingEm</c> tokens
    /// go here. A spacer character cannot do this, since its width is the font's
    /// rather than a fraction of the size, so each gap is an empty inline element
    /// of exactly that width.
    /// <para>
    /// Unlike CSS there is no gap after the last letter, so the label's box ends
    /// where its ink does and centring something over it centres on the word.
    /// The same static-label limits as <c>Spacer</c> apply, and text decorations
    /// skip the gaps, so underline such a label some other way.
    /// </para>
    /// </summary>
    public static readonly DependencyProperty EmProperty =
        DependencyProperty.RegisterAttached(
            "Em",
            typeof(double),
            typeof(LetterSpacing),
            new PropertyMetadata(0.0, OnEmChanged));

    public static void SetEm(DependencyObject target, double value) =>
        target.SetValue(EmProperty, value);

    public static double GetEm(DependencyObject target) =>
        (double)target.GetValue(EmProperty);

    private static void OnEmChanged(DependencyObject target, DependencyPropertyChangedEventArgs e)
    {
        if (target is not TextBlock label)
            return;

        if (label.IsLoaded)
        {
            ApplyEm(label);
            return;
        }

        label.Loaded -= OnLoadedEm;
        label.Loaded += OnLoadedEm;
    }

    private static void OnLoadedEm(object sender, RoutedEventArgs e)
    {
        var label = (TextBlock)sender;
        label.Loaded -= OnLoadedEm;
        ApplyEm(label);
    }

    private static void ApplyEm(TextBlock label)
    {
        if (label.GetValue(UnspacedProperty) is not string unspaced)
        {
            // Plain text only, as for Spacer; after the first rewrite the
            // inlines are ours, and the original comes from UnspacedProperty.
            if (label.Inlines.Count > 1)
                return;
            unspaced = label.Text;
            label.SetValue(UnspacedProperty, unspaced);
        }

        var gap = GetEm(label) * label.FontSize;

        label.Inlines.Clear();
        for (var i = 0; i < unspaced.Length; i++)
        {
            label.Inlines.Add(new System.Windows.Documents.Run(unspaced[i].ToString()));
            if (gap > 0 && i < unspaced.Length - 1)
                label.Inlines.Add(new System.Windows.Documents.InlineUIContainer(new FrameworkElement { Width = gap }));
        }

        if (AutomationProperties.GetName(label).Length == 0)
            AutomationProperties.SetName(label, unspaced);
    }
}
