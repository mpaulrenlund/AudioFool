using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using System.Windows.Threading;

using Track = AudioFool.Core.Models.Track;

namespace AudioFool;

/// <summary>
/// Leaving the search box (the user's call, session 46): Escape, or a click
/// anywhere else in the window, takes the cursor out and keeps the search.
/// Escape used to clear it; the × still does.
/// </summary>
public partial class MainWindow
{
    private IInputElement? _focusBeforeSearch;

    private void HookSearchFocus()
    {
        SearchBox.GotKeyboardFocus += (_, e) =>
        {
            if (e.OldFocus is DependencyObject old && !IsInSearchBox(old) && GetWindow(old) == this)
                _focusBeforeSearch = e.OldFocus;
        };

        SearchBox.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape && Keyboard.Modifiers == ModifierKeys.None)
            {
                LeaveSearch();
                e.Handled = true;
            }
        };

        // A click on something focusable takes focus itself; a click on anything
        // else (a header, the art, a transport button that skips focus) doesn't,
        // so once the click has been handled, focus is moved out if still here.
        PreviewMouseDown += (_, e) =>
        {
            if (!SearchBox.IsKeyboardFocusWithin || IsInSearchBox(e.OriginalSource as DependencyObject))
                return;

            Dispatcher.BeginInvoke(() =>
            {
                if (SearchBox.IsKeyboardFocusWithin)
                    LeaveSearch();
            }, DispatcherPriority.Input);
        };
    }

    /// <summary>
    /// Back to where focus was before the search box, or failing that the
    /// selected song (so Space, the arrows and F2 carry on), or the song table.
    /// </summary>
    internal void LeaveSearch()
    {
        if (_focusBeforeSearch is UIElement { IsVisible: true, IsEnabled: true, Focusable: true } before
            && GetWindow(before) == this && before.Focus())
            return;

        if (TrackGrid.SelectedItem is Track track && FocusCell(track, SongColumn) is not null)
            return;

        TrackGrid.Focus();
    }

    private bool IsInSearchBox(DependencyObject? node)
    {
        while (node is not null)
        {
            if (node == SearchBox)
                return true;
            node = node is Visual or Visual3D ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node);
        }

        return false;
    }
}
