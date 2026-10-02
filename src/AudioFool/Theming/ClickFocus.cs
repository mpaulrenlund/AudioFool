using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;

namespace AudioFool.Theming;

/// <summary>
/// A button that a mouse click doesn't focus (the user's call, 2026-10-02).
/// Once any key has been pressed, WPF draws focus outlines for every focus
/// change, so a click on Play left the teal ring round it. With this set, a
/// click still presses the button but leaves keyboard focus where it was; Tab
/// still stops on the button and shows the ring there.
/// </summary>
public static class ClickFocus
{
    public static readonly DependencyProperty SkipProperty = DependencyProperty.RegisterAttached(
        "Skip", typeof(bool), typeof(ClickFocus), new PropertyMetadata(false, OnSkipChanged));

    public static bool GetSkip(DependencyObject element) => (bool)element.GetValue(SkipProperty);

    public static void SetSkip(DependencyObject element, bool value) => element.SetValue(SkipProperty, value);

    private static void OnSkipChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ButtonBase button)
            return;

        var before = new MouseButtonEventHandler(OnPreviewMouseLeftButtonDown);
        var after = new MouseButtonEventHandler(OnMouseLeftButtonDown);
        button.RemoveHandler(UIElement.PreviewMouseLeftButtonDownEvent, before);
        button.RemoveHandler(UIElement.MouseLeftButtonDownEvent, after);
        if ((bool)e.NewValue)
        {
            button.AddHandler(UIElement.PreviewMouseLeftButtonDownEvent, before);
            // handledEventsToo: ButtonBase's own handler, which calls Focus()
            // and then captures the mouse, marks the press handled.
            button.AddHandler(UIElement.MouseLeftButtonDownEvent, after, handledEventsToo: true);
        }
    }

    // ButtonBase calls Focus() on the press, which does nothing while the
    // button isn't focusable; the press, capture and click carry on as usual.
    private static void OnPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        var button = (ButtonBase)sender;
        if (button.Focusable && !button.IsKeyboardFocused)
        {
            button.SetCurrentValue(UIElement.FocusableProperty, false);
            button.SetValue(SuppressedProperty, true);
        }
    }

    private static void OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        var button = (ButtonBase)sender;
        if (!(bool)button.GetValue(SuppressedProperty))
            return;

        button.ClearValue(SuppressedProperty);
        button.SetCurrentValue(UIElement.FocusableProperty, true);
    }

    // Set only between the two handlers, so a button that is unfocusable for
    // some other reason is never made focusable here.
    private static readonly DependencyProperty SuppressedProperty = DependencyProperty.RegisterAttached(
        "Suppressed", typeof(bool), typeof(ClickFocus), new PropertyMetadata(false));
}
