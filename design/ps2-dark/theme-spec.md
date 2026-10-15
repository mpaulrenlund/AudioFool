# AudioFool — PS2 Dark Theme Spec

AudioFool's second theme, inspired by the PlayStation 2: **matte black with a blue glow**. It sits beside the PS1 Light theme, and the user can switch between the two while the app runs.

This file covers **only what differs from PS1 Light**. Anything it doesn't mention (layout, sizes, spacing, fonts, icons, corner radii, behaviors, the logo asset) is exactly as in [`../theme-spec.md`](../theme-spec.md). When the two specs disagree about the dark theme, this file wins.

Companion files in this folder:

- `theme-tokens.dark.json`: the full dark token set. It has the same keys as `../theme-tokens.json` plus the six new keys in §6 (for three new effects), every value outside `color` and `shadow` is identical, and a plain diff of the two files shows only the colors and shadows that change.
- `brief.md`: the starting brief. The decisions below supersede its open questions.
- `session-prompts.md`: prompts for the three build sessions.
- `claude-md-addition.md`: proposed text for `CLAUDE.md` once the dark theme ships.
- Design canvas with the mockup, the option boards and the token sheet: https://claude.ai/artifact/T4EsUQNSd2v7Xak9V5CxZy (private to the user; a Claude Code session can't open it, and nothing here depends on it).

---

## 1. Decisions

Settled in design review on 2026-10-14:

| Question | Decision |
|---|---|
| Which color means playback? | **PS2 blue** (`#4C8DFF`) for playback. Artist names in a pale blue-white (`#A9C8FF`). |
| Window buttons | **DualShock symbol colors**: Minimize in triangle green, Maximize in square pink, Close in cross blue. Hover is as in PS1: all three get the same faint grey, and Close's X stays cross blue (settled 2026-10-15; see §7.2). |
| Bit-Perfect off | **Standby red** (`#FF5A5F`). On is power-light green (`#3DDC84`). |
| Glow | **On the Play/Pause button only**: a soft blue glow and a 1 px blue ring. Nothing else glows. |
| Transport buttons | **DualShock 2 black**: matte black faces with the same molded look as PS1. |
| Title bar | **Menu glow**: a faint deep-blue haze at the top centre, like the PS2 system menu (§7.1). |
| Switching themes | **Status bar button and Ctrl+T**, switching live. The app reopens in the theme that was in use when it closed; **Dark** when nothing has been saved yet (§3). |

---

## 2. Design principles (dark)

- **Matte black, never pure black.** Surfaces are the console's matte finish (`#111216` window, `#1A1C22` panels), with a slight blue undertone. Text is off-white, never pure white.
- **Color still has a job.** In this theme:
  - **Blue means playback**: Play/Pause, the played part of the waveform, the now-playing triangle, the search focus ring. (PS1 uses teal for this role.)
  - **Pale blue means artist names**, and the now-playing row's text is a similar pale blue.
  - **Green means on** (the console's power light): library light, Last.fm connected, Bit-Perfect on.
  - **Red means off** (the standby light): Bit-Perfect off, Last.fm off.
  - **DualShock symbol colors** are the remaining nods to the controller: the window buttons, Shuffle on (cross blue), Repeat on (circle red) and a liked song's heart (square pink).
- **Glow is rare.** Only the Play/Pause button glows, plus the ambient menu glow in the title bar.
- **Same molded plastic.** Panels keep the faint top highlight; round buttons keep the bottom shade and top highlight, in dark values.

---

## 3. Switching themes

### 3.1 The control

- **Status bar button**, immediately left of Bit-Perfect, 10 px apart (`statusBar.chipGap`). Same look as the other status bar buttons: 26 px tall, 4 px radius, `status.chipBg`, 1 px `status.chipBorder`, 12 px padding on both sides (`statusBar.chipPaddingX`, as Last.fm and Bit-Perfect; the user's call on 2026-10-14 over "10 px left and 12 px right").
- Content: a 14 px icon alone, no label (the user's call, 2026-10-15): a moon in Dark, a sun in Light, showing the **current** theme. Icons are drawn on the 24-unit grid with a 2.2 stroke, like the refresh icon:
  - Moon: `M20 14.5A8 8 0 0 1 9.5 4a8 8 0 1 0 10.5 10.5z`
  - Sun: a circle at (12, 12) with radius 4, and rays `M12 2.5v2 M12 19.5v2 M4.6 4.6l1.4 1.4 M18 18l1.4 1.4 M2.5 12h2 M19.5 12h2 M4.6 19.4L6 18 M18 6l1.4-1.4`
- One click switches to the other theme. Accessible name "Theme: Dark" or "Theme: Light". It's an ordinary button that changes its icon, not a pressed/unpressed toggle. Tab order: before Bit-Perfect.
- **Ctrl+T** does the same (a window shortcut like Ctrl+B for Bit-Perfect, so it's off while a song-table cell is being edited). Add it to README's shortcut table.
- ~~Optional: a "Dark Theme" check item in the logo menu, beside Statistics… and Last.fm…, showing Ctrl+T as its shortcut.~~ Asked 2026-10-14: not wanted (the button and Ctrl+T only).

### 3.2 Behavior

- The switch is **instant and live**: no restart, and nothing else changes. Playback, the queue, the selections, the scroll positions and any open dialog stay as they are, and every open window (Statistics, Analysis, the tag editor, Last.fm, art search and the art viewer, prompts) switches with the main window.
- **Remember it.** Save the theme in `settings.json` whenever it changes: `AppSettings.Theme`, `Light` or `Dark`, stored by name like the other enums there. Startup reads it and applies it before the main window is built.
- **First launch**: when the setting is missing (the first run after this update, or a new settings file), open in **Dark**.

### 3.3 Implementation notes

The current code applies one theme, once, at startup (`ThemeService.Apply()` in `App.xaml.cs`, after the settings load and before `MainWindow` is built). Switching live needs the following. Check each against the code before relying on it.

1. **Both token sets are embedded.** Add `design/ps2-dark/theme-tokens.dark.json` to `src/AudioFool/AudioFool.csproj` next to the light file, with `LogicalName="AudioFool.theme-tokens.dark.json"`, and copy it into the test project's `TestData` the way `theme-tokens.json` is.
2. **Swap the token dictionary.** `ThemeService.Apply(theme)` at startup; `ThemeService.Switch(theme)` at runtime builds the other set with `TokenResources.Build` and replaces the token dictionary in `Application.Resources.MergedDictionaries` (same keys, so `DynamicResource` references update). `Chrome.xaml` stays merged as it is.
3. **Every color lookup has to be live.** Almost all of them already are (174 `DynamicResource color.*` and 12 `DynamicResource shadow.*` references in the XAML). The exceptions:
   - **`type.*` text styles.** `TokenResources.TextStyle` sets `Foreground` to a brush object, and 17 styles derive from `type.*` through `BasedOn="{StaticResource …}"`, which can't be dynamic. Make that setter a dynamic reference to the color's key instead (a `DynamicResourceExtension` for `color.<name>`), so no style holds a color.
   - **11 static references**: the 10 `{StaticResource color.….value}` brushes in `Chrome.xaml` (WPF-UI's flyout, menu-bar and `PaletteRedBrush` keys) and `<StaticResource x:Key="PaletteRedBrush" ResourceKey="color.window.buttonHover"/>` in `MainWindow.xaml`. Rebuild those on a switch, or make them dynamic where WPF allows.
   - **Values read once in code.** `ThemeService` sets WPF-UI's accent from `color.control.iconNeutral.value`: re-apply it on every switch. The song table's drop line and the spectrogram marker are looked up when they're drawn, so they follow by themselves.
4. **WPF-UI's base dictionary.** `App.xaml` merges `ThemesDictionary Theme="Light"` so that any stock key the tokens don't restate falls back to a light value. In Dark, that fallback should be WPF-UI's Dark values (and the accent call should pass `ApplicationTheme.Dark`).
5. **Window frame.** If Windows draws the 1 px window border, tell it the window is dark (`DWMWA_USE_IMMERSIVE_DARK_MODE`) in Dark, and light again in Light.
6. **ThemeLab** needs a `--theme light|dark` switch so every render and brush dump can be made in both themes.

---

## 4. Colors

Names are token paths under `color` (`panel.bg` is `color.panel.bg`), as in the PS1 spec. "Same" means the dark file keeps the PS1 value.

### `window`

| Token | PS1 Light | PS2 Dark | Notes |
|---|---|---|---|
| `window.bg` | `#BAB7B3` | `#111216` |  |
| `window.minimize` | `#FFDA00` | `#3DD6A3` | triangle green |
| `window.maximize` | `#006FCD` | `#F28FCB` | square pink |
| `window.close` | `#E60012` | `#7AA7FF` | cross blue, on hover too (§7.2) |
| `window.buttonHover` | `rgba(0,0,0,0.06)` | `rgba(255,255,255,0.08)` | hover for all three buttons, Close included |
| `window.closeHover` | `#C42B1C` | Same | unused in both themes (§7.2) |
| `window.closeHoverIcon` | `#FFFFFF` | Same | unused in both themes (§7.2) |

### `panel`

| Token | PS1 Light | PS2 Dark | Notes |
|---|---|---|---|
| `panel.bg` | `#CAC7C3` | `#1A1C22` |  |
| `panel.border` | `#A5A29D` | `#2B2F38` |  |
| `panel.divider` | `#B3B0AB` | `#242831` |  |
| `panel.highlight` | `rgba(255,255,255,0.45)` | `rgba(255,255,255,0.04)` | 1 px top inside edge |

### `text`

| Token | PS1 Light | PS2 Dark | Notes |
|---|---|---|---|
| `text.heading` | `#1C1B19` | `#F0F2F6` |  |
| `text.primary` | `#22211F` | `#E2E5EB` |  |
| `text.secondary` | `#3E3B38` | `#B3B9C4` |  |
| `text.muted` | `#4E4B48` | `#8C93A0` | brief had `#868D99`; nudged for 4.5:1 on selected rows |
| `text.placeholder` | `#5A5754` | `#848B98` | brief had `#6E7480`; nudged for 4.5:1, also when focused |
| `text.status` | `#33312F` | `#B3B9C4` |  |

### `accent`

| Token | PS1 Light | PS2 Dark | Notes |
|---|---|---|---|
| `accent.playback` | `#15786C` | `#4C8DFF` | the playback blue (see §9) |
| `accent.playbackText` | `#0C5A51` | `#9CC0FF` | now-playing row text |
| `accent.blue` | `#00519A` | `#A9C8FF` | artist names: the system menu’s blue-white |
| `accent.like` | `#A8327F` | `#F28FCB` | the controller’s own square pink |

### `state`

| Token | PS1 Light | PS2 Dark | Notes |
|---|---|---|---|
| `state.selectionBg` | `#BAB7B2` | `#252A34` |  |
| `state.hoverOverlay` | `rgba(0,0,0,0.035)` | `rgba(255,255,255,0.04)` | white at 4% |
| `state.nowPlayingBg` | `#B4C6BF` | `#16233D` |  |
| `state.searchFocusBg` | `#D9D6D2` | `#20232A` |  |
| `state.focusRing` | `#0C5A51` | `#9CC0FF` |  |
| `state.searchFocusRing` | `#15786C` | `#4C8DFF` | playback blue |

### `control`

| Token | PS1 Light | PS2 Dark | Notes |
|---|---|---|---|
| `control.face` | `#E4E1DD` | `#121318` | DualShock 2 black |
| `control.faceBorder` | `#A9A6A1` | `#2E323B` |  |
| `control.faceHover` | `#ECE9E5` | `#1B1D23` |  |
| `control.iconNeutral` | `#3F3D3B` | `#C9CED8` |  |
| `control.iconOff` | `#75726E` | `#8C93A0` |  |
| `control.shuffleOn` | `#2F5DB8` | `#7AA7FF` | cross blue |
| `control.repeatOn` | `#C62F37` | `#FF6670` | circle red |
| `control.playPause` | `#15786C` | `#4C8DFF` | playback blue |

### `slider`

| Token | PS1 Light | PS2 Dark | Notes |
|---|---|---|---|
| `slider.track` | `#ADAAA5` | `#3F4451` | also the unplayed waveform |
| `slider.seekFill` | `#15786C` | `#4C8DFF` | playback blue |
| `slider.volumeFill` | `#75726E` | `#8C93A0` | the PS1 spec table says `#3F3D3B`; see §10 |
| `slider.thumb` | `#F7F6F4` | `#E2E5EB` |  |
| `slider.thumbBorder` | `#8F8C88` | `#111216` |  |

### `status`

| Token | PS1 Light | PS2 Dark | Notes |
|---|---|---|---|
| `status.chipBg` | `#C6C3BF` | `#1A1C22` |  |
| `status.chipBorder` | `#9C9995` | `#2F333C` |  |
| `status.libraryDot` | `#15786C` | `#3DDC84` | power-light green |
| `status.lastFmDot` | `#15786C` | `#3DDC84` | power-light green |
| `status.lastFmOff` | `#FFDA00` | `#FF5A5F` | standby red |
| `status.scanTrack` | `#ADAAA5` | `#3F4451` |  |
| `status.scanFill` | `#4E4B48` | `#8C93A0` |  |
| `status.bitPerfectOff` | `#FFDA00` | `#FF5A5F` | standby red |
| `status.bitPerfectOn` | `#15786C` | `#3DDC84` | power-light green |
| `status.dotRing` | `rgba(0,0,0,0.25)` | `rgba(0,0,0,0.5)` |  |

### `art`

| Token | PS1 Light | PS2 Dark | Notes |
|---|---|---|---|
| `art.border` | `#8F8C88` | `#33373F` |  |
| `art.thumbBorder` | `#9C9995` | `#2F333C` |  |
| `art.thumbBorderSelected` | `#6E6B67` | `#4A505C` |  |

The PS1 spectrogram palette (`spectrogram.level0`–`level5`, `marker`) is data, not chrome: it's **the same in both themes**.

---

## 5. Shadows

Same layer structure as PS1, so the inset overlays and drop shadows are drawn the same way; only the strengths change. Dark surfaces need stronger shadows to read at all.

| Token | PS1 Light | PS2 Dark |
|---|---|---|
| `shadow.panelHighlight` | `inset 0 1px 0 rgba(255,255,255,0.45)` | `inset 0 1px 0 rgba(255,255,255,0.04)` |
| `shadow.searchInset` | `inset 0 1px 2px rgba(0,0,0,0.15)` | `inset 0 1px 2px rgba(0,0,0,0.45)` |
| `shadow.sliderTrackInset` | `inset 0 1px 2px rgba(0,0,0,0.25)` | `inset 0 1px 2px rgba(0,0,0,0.5)` |
| `shadow.sliderThumb` | `0 1px 3px rgba(0,0,0,0.3)` | `0 1px 3px rgba(0,0,0,0.6)` |
| `shadow.headerArt` | `0 4px 10px rgba(0,0,0,0.25)` | `0 4px 14px rgba(0,0,0,0.6)` |
| `shadow.nowPlayingArt` | `0 0 6px rgba(0,0,0,0.25)` | `0 0 8px rgba(0,0,0,0.6)` |
| `shadow.controlButton` | `inset 0 -3px 0 rgba(0,0,0,0.12), inset 0 1px 0 rgba(255,255,255,0.8), 0 1px 3px rgba(0,0,0,0.25)` | `inset 0 -3px 0 rgba(0,0,0,0.45), inset 0 1px 0 rgba(255,255,255,0.1), 0 1px 3px rgba(0,0,0,0.6)` |
| `shadow.playButton` | `inset 0 -4px 0 rgba(0,0,0,0.12), inset 0 1px 0 rgba(255,255,255,0.8), 0 2px 5px rgba(0,0,0,0.28)` | `inset 0 -4px 0 rgba(0,0,0,0.45), inset 0 1px 0 rgba(255,255,255,0.1), 0 2px 5px rgba(0,0,0,0.6)` |

---

## 6. New tokens (add to both files)

Three effects are new to the dark theme. Their keys go into **both** token files so the files keep the same keys; the PS1 values are transparent, so the same markup draws nothing in Light. `theme-tokens.dark.json` already has them. `../theme-tokens.json` needs them added.

| Token | PS1 Light (add to `../theme-tokens.json`) | PS2 Dark | Used for |
|---|---|---|---|
| `color.window.titleGlow` | `rgba(0,0,0,0)` | `rgba(40,90,200,0.2)` | Menu glow in the title bar (§7.1) |
| `color.control.playRing` | `rgba(0,0,0,0)` | `rgba(76,141,255,0.35)` | 1 px ring just outside Play/Pause (§7.3) |
| `shadow.playGlow` | `0 0 0 rgba(0,0,0,0)` | `0 0 18px rgba(76,141,255,0.45)` | Glow around Play/Pause (§7.3) |
| `titleBar.glow.radiusX` | `900` | `900` | Menu glow shape (§7.1) |
| `titleBar.glow.radiusY` | `130` | `130` | Menu glow shape (§7.1) |
| `titleBar.glow.centerY` | `-40` | `-40` | Menu glow shape (§7.1) |

---

## 7. Components that look different

Everything below uses the tokens above; no screen needs theme-specific code.

### 7.1 Title bar: menu glow

A radial gradient drawn behind the title bar's content (logo, search, window buttons), under nothing else:

- Centred horizontally on the title bar; its centre `titleBar.glow.centerY` (−40 px) above the top edge.
- Radii `titleBar.glow.radiusX` × `titleBar.glow.radiusY` (900 × 130 px).
- Stops: 0 → `window.titleGlow`; 0.45 → the same color at 40% of its alpha (`rgba(40,90,200,0.08)`); 0.75 → transparent.

The result is a faint deep-navy haze along the top centre that has faded out by the bottom of the 48 px title bar. No bottom border, as in PS1. In Light the color is transparent.

### 7.2 Window buttons

Icons in their DualShock colors at rest: Minimize `window.minimize` (triangle green), Maximize `window.maximize` (square pink), Close `window.close` (cross blue). Hover as in PS1: all three get `window.buttonHover`, and the icons keep their colors, so Close's X stays cross blue. That is what PS1 has done since 2026-10-02 (the user's call; `../theme-spec.md` §6.1), settled for Dark on 2026-10-15. `window.closeHover` and `window.closeHoverIcon` are unused in both themes.

### 7.3 Transport buttons

Matte black faces (`control.face`, 1 px `control.faceBorder`) with the molded look of `shadow.controlButton` / `shadow.playButton`. Icons: Previous and Next `control.iconNeutral`; Play/Pause `control.playPause`; Shuffle and Repeat `control.iconOff`, or `control.shuffleOn` / `control.repeatOn` when on (the repeat-one badge disc is `control.repeatOn` with a "1" in `control.face`, as in PS1). Hover `control.faceHover`; pressed nudges down 1 px, as in PS1.

**Play/Pause only** also gets:

- a 1 px ring in `control.playRing`, just outside its border (a 62 px circle around the 60 px button);
- the `shadow.playGlow` glow: no offset, 18 px blur, `rgba(76,141,255,0.45)`.

The button's own `Effect` is already its molded drop shadow, so draw the ring and glow on an element behind the button rather than replacing that effect.

### 7.4 Status bar

Status bar buttons in `status.chipBg` / `status.chipBorder`. Library light and Last.fm connected are green (`status.libraryDot`, `status.lastFmDot`). The Bit-Perfect dot is red when off and green when on, with the faint `status.dotRing`. The theme button (§3.1) sits left of Bit-Perfect.

### 7.5 Lists and song table

Selected rows `state.selectionBg`; hover `state.hoverOverlay` (white at 4%). Now-playing row `state.nowPlayingBg` with `accent.playbackText` text and the triangle in `accent.playback`. A liked heart is `accent.like`, the controller's own square pink, which reads well on dark (PS1 had to deepen it to `#A8327F` to show on gray).

### 7.6 Seekbar, waveform and volume

Played part `slider.seekFill` (blue), the rest `slider.track`; the boundary is 3.0:1 in dark, against 2.3:1 in PS1. Volume fill `slider.volumeFill`; handles `slider.thumb` with a `slider.thumbBorder` edge.

### 7.7 Search box

At rest: `panel.bg` with the dark `shadow.searchInset`. Focused: `state.searchFocusBg` with a 2 px `state.searchFocusRing` ring and **no glow**.

### 7.8 Dialogs

Statistics, Analysis, the tag editor, Last.fm, art search, the art viewer and prompts have no dark-specific design beyond the tokens. Render each in ThemeLab in both themes and check them by eye. The Analysis window's spectrogram keeps its colors.

---

## 8. Contrast (dark)

Text aims for 4.5:1 and icons for 3:1, as in PS1.

| Foreground | Background | Ratio |
|---|---|---|
| `text.heading` | `panel.bg` | 15.2 : 1 |
| `text.primary` | `panel.bg` | 13.5 : 1 |
| `text.secondary` | `panel.bg` / `state.selectionBg` | 8.6 / 7.3 : 1 |
| `text.muted` | `panel.bg` / `state.selectionBg` | 5.5 / 4.7 : 1 |
| `text.placeholder` | `panel.bg` / `state.searchFocusBg` | 5.0 / 4.6 : 1 |
| `text.status` | `window.bg` | 9.5 : 1 |
| `accent.playback` (playback blue) | `panel.bg` | 5.3 : 1 |
| `accent.playbackText` | `state.nowPlayingBg` | 8.5 : 1 |
| `accent.blue` (artist names) | `panel.bg` / `state.selectionBg` | 10.0 / 8.5 : 1 |
| `accent.like` | `panel.bg` / `state.nowPlayingBg` | 7.8 / 7.1 : 1 |
| `control.iconNeutral` | `control.face` | 11.8 : 1 |
| `control.playPause` | `control.face` | 5.8 : 1 |
| `control.iconOff` | `control.face` | 6.0 : 1 |
| `control.shuffleOn` / `control.repeatOn` | `control.face` | 7.8 / 6.5 : 1 |
| `window.minimize` / `maximize` / `close` | `window.bg` | 10.1 / 8.5 / 7.8 : 1 |
| `status.libraryDot` | `window.bg` | 10.5 : 1 |
| `status.bitPerfectOff` / `bitPerfectOn` | `status.chipBg` | 5.6 / 9.5 : 1 |
| `state.focusRing` | `panel.bg` | 9.2 : 1 |
| `state.searchFocusRing` | `window.bg` | 5.9 : 1 |
| `slider.seekFill` | `slider.track` | 3.0 : 1 |

`slider.track` (the unplayed waveform) is 1.7:1 on `panel.bg`, on purpose, as PS1's is 1.4:1: it's a quiet background shape, and the played/unplayed edge above is what carries the position.

`control.face` is only 1.1:1 against the playback bar. The black buttons are meant to sit low and read by their border, bevel and icon, like a DualShock 2's face buttons.

---

## 9. Recommended rename

In the dark set, `accent.teal` and `accent.tealText` hold blues. Before the dark theme ships, rename them by role in **both** token files and every XAML and C# reference:

- `accent.teal` → `accent.playback`
- `accent.tealText` → `accent.playbackText`

…and update `CLAUDE.md`'s rule "Teal means playback" to "The playback color (`accent.playback`: teal in Light, blue in Dark) means playback". If the rename is skipped, the keys still work; only the names mislead.

**Done** in build session 1 (2026-10-14, the user's go-ahead): both token files, `Chrome.xaml`, the tests, both specs, `design/progress.md` and `CLAUDE.md`'s rule. The tables above use the new names.

---

## 10. Notes

- **PS1 spec fix:** `../theme-spec.md` §3 lists `slider.volumeFill` as `#3F3D3B`, but `../theme-tokens.json` (what the app loads) has `#75726E`, which is also what the app shows. The dark value was chosen against the JSON. Correct the PS1 table. **Done** in build session 3 (2026-10-15).
- **Changes from the brief's starting palette:** `text.muted` `#868D99` → `#8C93A0` (it was 4.3:1 on selected rows) and `text.placeholder` `#6E7480` → `#848B98` (it was 3.6:1). Everything else in the brief's palette is used as proposed.

---

## 11. Verifying

- **Parity test** (AudioFool.Core.Tests): both token files parse with `ThemeTokens.Parse`, have the same set of keys, and every value outside `color.*` and `shadow.*` is identical. This is what keeps "same layout, different colors" true as either file changes.
- **ThemeLab** renders of the main window and every dialog in both themes, at 100%, 125% and 150% scaling.
- **Switch live** with the button and with Ctrl+T while a song plays and a dialog is open. Everything changes at once, and nothing else changes.
- **Persistence:** close in Dark → reopens Dark; close in Light → reopens Light; remove `Theme` from `settings.json` → opens Dark.
- As always: no driving the mouse or keyboard to test (UI Automation and `PrintWindow` only).

**All done** in the three build sessions (2026-10-14 and 15): the parity tests; every window, dialog, prompt, menu and tooltip in both themes at 100%, 125% and 150%, with no colour from the other theme anywhere; a live switch identical to a fresh start for each of them, and with music playing and dialogs open; persistence in the installed app. The §8 figures were checked against the colours the app actually uses. Numbers in `../progress.md`, PS2 Dark session notes; renders of both themes in `screenshots/`.
