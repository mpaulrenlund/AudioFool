# AudioFool — PS1 Theme Spec

This is the source of truth for AudioFool's PlayStation 1–inspired theme. Every value and behavior below was settled during design review. When this file and the mockup disagree, **this file wins**.

Companion files in this folder:

- `theme-tokens.json` — the same colors, sizes, and spacing in machine-readable form. Load these into the app's central theme instead of hard-coding values.
- `mockup/AudioFool-mockup.dc.html` — source of the design mockup. Useful for exact icon shapes (SVG paths) and structure. It was made in a design tool and **will not run on its own**; read it, don't execute it. The `{{...}}` placeholders and the script block are mockup scaffolding, not app logic.
- `screenshots/` — exported images of the mockup for visual comparison.
- `progress.md` — what's been built so far.
- `session-prompts.md` — kickoff messages for each implementation session.

---

## 1. Design principles

- **Light console gray throughout.** No dark title bar or dark playback bar. Surfaces are the light gray of the original PS1 console.
- **Color has a job.** Teal means playback. Blue means artist names. Yellow and green mean off/on status. Shuffle blue and repeat red are the remaining nods to the controller's face buttons. Don't introduce color outside these roles.
- **Squarer corners.** 4 px on surfaces, 3 px on row highlights. The round transport buttons are the deliberate exception (they echo controller face buttons).
- **Quiet chrome, clear content.** Headers are small, gray, and uppercase. Selection is a light-mid gray, not a dark fill.

---

## 2. Window and layout

Designed at **1720 × 1080** (close to the current window, 1718 × 1256). The layout is flexible: extra width goes to the Songs panel and the seekbar; extra height goes to the lists.

From top to bottom:

| Region | Height | Notes |
|---|---|---|
| Title bar | 48 px | Window background color, no border |
| Panels row | fills remaining space | 0 px top margin, 12 px left/right margin, 6 px bottom margin |
| Playback bar | 104 px | 12 px left/right margin, 8 px bottom margin |
| Status bar | 36 px | 12 px left/right padding |

Panels row, left to right (outer widths include the 1 px border):

| Panel | Outer width |
|---|---|
| Artists | 212 px (210 content + border) |
| Albums | 292 px (290 content + border) |
| Songs | fills the rest |

Gap between panels: **6 px**.

### Alignment rules (implement as calculations, not fixed numbers)

These keep things lined up if panel widths change later.

1. **Logo** is horizontally centered over the "ARTISTS" header label. At current sizes, the logo's left edge sits 32 px from the window edge (logo center ≈ 52 px).
2. **Search box** starts 20 px after the logo and **ends exactly at the Albums panel's right outer edge** (currently 522 px from the window edge → search width 430 px).
3. **Playback controls** start where the Songs panel starts: the Previous button's left edge lines up with the Songs panel's outer left edge (currently 528 px from the window edge). Everything from Previous rightwards in the playback bar sits under the Songs panel; the now-playing art and text sit under Artists and Albums. (Until 2026-10-02 this rule lined Shuffle up with the song table's # column, 545 px; the user moved the controls left twice, and moved Shuffle to the end of the cluster.)

---

## 3. Colors

All colors live in the central theme (`theme-tokens.json`). Names below are the token paths under `color` in that file (`panel.bg` is `color.panel.bg`), and the app uses the full path as the resource name.

### Surfaces

| Token | Value | Used for |
|---|---|---|
| `window.bg` | `#BAB7B3` | Window, title bar, status bar |
| `panel.bg` | `#CAC7C3` | Artists/Albums/Songs panels, playback bar, search box at rest |
| `panel.border` | `#A5A29D` | Panel, playback bar, and search box borders (1 px) |
| `panel.divider` | `#B3B0AB` | Line under panel headers and the album header |
| `panel.highlight` | `rgba(255,255,255,0.45)` | 1 px inset highlight along the top inside edge of panels |

### Text

| Token | Value | Used for |
|---|---|---|
| `text.heading` | `#1C1B19` | Album title |
| `text.primary` | `#22211F` | Artist names, album titles, song titles, now-playing song title |
| `text.secondary` | `#3E3B38` | Album details (year/tracks/duration), song table columns other than Song, sublines on selected rows |
| `text.muted` | `#4E4B48` | Headers, album counts, album meta, track numbers, time labels, format line, output readout, neutral icons |
| `text.placeholder` | `#5A5754` | Search placeholder |
| `text.status` | `#33312F` | Status bar text and icons |

### Accents

| Token | Value | Used for |
|---|---|---|
| `accent.teal` | `#15786C` | Play/pause icon, seekbar fill, now-playing triangle, Last.fm dot, Bit-Perfect "on" dot, search focus ring |
| `accent.tealText` | `#0C5A51` | Now-playing row text; keyboard focus ring |
| `accent.blue` | `#00519A` | Artist names (album header and playback bar). Deeper version of PlayStation blue chosen for readability (≈4.7:1). |

### States

| Token | Value | Used for |
|---|---|---|
| `state.selectionBg` | `#BAB7B2` | Selected artist, album, and song rows |
| `state.hoverOverlay` | `rgba(0,0,0,0.035)` | Laid over any list row on hover |
| `state.nowPlayingBg` | `#C3CDC9` | Background of the currently playing song row |
| `state.searchFocusBg` | `#D9D6D2` | Search box background while focused |

### Transport controls

| Token | Value | Used for |
|---|---|---|
| `control.face` | `#E4E1DD` | Round button face |
| `control.faceBorder` | `#A9A6A1` | Round button border (1 px) |
| `control.iconNeutral` | `#3F3D3B` | Previous/Next icons |
| `control.iconOff` | `#75726E` | Shuffle/Repeat icons when off |
| `control.shuffleOn` | `#2F5DB8` | Shuffle icon when on |
| `control.repeatOn` | `#C62F37` | Repeat icon (and "1" badge) when on |
| `slider.track` | `#ADAAA5` | Seekbar and volume track |
| `slider.seekFill` | `#15786C` | Seekbar played portion |
| `slider.volumeFill` | `#3F3D3B` | Volume filled portion |
| `slider.thumb` | `#F7F6F4` | Slider handle |
| `slider.thumbBorder` | `#8F8C88` | Slider handle border |

### Title bar window buttons (always colored)

| Token | Value | Used for |
|---|---|---|
| `window.minimize` | `#FFDA00` | Minimize icon (PlayStation yellow, brightened) |
| `window.maximize` | `#006FCD` | Maximize icon (PlayStation blue) |
| `window.close` | `#E60012` | Close icon (PlayStation red) |
| `window.buttonHover` | `rgba(0,0,0,0.06)` | Minimize/Maximize hover background |
| `window.closeHover` | `#C42B1C` | Close hover background (icon turns white) |

### Status bar

| Token | Value | Used for |
|---|---|---|
| `status.chipBg` | `#C6C3BF` | Refresh, Last.fm, Bit-Perfect buttons |
| `status.chipBorder` | `#9C9995` | Their borders |
| `status.bitPerfectOff` | `#FFDA00` | Bit-Perfect dot when off |
| `status.bitPerfectOn` | `#15786C` | Bit-Perfect dot when on (same green as Last.fm) |
| `status.dotRing` | `rgba(0,0,0,0.25)` | Faint 1 px inner ring on the Bit-Perfect dot so yellow stays visible |

### Album art

| Token | Value | Used for |
|---|---|---|
| `art.border` | `#8F8C88` | Album header art and now-playing art border |
| `art.thumbBorder` | `#9C9995` | Album list thumbnails |
| `art.thumbBorderSelected` | `#6E6B67` | Thumbnail border on the selected album |

**Pink is not used anywhere.** (The pause button was pink in an earlier draft; it's now teal.)

---

## 4. Typography

Font: **Segoe UI Variable Text**, falling back to **Segoe UI**.

| Element | Size | Weight | Color | Other |
|---|---|---|---|---|
| Panel headers (ARTISTS, ALBUMS) | 11 px | 600 | `text.muted` | Uppercase, letter-spacing 0.12 em |
| Song table headers | 11 px | 600 | `text.muted` | Uppercase, letter-spacing 0.06 em |
| Artist name | 13.5 px | 400 | `text.primary` | |
| Artist album count | 11.5 px | 400 | `text.muted` | "1 album" / "N albums" |
| Album title (list) | 13.5 px | 400 | `text.primary` | Line height 1.3, max 2 lines |
| Album meta (list) | 12 px | 400 | `text.muted` | "1998 · 21 tracks" |
| Album title (header) | 30 px | 600 | `text.heading` | Line height 1.15 |
| Album details (header) | 14 px | 400 | `text.secondary` | Line height 1.4; artist line is 600 in `accent.blue` |
| Song title (table) | 13.5 px | 400 | `text.primary` | |
| Other table columns | 13 px | 400 | `text.secondary` | |
| Track number | 12.5 px | 400 | `text.muted` | Tabular figures |
| Now-playing song title | 17 px | 600 | `text.primary` | |
| Now-playing artist | 14 px | 600 | `accent.blue` | |
| Now-playing format line | 13 px | 400 | `text.muted` | "MP3 · 320 kbps · 44.1 kHz" |
| Time labels (seekbar) | 12 px | 400 | `text.muted` | Tabular figures |
| Output readout | 11.5 px | 400 | `text.muted` | |
| Search text / placeholder | 13 px | 400 | `text.primary` / `text.placeholder` | Placeholder: "Search" |
| Status bar | 12 px | 400 | `text.status` | |

Use tabular (fixed-width) figures for track numbers, times, and numeric columns so they line up.

---

## 5. Corner radii

| Element | Radius |
|---|---|
| Panels, playback bar, search box | 4 px |
| Status bar buttons (Refresh, Last.fm, Bit-Perfect) | 4 px |
| Row highlights (artist, album, song) | 3 px |
| Slider tracks | fully rounded (3 px on a 6 px track) |
| Transport buttons | fully round (circles) |
| Album art and thumbnails | 0 (square) |

---

## 6. Components

### 6.1 Title bar (48 px)

- Background `window.bg`. No bottom border.
- **Logo:** the existing PlayStation logo asset, in a 40 × 36 px slot, positioned per alignment rule 1.
- **Search box:** 30 px tall, width per alignment rule 2, 4 px radius, 1 px `panel.border`, 12 px horizontal padding.
  - At rest: background `panel.bg`, subtle inset shadow (`inset 0 1px 2px rgba(0,0,0,0.15)`).
  - Focused: background `state.searchFocusBg` plus a 2 px `accent.teal` ring.
  - 16 px magnifying-glass icon in `text.muted`, 10 px gap before the text.
- **Window buttons:** Minimize, Maximize, Close at the far right. Each 46 px wide and full title-bar height. Icons 18 px, stroke ≈1.95 px (2.6 on a 24-unit grid), round line caps. Colors per §3. Hover: Minimize/Maximize get `window.buttonHover`; Close fills `window.closeHover` with a white icon.

### 6.2 Panels (shared)

- Background `panel.bg`, 1 px `panel.border`, 4 px radius, 1 px inset top highlight.
- **Header:** 28 px tall, 14 px horizontal padding, 1 px `panel.divider` bottom border. Label only — **no counts** in the Artists or Albums headers.
- List area: 6 px padding.
- **No visible scrollbars anywhere.** Lists still scroll with the mouse wheel, trackpad, and keyboard. Hide the scrollbar; don't disable scrolling.

### 6.3 Artists panel

- Rows 42 px tall, 10 px horizontal padding, two lines stacked with 1 px between:
  - Line 1: artist name (truncate with "…" if too long).
  - Line 2: "1 album" / "N albums".
- Selected: `state.selectionBg`; subline switches to `text.secondary`.
- Hover: `state.hoverOverlay`.
- Sorting ignores a leading "The" (e.g., "The Mercury Tree" sorts under M) — keep the current app's behavior.

### 6.4 Albums panel

- Rows 64 px tall: 4 px vertical / 8 px horizontal padding, 56 px square thumbnail, 12 px gap, then two lines (title, meta) with 3 px between. No gap between rows.
- **Title wrapping:** wrap to a maximum of **two lines**, then end with "…". Every row stays the same height.
- **Tooltip:** show the full title on hover **only when it's actually cut off**. Titles that fit get no tooltip. *This replaces the current app's single-line truncation with an always-on tooltip — remove that old tooltip.*
- Album titles are regular weight (400), matching artist names.
- Selected: `state.selectionBg`; meta switches to `text.secondary`; thumbnail border `art.thumbBorderSelected`.

### 6.5 Album header (top of Songs panel)

- 22 px vertical / 24 px horizontal padding, 1 px `panel.divider` bottom border.
- 144 px square art with `art.border` and shadow `0 4px 10px rgba(0,0,0,0.25)`, then 24 px gap.
- Right column, top-aligned, 8 px between title and details:
  - Album title (30 px).
  - Four stacked lines, 3 px apart, each 14 px: **Artist** (600, `accent.blue`) / **Year** / **"N tracks"** / **Duration** formatted like "1 hr 6 min" (use "42 min" under an hour).
- No Play/Shuffle buttons and no format tags in the header.

### 6.6 Song table

**Columns (10), left to right**, with 12 px between columns:

| Column | Width | Align |
|---|---|---|
| # | 27 px | right (see below) |
| Song | flexible, 1.05 share | left |
| Artist | 88 px | left |
| Album | flexible, 1.0 share | left |
| Time | 44 px | right |
| Disc | 36 px | center |
| Kind | 40 px | left |
| Bitrate | 68 px | right |
| Bit Depth | 70 px | center |
| Sample Rate | 82 px | right |

- Header row 32 px, 16 px horizontal padding (lines up with row content), 1 px `panel.divider` bottom border.
- Rows 28 px, 8 px horizontal padding inside a list area with 6 px / 8 px padding.
- Long text truncates with "…" and **no tooltip**. Column widths will be adjusted by hand if a title is too long.
- Bit Depth shows "—" when not applicable (e.g., MP3).

**# column structure:** an 8 px slot for the now-playing triangle, a 3 px gap, then the track number right-aligned in a 16 px space (fits two digits). For albums with 100+ tracks, widen the number space by about 7 px.

**Now-playing triangle:** small right-pointing triangle in `accent.teal` in the 8 px slot of the playing row only. **Solid while playing, outlined while paused.** Every row keeps its track number visible.

**Row states:**

| State | Background | Text |
|---|---|---|
| Normal | transparent | Song `text.primary`, others `text.secondary`, number `text.muted` |
| Hover | `state.hoverOverlay` | unchanged |
| Selected | `state.selectionBg` | number switches to `text.secondary` |
| Now playing | `state.nowPlayingBg` | all text `accent.tealText`, weight 600 |
| Now playing + selected | `state.nowPlayingBg` | same as now playing |

**Interaction:** single-click selects a row; **double-click plays** it. Prevent double-click from highlighting text.

### 6.7 Playback bar (104 px)

A panel-styled bar: `panel.bg`, 1 px `panel.border`, 4 px radius, inset top highlight, 24 px horizontal padding. Four zones with 40 px between them, all vertically centered:

**Zone 1 — Now playing** (width set by alignment rule 3; currently 451 px):
- 88 px square art, `art.border`, shadow `0 0 6px rgba(0,0,0,0.25)` (even on all sides — no offset).
- 16 px gap, then three lines, 3 px apart: song title (17 px) / artist (14 px, blue) / format line (13 px, "MP3 · 320 kbps · 44.1 kHz" with middle dots).

**Zone 2 — Transport buttons**: Previous, Play/Pause and Next in a row, 16 px apart, then **Repeat over Shuffle** as a vertical stack (10 px apart) 16 px after Next, centred on the row's height. This is also the Tab order. (Until 2026-10-02 the row was Shuffle, Previous, Play/Pause, Next, Repeat at 44 px; the user stacked the two mode buttons and made them smaller.)
- All round, face `control.face`, 1 px `control.faceBorder`. Molded look: `inset 0 -3px 0 rgba(0,0,0,0.12)`, `inset 0 1px 0 rgba(255,255,255,0.8)`, `0 1px 3px rgba(0,0,0,0.25)`.
- Sizes: Previous and Next 44 px; Play/Pause 60 px (shadow `inset 0 -4px 0 …` and `0 2px 5px rgba(0,0,0,0.28)`); **Shuffle and Repeat 32 px** (`playbackBar.modeButtonSize`), same molded look.
- Icons: Previous/Next 20 px (`control.iconNeutral`), Play/Pause 26 px, Shuffle 15 px, Repeat 16 px.
- **Play/Pause is always `accent.teal`** — triangle when paused, two bars when playing.
- **Shuffle:** `control.iconOff` when off, `control.shuffleOn` when on.
- **Repeat** cycles **off → all → one** on each click: `control.iconOff` when off, `control.repeatOn` for all and one. Repeat-one adds a badge on the button's top-right corner: a 13 px `control.repeatOn` disc overhanging the button by 2 px, holding a "1" (9 px, 700, `control.face`). (A "1" inside the 16 px icon was unreadable at this size.)
- No status lights under the buttons.
- Hover: slightly brighter. Pressed: nudges down 1 px.

**Zone 3 — Seekbar** (fills remaining width):
- Elapsed time (12 px, right-aligned in a 36 px space), 14 px gap, track, 14 px gap, total time.
- Track 6 px tall, `slider.track`, subtle inset shadow, fully rounded. Played portion `slider.seekFill`.
- Handle 14 px circle, `slider.thumb` with 1 px `slider.thumbBorder`, small drop shadow.
- Click or drag anywhere on the track to seek.

**Zone 4 — Volume** (240 px):
- Speaker button (44 px clickable area, 17 px icon, `text.muted`, no face), 8 px gap, volume track.
- Volume track is the **same height and style as the seekbar** and sits on the **same horizontal line** as the seekbar. Filled portion `slider.volumeFill`; handle 12 px.
- **Output readout** sits just under the volume slider, right-aligned, without pushing the slider up. Text:
  - Shared mode: "Shared · 96 kHz / 32-bit (resampled)" — include "(resampled)" when the output rate differs from the file's rate.
  - Bit-perfect: "Exclusive · 44.1 kHz · bit-perfect".
- **Mute:** clicking the speaker mutes (icon gets an "×", slider shows zero). Clicking again restores the **previous volume**. Dragging the slider while muted unmutes and sets the new level.

### 6.8 Status bar (36 px)

Background `window.bg`, 12 px horizontal padding, 12 px text in `text.status`.

**Left, in order:**
1. **Refresh button:** 26 × 26 px, 4 px radius, `status.chipBg`, 1 px `status.chipBorder`, 14 px circular-arrow icon, 6 px extra space after it. Rescans the library.
2. Small 7 px `accent.teal` dot.
3. "499 artists · 2,376 albums · 26,795 tracks · [size] GB" (all live values).

**Library size:** total size of all library files, using **1 GB = 1,024³ bytes** (matches Windows File Explorer), rounded to the nearest whole GB, with a thousands separator above 999 ("1,204 GB"). Recalculate when Refresh runs.

**Right, 10 px apart:**
- **Last.fm:** 26 px tall chip, 4 px radius, `status.chipBg`/`status.chipBorder`, 7 px `accent.teal` dot + "Last.fm".
- **Bit-Perfect:** same chip, 7 px dot + "Bit-Perfect". **No "On"/"Off" text** — the dot shows state: `status.bitPerfectOff` when off, `status.bitPerfectOn` when on, with a faint `status.dotRing`. Screen readers should still announce on/off (toggle-button semantics).

---

## 7. Behavior summary

| Behavior | Rule |
|---|---|
| Play a song | Double-click its row |
| Select a song | Single click |
| Previous | If more than 3 s into the song, restart it; otherwise go to the previous song |
| Next | Next song (random when Shuffle is on) |
| End of album | Repeat off: stop. Repeat all: wrap to first song. Repeat one: replay the same song |
| Mute | Toggle; unmute restores previous volume |
| Bit-Perfect | Toggle; updates the output readout and the dot color |
| Refresh | Rescan library; recalculate counts and size |
| Scrolling | Hidden scrollbars; wheel/trackpad/keyboard still scroll |
| Album title overflow | Two lines max with "…"; tooltip only when truncated |
| Song table overflow | "…", no tooltip |

---

## 8. Accessibility

- Visible keyboard focus: 2 px `accent.tealText` outline, 2 px offset, on all buttons and rows. Sliders show the focus outline around the track.
- Every icon-only button has an accessible name (Shuffle, Previous track, Play/Pause, Next track, Repeat: off/all/one, Mute/Unmute, Refresh library, Minimize, Maximize, Close).
- Toggle buttons (Shuffle, Mute, Bit-Perfect) expose their pressed state.
- Contrast: text colors above meet ~4.5:1 on their backgrounds; icon colors meet ~3:1. The title-bar yellow is a deliberate stylistic exception.
- Test at Windows display scaling of 125% and 150%. Thin borders and the ≈1.95 px window-button strokes can blur — snap to whole pixels where the framework allows.

---

## 9. What's changing from the current app

Use this as a checklist so nothing from the old theme lingers.

- **Title bar:** logo centered over ARTISTS; search box restyled (panel gray, 4 px corners, focus brightening) and resized to end at the Albums panel; window buttons always colored yellow/blue/red.
- **Panels:** 4 px corners, 6 px gaps, 28 px headers with no counts.
- **Artists:** narrower panel (212 px outer), 42 px two-line rows.
- **Albums:** titles wrap to two lines instead of truncating; old always-on title tooltip removed; titles regular weight; light-mid gray selection.
- **Album header:** adds a track-count line; artist name in blue; duration as "1 hr 6 min" (was "1:06:27").
- **Song table:** same ten columns; tight triangle gutter beside the number (number always visible); distinct selected vs. now-playing styles.
- **Playback bar:** light panel style; round face-style buttons; Play/Pause always teal; shuffle/repeat colored when on; repeat-one "1" badge; larger now-playing art (88 px) and text; controls aligned under the Songs panel; volume aligned with the seekbar at the same thickness.
- **Status bar:** library size added; Bit-Perfect uses a colored dot instead of "OFF" text; buttons have 4 px corners.
- **Removed:** dark title/playback bars, spinning disc, status lights under transport buttons, A–Z jump rail, pink pause color, format tags and Play/Shuffle buttons in the album header.

---

## 10. Assets

- **Logo:** keep the existing PlayStation logo asset (personal-use app). Fits a 40 × 36 px slot.
- **Album art:** from the library as today. The mockup's striped "[ALBUM ART]" boxes are placeholders.
- **Icons:** SVG paths for every icon are in the mockup source (search for `<svg`). All are drawn on a 24-unit grid except the now-playing triangle.
