# AudioFool

A personal Windows 11 music player. **C# / .NET 10 / WPF**, with WPF-UI for Fluent
controls (light PS1 theme; see `design/`), ManagedBass for audio and TagLibSharp for metadata.


## Read these first

- **[HANDOFF.md](HANDOFF.md)** — current state, the build-and-install loop, verification
  techniques, and the traps worth not rediscovering. Start here.
- **[README.md](README.md)** — how the app works: output modes, DSD, the library cache,
  portable-drive handling, keyboard shortcuts.
- [HISTORY.md](HISTORY.md) — what each past session changed, oldest first. Not needed
  at the start of a session; look things up there when you need the background.

## Three things to know before touching anything

**1. Publishing does not update the app the user runs.**
`dotnet publish` writes to `C:\MusicPlayer\publish\`. The shortcuts launch
`%LOCALAPPDATA%\Programs\AudioFool\`. Always finish by copying publish output over the
installed copy, or your change appears to do nothing. Full commands in HANDOFF.md.

**2. Do not drive the mouse or keyboard to test the UI.**
The user works on this machine. Moving the pointer, sending keystrokes to the focused
window, or pulling windows to the foreground interrupts them — this stopped a previous
session. Use UI Automation (sets values without stealing focus) and `PrintWindow` for
screenshots, calling `SetProcessDPIAware()` first. Details in HANDOFF.md.

**3. The music library lives on a removable drive.**
`D:\Music`, an external SSD, ~26,000 tracks. Drive letters can change and the folder may
be absent at startup. Both are handled (`LibraryRelocator`; unreachable folders are
carried over rather than treated as deleted). Never write code that assumes a stable
absolute path, and never let a missing folder be interpreted as a deleted library.

## Shell notes

`dotnet` is not on PATH in a fresh shell — prefix with
`$env:PATH = "C:\Program Files\dotnet;$env:PATH"`. Never combine that assignment with a
`Remove-Item` in one PowerShell call; the sandbox guard misreads it as deleting
`C:\Program` and blocks the command.

## Verify, don't assume

Playback, DSD correctness and drive behaviour have all been verified by driving the real
app or a headless probe against `AudioFool.Core` — not by reasoning about the code. Keep
that standard. `dotnet test` runs the unit tests over `AudioFool.Core` (634 as of
session 47). They cover the logic, not the windows: anything visual is checked with
ThemeLab or the installed app, as HANDOFF.md describes.


## The PS1 theme

The app's only theme, inspired by the PlayStation 1. It was built in sessions 25–31 and
is finished; changes since are the user's requests, one at a time.

- **`design/theme-spec.md`**: source of truth for every color, size, layout rule, and behavior. Read it before any UI work.
- **`design/theme-tokens.json`**: the same values in machine-readable form.
- **`design/progress.md`**: every design decision and what each session verified. Read the Decisions section before UI work; add to it when you change the UI.
- `design/mockup/` and `design/screenshots/`: visual references. The mockup file is design-tool output; read it for icon shapes, but don't try to run it.

If the spec and the mockup disagree, the spec wins. If the spec is silent on something, ask rather than guess.

### Rules that must not slip

- **Use the central theme.** All colors, font sizes, spacing, and corner radii come from the theme resources built from `theme-tokens.json`. No hard-coded values in individual screens.
  - The JSON is embedded and loaded at startup (`src/AudioFool/Theming/TokenResources.cs`); each resource is named by its JSON path. Colours, text styles, radii and shadows: `{DynamicResource color.panel.bg}`, `{DynamicResource type.albumTitle}`. Numbers, converted to the property's type: `{theme:Token albums.rowHeight}`. Paddings and margins: `{theme:Thickness X=songTable.listPaddingX, Y=songTable.listPaddingY}`. `xmlns:theme="clr-namespace:AudioFool.Theming"`. The full mapping is in `design/progress.md`.
  - Changing a value means editing `theme-tokens.json` and rebuilding; never copy a value into XAML.
- **No visible scrollbars.** Hide them; lists must still scroll with the mouse wheel, trackpad, and keyboard.
- **Song rows: single-click selects, double-click plays.**
- **Keep the existing PlayStation logo asset** (`PlayStation-Logo-1994.png`). Don't replace or redraw it.
- **Teal means playback** (play/pause, seekbar, now-playing row). **Blue is only for artist names.** Don't add colors outside the roles in the spec.
- **Album titles** wrap to two lines max, then "…"; tooltip only when truncated.

### How we work on the UI

- Before editing, describe the plan and wait for approval.
- Stay within the change asked for; don't restyle other parts of the UI.
- For visual checks, render with ThemeLab or publish and install as described in HANDOFF.md, then capture the app with `PrintWindow`. Never drive the mouse or keyboard.
- When done, list what changed and anything that differs from the spec, update the spec if the user's request changed what it says, and add the decision to `design/progress.md`.
- Work on `main`, no branch (the user's call, 2026-10-01). WPF-UI's own colour keys aren't overridden: a stock control that shows gets its colours from tokens, on the control or in its style.
