using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using Wpf.Ui.Controls;

namespace AudioFool;

/// <summary>
/// A plain viewer for one album cover, sized to the image's own shape.
/// Closes on Escape, or on another double-click - the same gesture that opened it.
/// </summary>
public partial class ArtWindow : FluentWindow
{
    public ArtWindow(BitmapSource art, string caption, Window owner)
    {
        InitializeComponent();

        // A Mica window in front of an opaque theme reads as a different app.
        WindowBackdropType = ThemeService.Backdrop;

        Owner = owner;
        Title = caption;
        Bar.Title = caption;
        Caption.Text = $"{caption}   ·   {art.PixelWidth} × {art.PixelHeight}";
        ArtImage.Source = art;

        SizeToArt(art, owner);
    }

    /// <summary>
    /// Matches the window to the cover's aspect ratio, so a square cover doesn't
    /// sit in a tall window with dead space above and below. Clamped to the work
    /// area, and positioned over the owner rather than the screen centre.
    /// </summary>
    private void SizeToArt(BitmapSource art, Window owner)
    {
        // Room for the title bar and the caption line underneath.
        const double chrome = 96;
        const double margin = 80;

        var work = SystemParameters.WorkArea;
        var maxWidth = work.Width - margin;
        var maxHeight = work.Height - margin;

        var width = (double)art.PixelWidth;
        var height = art.PixelHeight + chrome;

        // Never blow a small cover up past its own resolution - it would just be
        // a soft, magnified version of itself.
        var scale = Math.Min(1.0, Math.Min(maxWidth / width, maxHeight / height));
        width *= scale;
        height *= scale;

        Width = Math.Max(MinWidth, width);
        Height = Math.Max(MinHeight, height);

        var centreX = owner.Left + (owner.ActualWidth / 2);
        var centreY = owner.Top + (owner.ActualHeight / 2);

        Left = Math.Clamp(centreX - (Width / 2), work.Left, Math.Max(work.Left, work.Right - Width));
        Top = Math.Clamp(centreY - (Height / 2), work.Top, Math.Max(work.Top, work.Bottom - Height));
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (e.Key is Key.Escape)
        {
            Close();
            e.Handled = true;
            return;
        }

        base.OnPreviewKeyDown(e);
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            Close();
            e.Handled = true;
            return;
        }

        base.OnMouseLeftButtonDown(e);
    }
}
