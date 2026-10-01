# AudioFool PS1 Theme — Progress

Update this at the end of every session: tick off what's finished, note anything left open, and record any decision that changed the spec.

## Sessions

- [x] **1. Setup and theme foundation** (spec §2–5)
  - [x] ~~Create a branch for the new theme~~. Not done, by decision: the work happens on `main` (see Decisions)
  - [x] Build the central theme from `theme-tokens.json` (colors, type, radii, spacing, shadows)
  - [x] Confirm existing screens can read from it without hard-coded values
- [ ] **2. Title bar and panels** (spec §6.1–6.4)
  - [ ] Title bar: logo alignment, search box (rest/focus), colored window buttons with hover states
  - [ ] Panel shell: widths, gaps, corners, 28 px headers without counts, hidden scrollbars
  - [ ] Artists: 42 px two-line rows, selection and hover
  - [ ] Albums: 64 px rows, two-line clamp, tooltip only when truncated, old tooltip removed
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
| `shadow.*` (inset layers) | `<path>.inset1` brush, plus `.inset1.x` / `.y` / `.blur` numbers | drawn by the template as an overlay |
| other numbers | `double` | `Height="{theme:Token artists.rowHeight}"`. `{theme:Token}` also converts a number to Thickness, CornerRadius, GridLength or DataGridLength, and `"right"` to an alignment, to suit the property |
| paddings and margins | built from numbers | `Padding="{theme:Thickness X=albums.rowPaddingX, Y=albums.rowPaddingY}"` (`All`, `X`, `Y`, `Left`, `Top`, `Right`, `Bottom`; a side may be a literal `0`) |
| `font.family` | `FontFamily` with fallbacks | `{DynamicResource font.family}` |
| other strings, flags | `string`, `bool` | e.g. `songTable.columns.disc.align`, `type.panelHeader.uppercase` |

Namespace: `xmlns:theme="clr-namespace:AudioFool.Theming"`. A misspelt name fails when the XAML loads, naming the token. It doesn't silently fall back.

**One limit.** In a `Style` setter, WPF resolves the setter's `Property` only after its `Value`, so `{theme:Token}` can't see the target type there and passes the raw value through. Radii are already `CornerRadius` for this reason. For a margin or padding in a setter, use `{theme:Thickness}`.

C# code can read the parsed tokens through `TokenResources.Current` (an `AudioFool.Core.Theme.ThemeTokens`), for the alignment rules that are calculations (spec §2).

**To check it:** `ThemeLab.exe --window tokens --out tokens.png`. It looks up all 332 resources from the real MainWindow, parses a XAML sample covering every usage above (in elements, a style and a template), reports which font files WPF resolves, and draws a specimen sheet of every colour, text style, radius and shadow. It exits non-zero if anything is wrong. The token file's parsing is covered by `ThemeTokensTests` (27 tests).

## Open items

- Library size: implement the calculation (spec §6.8). The mockup shows "[size] GB" as a placeholder.
- **The dialogs aren't in the spec.** Tag editor, Statistics, Last.fm, art search and the art viewer all still use the old PS1 resources. When the old theme files are deleted (end of session 5) they need a look, and the spec is silent on them. Ask the user then.

## Notes for later sessions

- **Hard-coded values still in the screens** (literal sizes, margins, paddings; no literal colours anywhere): MainWindow 52, TagEditWindow 64, StatisticsWindow 30, LastFmWindow 20, ArtSearchWindow 17, ArtWindow 9. Each goes as its region is restyled.
- **Letter spacing** (`type.panelHeader` 0.12 em, `type.tableHeader` 0.06 em): WPF has no tracking. The existing `Formatting/LetterSpacing.cs` inserts a fixed hair space between letters, which isn't an em value. Session 2 needs it reworked to space by `letterSpacingEm × FontSize`, or a small control that draws with spacing.
- **Uppercase**: a style can't change text case. Write the labels in capitals, or use a converter.
- **Inset shadows** have no WPF equivalent. `panelHighlight` is a 1 px line inside the top edge. `searchInset` and `sliderTrackInset` are a short dark-to-clear gradient along the top. The buttons' `inset 0 -3px` is a crescent along the bottom of the circle, drawn as the circle minus itself shifted up.
- **Shadow blur** maps CSS blur straight onto WPF `BlurRadius`. They aren't the same curve, so compare with the screenshot by eye and adjust the JSON if needed.
- **Font verified**: WPF resolves "Segoe UI Variable Text" to `SEGUIVAR.TTF`, and weights 400 and 600 are real faces (600 is "Semibold", not synthesised). Weight 700, used for the repeat-one badge, hasn't been checked.
- **Albums tooltip**: since 2026-09-30 it already shows only when the title is cut off (`AlbumRow_ToolTipOpening` in `MainWindow.xaml.cs`), but it measures a single line. With the two-line clamp it has to test whether the clamped text was trimmed instead.
- **`type.albumTitle`** clamps with `MaxHeight` = 2 × line height (17.55 px), wrapping and trimming. Verified on the specimen: two lines, then "…".

## Decisions and changes

_Record anything decided during implementation that isn't in the spec, or that changes it._

- **No branch** (2026-10-01, the user's call: all five sessions are planned for one day). The work is on `main`, and screens move to the new theme one session at a time. `CLAUDE.md` was updated to match.
- **Now-playing art shadow is even on all sides**: `shadow.nowPlayingArt` is `0 0 6px rgba(0,0,0,0.25)`. Spec §6.7 said both `0 2px 6px` and "no offset"; the user chose no offset.
- **Spec token names now match the JSON paths**: `state.selectionBg`, `state.hoverOverlay`, `state.nowPlayingBg`, `state.searchFocusBg`, `status.chipBg`, `status.chipBorder`, `art.thumbBorder`, `art.thumbBorderSelected`. They were `selection.bg`, `hover.overlay` and so on.
- **`font.tabularFiguresFor` names type styles**: `trackNumber`, `timeLabel`, `tableCell`. It was `trackNumber`, `times`, `numericColumns`, which named nothing. Tabular figures only affect digits, so `tableCell`'s text columns are unchanged.
- **The tokens are read at runtime rather than copied into a XAML file**, so the JSON can't drift from the app.

## Session notes

_Add a short entry after each session: date, what was done, anything left open._

- **2026-10-01, session 1.** Built the central theme: `AudioFool.Core/Theme/ThemeTokens.cs` (parses the JSON: hex and `rgba()` colours, CSS shadows, columns by id, type styles with their colour references checked), `src/AudioFool/Theming/TokenResources.cs` (turns it into brushes, styles, radii and effects) and `Theming/TokenExtensions.cs` (`{theme:Token}`, `{theme:Thickness}`). Loaded first in `ThemeService.Apply()`. No screen was restyled. Verified that seven ThemeLab renders (main, paused, scanning, focused album row, tag editor, Statistics, Last.fm) are **0 pixels different** from before, with 0 magenta. `--window tokens` passes. 363 tests pass (336 + 27). Installed; the app should look exactly as it did.
