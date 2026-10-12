using System.ComponentModel;
using System.Windows;
using System.Windows.Interop;

namespace AudioFool.Services;

/// <summary>
/// Closing an owned, non-modal window (the song and album tag windows, Analyze,
/// the art viewer) sometimes brought another application to the front instead
/// of AudioFool (the user's report, session 46). Windows picks the next window to
/// activate as one closes, and WPF can drop the owner link before it does, so
/// the pick falls to whatever is next in line on the desktop. The usual remedy:
/// while the closing window is still the active one, activate its owner first.
/// <para>
/// Each such window calls <see cref="Attach"/> in its constructor. Not a class
/// handler on Loaded, which never fires for a Window (as for a MenuItem; see
/// HANDOFF). Only an active window hands activation on: one closed while the
/// user is in another app leaves that app in front.
/// </para>
/// </summary>
public static class OwnerActivation
{
    public static void Attach(Window window) => window.Closing += OnClosing;

    private static void OnClosing(object? sender, CancelEventArgs e)
    {
        // A modal dialog is left to WPF, which re-enables and activates the window
        // that was active before it; its owner is still disabled at this point.
        if (!e.Cancel && !ComponentDispatcher.IsThreadModal
            && sender is Window { IsActive: true, Owner: { IsVisible: true } owner }
            && owner.WindowState != WindowState.Minimized)
            owner.Activate();
    }
}
