using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;

namespace AudioFool;

/// <summary>
/// The line a drag to reorder draws between two song rows, where the dragged
/// songs will land. Drawn over the table rather than in a row, so no row moves
/// to make room for it. Takes no input: hit tests pass straight through.
/// </summary>
public sealed class DropLineAdorner : Adorner
{
    private readonly Brush _brush;
    private readonly double _thickness;
    private Rect? _line;
    private Rect _clip;

    public DropLineAdorner(UIElement table, Brush brush, double thickness) : base(table)
    {
        _brush = brush;
        _thickness = thickness;
        IsHitTestVisible = false;
    }

    /// <summary>
    /// Centres the line on <paramref name="y"/>, from <paramref name="left"/> to
    /// <paramref name="right"/>, and keeps it inside <paramref name="clip"/> (the
    /// rows area, so it never draws over the column headers).
    /// </summary>
    public void Show(double left, double right, double y, Rect clip)
    {
        var top = Math.Clamp(y - (_thickness / 2), clip.Top, clip.Bottom - _thickness);
        _line = new Rect(left, top, Math.Max(right - left, 0), _thickness);
        _clip = clip;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        if (_line is not { } line)
            return;

        drawingContext.PushClip(new RectangleGeometry(_clip));
        drawingContext.DrawRectangle(_brush, null, line);
        drawingContext.Pop();
    }
}
