using System.Windows;
using System.Windows.Controls;

namespace AudioFool.Theming;

/// <summary>
/// The paddings of a table drawn as the Songs table (theme.songTable in
/// Chrome.xaml), worked out from the songTable tokens: the main window's track
/// grid and Find and Replace's preview (session 63) share them.
/// </summary>
internal static class SongTableLayout
{
    /// <summary>
    /// A squeezed flexible column keeps at least this much, so it stays a column
    /// rather than disappearing to a sliver.
    /// </summary>
    public const double FlexFloor = 70;

    /// <summary>
    /// The 12 px gap split into half a gap either side of every cell and header,
    /// and the row's and header's own paddings less the half gap the outermost
    /// cells already supply.
    /// </summary>
    public static void ApplyPaddings(DataGrid grid)
    {
        var n = TokenResources.Current!.Numbers;

        var gap = n["songTable.columnGap"];
        var listX = n["songTable.listPaddingX"];
        var listY = n["songTable.listPaddingY"];
        var rowPadding = n["songTable.rowPaddingX"] - (gap / 2);
        var headerPadding = n["songTable.headerPaddingX"] - (gap / 2);

        grid.Resources["songTable.derived.cellPadding"] = new Thickness(gap / 2, 0, gap / 2, 0);
        grid.Resources["songTable.derived.headerPadding"] = new Thickness(headerPadding, 0, headerPadding, 0);

        // DataGrid fits its columns to the viewport less the cells' offset from
        // the grid's left edge, which already includes the left list padding.
        // So the list area keeps only the row padding on the right, and the row's
        // fill stops the rest of the list padding short of the row's right edge.
        grid.Resources["songTable.derived.listMargin"] = new Thickness(listX, listY, rowPadding, listY);
        grid.Resources["songTable.derived.rowCellsMargin"] = new Thickness(rowPadding, 0, 0, 0);
        grid.Resources["songTable.derived.rowFillMargin"] = new Thickness(0, 0, listX - rowPadding, 0);
    }
}
