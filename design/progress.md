# AudioFool PS1 Theme — Progress

Update this at the end of every session: tick off what's finished, note anything left open, and record any decision that changed the spec.

## Sessions

- [x] **1. Setup and theme foundation** (spec §2–5)
  - [x] ~~Create a branch for the new theme~~. Not done, by decision: the work happens on `main` (see Decisions)
  - [x] Build the central theme from `theme-tokens.json` (colors, type, radii, spacing, shadows)
  - [x] Confirm existing screens can read from it without hard-coded values
- [x] **2. Title bar and panels** (spec §6.1–6.4)
  - [x] Title bar: logo alignment, search box (rest/focus), colored window buttons with hover states
  - [x] Panel shell: widths, gaps, corners, 28 px headers without counts, hidden scrollbars
  - [x] Artists: 42 px two-line rows, selection and hover
  - [x] Albums: 64 px rows, two-line clamp, tooltip only when truncated, old tooltip removed
- [ ] **3. Album header and song table** (spec §6.5–6.6)
  - [ ] Album header: stacked details, blue artist, "1 hr 6 min" duration
  - [ ] Ten columns at spec widths and alignments
  - [ ] # column with triangle slot (solid playing / outlined paused)
  - [ ] Row states: normal, hover, selected, now playing
  - [ ] Single-click select, double-click play
- [ ] **4. Playback bar and status bar** (spec §6.7–6.8)
  - [ ] Now-playing zone (88 px art, text sizes) and controls aligned under the song list
  - [ ] Round transport buttons; teal play/pause; shuffle/repeat states; repeat-one badge
  - [ ] Seekbar and volume: same track style, same line; output readout under volume
  - [ ] Mute restores previous volume
  - [ ] Status bar: refresh, library totals + size in GB, Last.fm dot, Bit-Perfect dot (no text)
- [ ] **5. Polish and testing** (spec §7–9)
  - [ ] Compare every region against `screenshots/`
  - [ ] Keyboard focus outlines and accessible names
  - [ ] Display scaling at 125% and 150%
  - [ ] Edge cases: very long names, 100+ track album, muted, Bit-Perfect on, empty library
  - [ ] Walk through the "What's changing" checklist (spec §9) to catch anything left over from the old theme

## How screens use the central theme

`theme-tokens.json` is embedded in the exe and read at startup by `src/AudioFool/Theming/TokenResources.cs`, called first thing in `ThemeService.Apply()`. **It is the only copy of every value**: change the JSON and rebuild. Never copy a value into XAML.

Every resource is named by its **JSON path**: `color.panel.bg`, `albums.rowHeight`, `songTable.columns.time.width`. Columns are addressed by their `id`, not their position.

| Token | Resource | Use in XAML |
|---|---|---|
| `color.*` | frozen `SolidColorBrush`; the raw `Color` is `<path>.value` | `Background="{DynamicResource color.panel.bg}"` |
| `type.*` | a `TextBlock` style: font, size, weight, colour, line height, tabular figures, and a two-line clamp for `maxLines` | `Style="{DynamicResource type.albumTitle}"` |
| `radius.*` | `CornerRadius`; the number is `<path>.value` | `CornerRadius="{DynamicResource radius.surface}"` |
| `shadow.*` (outer layer) | frozen `DropShadowEffect` | `Effect="{DynamicResource shadow.headerArt}"` |
| `shadow.*` (inset layers) | `<path>.inset1` brush, plus `.inset1.x` / `.y` / `.blur` numbers; `.inset1.fade` (the colour fading to clear, top to bottom) and `.inset1.depth` (offset + blur) | drawn by the template as an overlay. A top-edge inset is a `.depth`-tall border filled with `.fade` |
| other numbers | `double` | `Height="{theme:Token artists.rowHeight}"`. `{theme:Token}` also converts a number to Thickness, CornerRadius, GridLength or DataGridLength, and `"right"` to an alignment, to suit the property |
| paddings and margins | built from numbers | `Padding="{theme:Thickness X=albums.rowPaddingX, Y=albums.rowPaddingY}"` (`All`, `X`, `Y`, `Left`, `Top`, `Right`, `Bottom`; a side may be a literal `0`, or `-token` for an outset) |
| `font.family` | `FontFamily` with fallbacks | `{DynamicResource font.family}` |
| other strings, flags | `string`, `bool` | e.g. `songTable.columns.disc.align`, `type.panelHeader.uppercase` |

Namespace: `xmlns:theme="clr-namespace:AudioFool.Theming"`. A misspelt name fails when the XAML loads, naming the token. It doesn't silently fall back.

**One limit.** In a `Style` setter, WPF resolves the setter's `Property` only after its `Value`, so `{theme:Token}` can't see the target type there and passes the raw value through. Radii are already `CornerRadius` for this reason. For a margin or padding in a setter, use `{theme:Thickness}`.

**Components** built from the tokens live in `src/AudioFool/Theming/Chrome.xaml`, merged by `ThemeService.Apply()` after the old dictionaries, with keys starting `theme.`: `theme.panel` / `theme.panel.songs`, `theme.browserRowTemplate`, `theme.searchBox`, `theme.windowButton`. Letter spacing in em is `fmt:LetterSpacing.Em` (`Formatting/LetterSpacing.cs`), set from `type.*.letterSpacingEm`.

C# code can read the parsed tokens through `TokenResources.Current` (an `AudioFool.Core.Theme.ThemeTokens`), for the alignment rules that are calculations (spec §2).

**To check it:** `ThemeLab.exe --window tokens --out tokens.png`. It looks up all 350 resources from the real MainWindow, parses a XAML sample covering every usage above (in elements, a style and a template), reports which font files WPF resolves, and draws a specimen sheet of every colour, text style, radius and shadow. It exits non-zero if anything is wrong. The token file's parsing is covered by `ThemeTokensTests` (27 tests).

## Open items

- Library size: implement the calculation (spec §6.8). The mockup shows "[size] GB" as a placeholder.
- **The dialogs aren't in the spec.** Tag editor, Statistics, Last.fm, art search and the art viewer all still use the old PS1 resources. When the old theme files are deleted (end of session 5) they need a look, and the spec is silent on them. Ask the user then.

## Notes for later sessions

- **Hard-coded values still in the screens** (literal sizes, margins, paddings; no literal colours anywhere): MainWindow 52, TagEditWindow 64, StatisticsWindow 30, LastFmWindow 20, ArtSearchWindow 17, ArtWindow 9. Each goes as its region is restyled.
- **Letter spacing** is done: `LetterSpacing.Em` puts an empty inline element of `Em × FontSize` between letters (none after the last, unlike CSS). The table headers (0.06 em) can use it in session 3. Text decorations skip the gaps, so the ARTISTS hover underline is a 1 px rule under the label instead.
- **Uppercase**: a style can't change text case. Write the labels in capitals, or use a converter.
- **Inset shadows** have no WPF equivalent. `panelHighlight` is a 1 px line inside the top edge. `searchInset` and `sliderTrackInset` are a short dark-to-clear gradient along the top. The buttons' `inset 0 -3px` is a crescent along the bottom of the circle, drawn as the circle minus itself shifted up.
- **Shadow blur** maps CSS blur straight onto WPF `BlurRadius`. They aren't the same curve, so compare with the screenshot by eye and adjust the JSON if needed.
- **Font verified**: WPF resolves "Segoe UI Variable Text" to `SEGUIVAR.TTF`, and weights 400 and 600 are real faces (600 is "Semibold", not synthesised). Weight 700, used for the repeat-one badge, hasn't been checked.
- **Albums tooltip** is done: `MainWindow.IsTrimmed` wraps the full title at the block's width and compares its height with the clamped block's. Probed with real `type.albumTitle` blocks at 194 px: one- and two-line titles get no tooltip, a three-line one does.
- **Until session 3**, the inside of the Songs panel (album header, song table) still uses the old resources: the old row banding and header colours sit on the new panel grey. The table's columns are still fitted to their content, so at the new minimum width (see Decisions) the right-hand columns can be cut off. They fit once session 3 sets the spec widths.
- **Until session 4**, the playback bar keeps its old 10 px top margin, so the gap under the panels is 16 px (6 + 10) rather than 6. The old, darker shell (`#A7A4A3`) is also still painted under the playback and status bars: `color.window.bg` covers only the title bar and panels row (a `Border` in MainWindow). Extend it to the whole window in session 4.
- **Old resources no longer used by anything**: `AfBrowserRowTemplate`, `AfRowHoverWash` (and with it `Ps1Motion.xaml`), `AfSearchBox`, `AfBrandMark`, `AfPane`, `AfPaneMarkArtists` / `AfPaneMarkAlbums`. They go with the old files.
- **Literals left in the title bar and panels**, each a framework constant rather than a design value: the logo menu's 20 px header spacer (WPF-UI's cap), `Margin="-2,0,0,0"` on the search box's text host (WPF's caret gutter, measured), the album tooltip's 1000 ms delay, and icon geometry on its 24-unit grid. The no-art album placeholder still uses the old `AfIconSizeMedium`, because the spec has no placeholder.
- **`type.albumTitle`** clamps with `MaxHeight` = 2 × line height (17.55 px), wrapping and trimming. Verified on the specimen: two lines, then "…".

## Decisions and changes

_Record anything decided during implementation that isn't in the spec, or that changes it._

- **No branch** (2026-10-01, the user's call: all five sessions are planned for one day). The work is on `main`, and screens move to the new theme one session at a time. `CLAUDE.md` was updated to match.
- **Now-playing art shadow is even on all sides**: `shadow.nowPlayingArt` is `0 0 6px rgba(0,0,0,0.25)`. Spec §6.7 said both `0 2px 6px` and "no offset"; the user chose no offset.
- **Spec token names now match the JSON paths**: `state.selectionBg`, `state.hoverOverlay`, `state.nowPlayingBg`, `state.searchFocusBg`, `status.chipBg`, `status.chipBorder`, `art.thumbBorder`, `art.thumbBorderSelected`. They were `selection.bg`, `hover.overlay` and so on.
- **`font.tabularFiguresFor` names type styles**: `trackNumber`, `timeLabel`, `tableCell`. It was `trackNumber`, `times`, `numericColumns`, which named nothing. Tabular figures only affect digits, so `tableCell`'s text columns are unchanged.
- **The tokens are read at runtime rather than copied into a XAML file**, so the JSON can't drift from the app.
- **No horizontal scrolling anywhere** (2026-10-01, the user's call: "There shouldn't be a reason to scroll horizontally"). Vertical scrollbars stay hidden as the spec says, and lists still scroll by wheel, trackpad and keyboard. The song table's horizontal bar is disabled, and the code that toggled it is gone.
- **The window's minimum width is calculated** (the user's call). It is the panels at their spec widths plus a Songs panel whose columns all fit: the fixed columns at spec widths, Song and Album at the existing 70 px floor, the 12 px gaps, and the padding and borders. That's **1,277 px** with today's tokens (it was 900). `MainWindow.ApplyMinimumWidth` also gives the Songs column that minimum, so a splitter can't squeeze it.
- **Splitters kept** (the user's call). Artists and Albums start at 212 and 292 px and can be dragged in the 6 px gaps; the logo and search box follow. Their minimums are new tokens, `layout.artistsPanelMinWidth` (140) and `layout.albumsPanelMinWidth` (180), the values the app already used.
- **The ARTISTS header stays the sort toggle** (the user's call), restyled as the plain header. It reads `ARTISTS · RECENT` while sorted by recent, and the logo centres on the word ARTISTS in both modes.
- **Filter chip and search clear button kept** (the user's call). The chip borrows the status-bar chip's tokens (`status.chipBg`, `status.chipBorder`, `radius.chip`) at the search box's height. The clear × is drawn like the magnifier (16 px, `text.muted`) and shows only when there is text.
- **New tokens**: `titleBar.search.iconStrokeOn24Grid` (2, from the mockup's SVG) and `titleBar.search.focusRingWidth` (2, spec §6.1), as well as the two panel minimums above.
- **The restore icon** (shown while maximised) isn't in the mockup. It's drawn as a smaller version of the maximise square, offset down-left, with the corner of a second square behind it, in the same colour and stroke.
- **Logo position.** The rule (centred over ARTISTS) puts the slot at x 31–71, 1 px left of the spec's quoted 32. WPF's ARTISTS label is about 2 px narrower than the mockup's, which also has CSS tracking after its last letter.
- **No focus outline on the Artists and Albums lists themselves** (`FocusVisualStyle` null). Once the lists were inset by their 6 px margin, WPF's list-level focus box showed as a dark rectangle inside the panel after any key press (seen in the user's screenshot of the real app). Row and button focus outlines are still session 5.
- **Row hover is instant**, with no fade. The old theme faded it in (`Ps1Motion.xaml`); the spec doesn't mention motion.

## Session notes

_Add a short entry after each session: date, what was done, anything left open._

- **2026-10-01, session 1.** Built the central theme: `AudioFool.Core/Theme/ThemeTokens.cs` (parses the JSON: hex and `rgba()` colours, CSS shadows, columns by id, type styles with their colour references checked), `src/AudioFool/Theming/TokenResources.cs` (turns it into brushes, styles, radii and effects) and `Theming/TokenExtensions.cs` (`{theme:Token}`, `{theme:Thickness}`). Loaded first in `ThemeService.Apply()`. No screen was restyled. Verified that seven ThemeLab renders (main, paused, scanning, focused album row, tag editor, Statistics, Last.fm) are **0 pixels different** from before, with 0 magenta. `--window tokens` passes. 363 tests pass (336 + 27). Installed; the app should look exactly as it did.
- **2026-10-01, session 2.** Restyled the title bar and panels on the central theme (`Theming/Chrome.xaml`, MainWindow). The window buttons are WPF-UI's own with a new template, so snap layouts on maximise still work. Their hover was checked by invoking WPF-UI's `Hover()` (new `ThemeLab --winhover`; `--type` puts text in the search box). Measured on a 1720 × 1080 ThemeLab render: Artists x 12–223, Albums 230–521, Songs from 528, with 6 px gaps. Header strips are 28 px and rows 42 / 64 px. The search box runs x 91–522, ending on the Albums edge, and typed text and placeholder both start at x 131. 0 magenta. The playback and status bars are **0 pixels different** from before (playing and paused), as are the tag editor, Statistics, Last.fm and the logo menu's drop-down. `--window tokens` passes (350 resources) and 363 tests pass. Installed. Not done: a `PrintWindow` capture of the installed app (it wasn't running, and launching it would take focus), and a check of the new minimum width in a real window (ThemeLab forces its own size).
