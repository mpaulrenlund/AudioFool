using System.Windows;
using System.Windows.Controls;

namespace AudioFool.Theming;

/// <summary>
/// Gives every menu's drop-down and item highlights the theme's corners.
/// WPF-UI's MenuItem templates fix them: 8 px on the box a drop-down or
/// submenu opens in (<c>SubmenuBorder</c>) and 4 px on an item's highlight
/// (<c>Border</c>), where the spec has 4 px surfaces (<c>radius.surface</c>)
/// and 3 px rows (<c>radius.row</c>). A style can't reach a radius inside a
/// template, so this sets the named parts when a menu opens.
///
/// It listens for <see cref="MenuItem.SubmenuOpenedEvent"/> and
/// <see cref="ContextMenu.OpenedEvent"/> as class handlers, so it covers every
/// menu whatever style its items carry (the Libraries folder rows have an
/// <c>ItemContainerStyle</c> with no <c>BasedOn</c>). A class handler on
/// <c>Loaded</c> never fires for a MenuItem, which is why it isn't used. The
/// logo's own top-level border keeps its 6 px: it is transparent in every state.
/// </summary>
public static class MenuCorners
{
    private static bool _registered;

    public static void Register()
    {
        if (_registered)
            return;

        _registered = true;
        EventManager.RegisterClassHandler(
            typeof(MenuItem), MenuItem.SubmenuOpenedEvent, new RoutedEventHandler(OnSubmenuOpened));
        EventManager.RegisterClassHandler(
            typeof(ContextMenu), ContextMenu.OpenedEvent, new RoutedEventHandler(OnContextMenuOpened));
    }

    private static void OnSubmenuOpened(object sender, RoutedEventArgs e)
    {
        // The event bubbles through every ancestor item; act once, on the item
        // that opened.
        if (sender is not MenuItem opened || !ReferenceEquals(e.OriginalSource, sender))
            return;

        if (!TryGetRadii(out var surface, out var row))
            return;

        if (opened.Role is MenuItemRole.TopLevelHeader or MenuItemRole.SubmenuHeader)
            SetCorners(opened, "SubmenuBorder", surface);

        RoundItems(opened, row);
    }

    private static void OnContextMenuOpened(object sender, RoutedEventArgs e)
    {
        if (sender is ContextMenu menu && TryGetRadii(out _, out var row))
            RoundItems(menu, row);
    }

    // The highlight of each item in a menu. Data-bound rows (the folder list)
    // get their containers only when the open menu is first laid out, after the
    // event, so this waits for that. A highlight is transparent until the
    // pointer is over it, so nothing shows in between.
    private static void RoundItems(ItemsControl menu, CornerRadius row) =>
        menu.Dispatcher.BeginInvoke(
            System.Windows.Threading.DispatcherPriority.Loaded, new Action(() => RoundItemsNow(menu, row)));

    private static void RoundItemsNow(ItemsControl menu, CornerRadius row)
    {
        for (var i = 0; i < menu.Items.Count; i++)
        {
            if (menu.ItemContainerGenerator.ContainerFromIndex(i) is not MenuItem item)
                continue;

            item.ApplyTemplate();

            // A top-level item's border is the logo's, left alone.
            if (item.Role is MenuItemRole.SubmenuItem or MenuItemRole.SubmenuHeader)
                SetCorners(item, "Border", row);
        }
    }

    private static void SetCorners(MenuItem item, string part, CornerRadius radius)
    {
        if (item.Template?.FindName(part, item) is Border border)
            border.CornerRadius = radius;
    }

    private static bool TryGetRadii(out CornerRadius surface, out CornerRadius row)
    {
        var app = Application.Current;
        if (app?.TryFindResource("radius.surface") is CornerRadius s &&
            app.TryFindResource("radius.row") is CornerRadius r)
        {
            (surface, row) = (s, r);
            return true;
        }

        (surface, row) = (default, default);
        return false;
    }
}
