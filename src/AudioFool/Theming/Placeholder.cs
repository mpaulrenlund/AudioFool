using System.Windows;

namespace AudioFool.Theming;

/// <summary>
/// <c>theme:Placeholder.Text="Artist"</c>: the hint a dialog text field
/// (<c>theme.dialogTextBox</c>) shows while it is empty. The template draws it;
/// this only carries the text, since a plain TextBox has no property for it.
/// </summary>
public static class Placeholder
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.RegisterAttached(
        "Text", typeof(string), typeof(Placeholder), new FrameworkPropertyMetadata(string.Empty));

    public static string GetText(DependencyObject element) => (string)element.GetValue(TextProperty);

    public static void SetText(DependencyObject element, string value) => element.SetValue(TextProperty, value);
}
