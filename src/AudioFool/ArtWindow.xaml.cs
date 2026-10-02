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

        Owner = owner;
        // The title bar shows the window's Title (theme.dialogTitle).
        Title = caption;
        // The file's own size and format when known: the bitmap is capped at
        // 2,000 px, so its size alone would under-report a larger cover.
        Caption.Text = Services.AlbumArtService.InfoFor(art) is { } info
            ? $"{caption} · {info.SizeText} · {info.FormatText}"
            : $"{caption} · {art.PixelWidth} × {art.PixelHeight}";
        ArtImage.Source = art;

        SizeToArt(art, owner);
    }

    /// <summary>
    /// Matches the window to the cover's aspect ratio, so a square cover doesn't
    /// sit in a tall window with dead space above and below. Only the cover is
    /// scaled; the title bar, caption, margins and frame keep their size.
    /// Clamped to the work area, and positioned over the owner rather than the
    /// screen centre.
    /// </summary>
    private void SizeToArt(BitmapSource art, Window owner)
    {
        var n = Theming.TokenResources.Current!.Numbers;
        var frame = 2 * n["layout.panelBorder"];
        // DesiredSize includes the caption's margin.
        Caption.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var chromeX = (2 * n["layout.windowMarginX"]) + frame;
        var chromeY = n["layout.titleBarHeight"] + Caption.DesiredSize.Height + frame;
        var margin = n["artViewer.screenMargin"];

        var work = SystemParameters.WorkArea;
        var maxWidth = work.Width - margin - chromeX;
        var maxHeight = work.Height - margin - chromeY;

        // Never blow a small cover up past its own resolution - it would just be
        // a soft, magnified version of itself.
        var scale = Math.Min(1.0, Math.Min(maxWidth / art.PixelWidth, maxHeight / art.PixelHeight));

        // Whole pixels, so the centred cover and its frame land on the pixel grid.
        // The caption's measured height isn't whole, so the sums are rounded up.
        Width = Math.Max(MinWidth, Math.Ceiling(Math.Floor(art.PixelWidth * scale) + chromeX));
        Height = Math.Max(MinHeight, Math.Ceiling(Math.Floor(art.PixelHeight * scale) + chromeY));

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
