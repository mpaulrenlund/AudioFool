namespace AudioFool.ViewModels;

public sealed record ShortcutRow(string Keys, string Does);

public sealed record ShortcutGroup(string Title, IReadOnlyList<ShortcutRow> Rows);

/// <summary>
/// The KEYBOARD SHORTCUTS section in Statistics (the user's call, session 46):
/// every shortcut, grouped. Keep it in step with MainWindow.Shortcuts.cs, the
/// global hotkeys and README's table.
/// </summary>
public static class ShortcutGuide
{
    /// <param name="globalHotkeys">F9–F11 are listed only while they are on in settings.</param>
    public static IReadOnlyList<ShortcutGroup> For(bool globalHotkeys)
    {
        List<ShortcutGroup> groups =
        [
            new("PLAYBACK",
            [
                new("Space", "Play / pause"),
                new("Enter", "Play the selected song"),
                new("Ctrl+Left / Right", "Previous / next track"),
                new("Ctrl+Up / Down", "Volume up / down 5%"),
                new("Ctrl+M", "Mute / unmute"),
                new("Ctrl+S", "Shuffle on / off"),
                new("Ctrl+E", "Next repeat setting"),
                new("Ctrl+B", "Bit-Perfect on / off"),
                new("Media keys", "Play / pause, next, previous, stop"),
            ]),
            new("LIBRARY",
            [
                new("Ctrl+F", "Search (Escape leaves the box)"),
                new("Ctrl+J", "Jump to the playing song"),
                new("Ctrl+L", "Like / unlike the playing song"),
                new("F2", "Edit a tag in place (Enter saves, Escape cancels)"),
                new("Ctrl+R or F5", "Rescan the library"),
            ]),
            new("WINDOWS",
            [
                new("Ctrl+W", "Statistics"),
                new("Ctrl+Q", "Analyze the selected song, or the playing one"),
                new("Ctrl+D", "Album art for the playing album, or the selected one; again to close"),
                new("Ctrl+T", "Light / dark theme"),
                new("Ctrl+Shift+F", "Visualizer; again to close"),
                new("Escape", "Close the album art, or this window"),
            ]),
            new("VISUALIZER",
            [
                new("Double-click or Alt+Enter", "Fullscreen, and back"),
                new("Escape", "Leave fullscreen, or close it"),
                new("Up / Down", "Livelier / calmer"),
                new("B", "projectM / the bars"),
                new("Right / Left", "projectM: next / previous preset"),
                new("L", "projectM: stay on this preset, or move on again"),
                new("Delete", "projectM: never show this preset again"),
                new("Space, Ctrl+arrows and the rest", "Playback, as in the main window"),
            ]),
        ];

        if (globalHotkeys)
        {
            groups.Add(new("FROM ANY APP",
            [
                new("F9", "Play / pause"),
                new("F10", "Previous track, or restart the song"),
                new("F11", "Next track"),
            ]));
        }

        return groups;
    }
}
