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
        Services.OwnerActivation.Attach(this);

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
    /// Clamped to the work area of the owner's monitor, and positioned over the
    /// owner as it is on screen rather than the screen centre.
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

        var work = Services.DialogPlacement.WorkAreaOf(owner);
        var maxWidth = work.Width - margin - chromeX;
        var maxHeight = work.Height - margin - chromeY;

        // Never blow a small cover up past its own resolution - it would just be
        // a soft, magnified version of itself.
        var scale = Math.Min(1.0, Math.Min(maxWidth / art.PixelWidth, maxHeight / art.PixelHeight));

        // Whole pixels, so the centred cover and its frame land on the pixel grid.
        // The caption's measured height isn't whole, so the sums are rounded up.
        Width = Math.Max(MinWidth, Math.Ceiling(Math.Floor(art.PixelWidth * scale) + chromeX));
        Height = Math.Max(MinHeight, Math.Ceiling(Math.Floor(art.PixelHeight * scale) + chromeY));

        Services.DialogPlacement.CentreOverOwner(this, owner, Width, Height);
    }

    /// <summary>
    /// Escape, or Ctrl+D, the shortcut that opens the viewer (the user's call,
    /// session 46). A held Ctrl+D only opens it: the repeats land here.
    /// </summary>
    internal static bool ClosesOn(Key key, ModifierKeys modifiers, bool isRepeat) =>
        key is Key.Escape || (key is Key.D && modifiers == ModifierKeys.Control && !isRepeat);

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (ClosesOn(e.Key, Keyboard.Modifiers, e.IsRepeat))
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
