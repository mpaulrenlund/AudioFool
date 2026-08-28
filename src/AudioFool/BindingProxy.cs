using System.Windows;

namespace AudioFool;

// Freezable subclasses inherit DataContext, which makes them usable as a
// relay inside CompositeCollection items that otherwise have no DataContext.
internal sealed class BindingProxy : Freezable
{
    protected override Freezable CreateInstanceCore() => new BindingProxy();

    public static readonly DependencyProperty DataProperty = DependencyProperty.Register(
        nameof(Data), typeof(object), typeof(BindingProxy));

    public object? Data
    {
        get => GetValue(DataProperty);
        set => SetValue(DataProperty, value);
    }
}
