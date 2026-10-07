using System.Windows;
using System.Windows.Controls;

namespace AudioFool;

/// <summary>
/// The heart in the song table's Like column: outlined, or filled in the Square
/// pink while liked. It has no click of its own. <c>MainWindow</c> toggles the
/// like on the press, before the grid sees it, so clicking a heart doesn't
/// select the row (a DataGridCell selects on any press inside it, even one a
/// button has handled), and a double-click on it doesn't play the song.
/// </summary>
public sealed class LikeToggle : Control
{
    public static readonly DependencyProperty IsLikedProperty = DependencyProperty.Register(
        nameof(IsLiked), typeof(bool), typeof(LikeToggle), new PropertyMetadata(false));

    public bool IsLiked
    {
        get => (bool)GetValue(IsLikedProperty);
        set => SetValue(IsLikedProperty, value);
    }
}
