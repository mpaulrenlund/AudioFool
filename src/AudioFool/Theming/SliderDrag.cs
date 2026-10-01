using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;

namespace AudioFool.Theming;

/// <summary>
/// Press anywhere on a slider's track and drag (spec 6.7: "click or drag
/// anywhere on the track"). WPF's IsMoveToPointEnabled jumps the value to a
/// press on the track but starts no drag, so only a press on the handle itself
/// could be dragged; on the 12 px volume handle that felt like dragging not
/// working at all. After the jump, the press is handed to the handle, which
/// starts its drag from there.
/// </summary>
public static class SliderDrag
{
    public static readonly DependencyProperty FromTrackProperty = DependencyProperty.RegisterAttached(
        "FromTrack", typeof(bool), typeof(SliderDrag), new PropertyMetadata(false, OnFromTrackChanged));

    public static bool GetFromTrack(DependencyObject element) => (bool)element.GetValue(FromTrackProperty);

    public static void SetFromTrack(DependencyObject element, bool value) => element.SetValue(FromTrackProperty, value);

    private static void OnFromTrackChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not Slider slider)
            return;

        // handledEventsToo: the slider marks the press handled once it has
        // moved the value, and that is exactly the press to pass on.
        var handler = new MouseButtonEventHandler(OnPreviewMouseLeftButtonDown);
        slider.RemoveHandler(UIElement.PreviewMouseLeftButtonDownEvent, handler);
        if ((bool)e.NewValue)
            slider.AddHandler(UIElement.PreviewMouseLeftButtonDownEvent, handler, handledEventsToo: true);
    }

    private static void OnPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        var slider = (Slider)sender;
        if (!slider.IsMoveToPointEnabled || slider.Template?.FindName("PART_Track", slider) is not Track { Thumb: { } thumb })
            return;

        // A press on the handle drags already.
        if (thumb.IsMouseOver || thumb.IsDragging)
            return;

        // The handle moves under the pointer first, so the drag starts on it.
        slider.UpdateLayout();
        thumb.RaiseEvent(new MouseButtonEventArgs(e.MouseDevice, e.Timestamp, MouseButton.Left)
        {
            RoutedEvent = UIElement.MouseLeftButtonDownEvent,
            Source = thumb,
        });
    }
}
