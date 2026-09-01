# AudioFool — session handoff

Updated 2026-09-01 after the sixth build session. Read this alongside
`README.md`: the README covers *how the app works*, this covers *where things stand and
how to work on it*.

---

## Where everything lives

| What | Path |
|---|---|
| Source | `C:\MusicPlayer` (solution is `AudioFool.slnx` — the new XML format, not `.sln`) |
| **Installed app** | `%LOCALAPPDATA%\Programs\AudioFool\` ← what the shortcuts launch |
| Publish output | `C:\MusicPlayer\publish\` (a build artifact, *not* what runs) |
| Settings | `%APPDATA%\AudioFool\settings.json` |
| Scan cache | `%LOCALAPPDATA%\AudioFool\library.json` (~13 MB) |
| Native BASS DLLs | `C:\MusicPlayer\lib\bass\x64\` (13 of them) |
| Music library | `D:\Music` — external 4 TB SSD "Marc SSD", ~26,000 tracks, 692 GB |

**The single most important workflow fact:** `dotnet publish` writes to
`C:\MusicPlayer\publish`, which is **not** the copy the user runs. Publishing without
copying makes it look like your change did nothing. Always finish with the copy step.

---

## The build → verify → ship loop

```bash
dotnet build C:\MusicPlayer\AudioFool.slnx
```

```bash
dotnet test C:\MusicPlayer\tests\AudioFool.Core.Tests\AudioFool.Core.Tests.csproj
```

```bash
dotnet publish C:\MusicPlayer\src\AudioFool\AudioFool.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -p:DebugType=none -o C:\MusicPlayer\publish
```

Then install. The running app must be closed first or the exe is locked:

```powershell
Get-Process AudioFool -ErrorAction SilentlyContinue | Stop-Process -Force
Get-ChildItem C:\MusicPlayer\publish -Filter *.pdb | Remove-Item -Force
Copy-Item "C:\MusicPlayer\publish\*" (Join-Path $env:LOCALAPPDATA "Programs\AudioFool") -Recurse -Force
```

Two shell notes:

- `dotnet` is not on PATH in a fresh shell. Prefix with
  `$env:PATH = "C:\Program Files\dotnet;$env:PATH"`.
- **Never put that PATH assignment and a `Remove-Item` in the same PowerShell call.**
  The sandbox guard misparses it as trying to delete `C:\Program` and blocks the whole
  command. Split them into separate calls.
- **Stop-Process needs a few seconds before Copy-Item will succeed.** If the copy fails
  with "file in use", the process is still shutting down. Add `Start-Sleep -Seconds 3`
  between the stop and copy steps.

---

## Version control

`C:\MusicPlayer` is a git repository as of session 5. Branch `main`, one commit
(`86fb5ab`) holding all four prior sessions of work. **No remote** — it is local only,
so pushing is not part of any workflow here.

Three decisions baked into that commit, so you do not have to re-derive them:

- **The BASS DLLs are vendored, not ignored.** `lib/bass/x64/` (13 DLLs, 864 KB) is
  tracked. `dotnet restore` cannot produce them — the `ManagedBass.*` NuGet packages
  ship only the C# bindings — so ignoring them would leave the repo unbuildable from a
  clean clone. The old `.gitignore` rule excluding them has been removed.
- **`.gitattributes` pins `eol=lf`.** Global `core.autocrlf` is `true` on this machine,
  which would rewrite the line endings of every source file on checkout. The files are
  LF on disk; the attribute keeps them that way. `*.dll`, `*.exe`, `*.ico`, `*.jpg` and
  `*.png` are marked `binary`.
- **`bin/`, `obj/`, `publish/` are ignored**, as is the unreferenced
  `src/AudioFool/Resources/icon_preview.png`. `Resources/logo_square.png` **is** tracked
  — it is the input `tools/make-icon.ps1` regenerates the .ico from.

Verified, not assumed: a fresh `git clone` of this repo into a scratch directory builds
with 0 warnings and passes all 73 tests. If you add a dependency that lives outside
NuGet, re-run that check — it is the only thing that catches a file you forgot to track.

---

## State at handoff

Everything below is implemented **and verified working**, not merely written:

### Core features (from session 1)
- Artist → Album → Song browsing. Leading "The" ignored when sorting artists, albums
  oldest-first, tracks by disc then track number.
- 11-column track grid (see below), sortable by header click.
- Album art: embedded, falling back to a cover file beside the audio. Double-click
  either the large or the now-playing art for a full-size viewer.
- Search across artists / albums / songs from one box, 180 ms debounce. Search bar
  widened to 333 px, aligned with the Albums panel right edge.
- Gapless playback (BASSmix mixer plus a mixtime sync).
- Bit-perfect exclusive WASAPI with per-track sample-rate following.
- DSD: PCM conversion at up to DSD-rate ÷ 8, or DoP passthrough at DSD-rate ÷ 16.
- Library cache — **0.3 s startup** instead of 18 s, plus a ~1 s background change check.
- Portable-drive handling: drive-letter relocation, and an unreachable folder is never
  mistaken for a deleted library.
- Global hotkeys: **F9** play/pause, **F10** previous-or-restart, **F11** next.
- App icon built from `logo.jpg` (abstract flower) by `tools/make-icon.ps1`. Full
  rectangular logo shown at 30 px in the title bar header.
- **Type-ahead scroll** on Artists and Albums panes — hover and type to jump to a match.

### UI changes from session 2
- **Library folder checkboxes.** AudioFool menu → Libraries submenu → one checkable item
  per configured folder. Unchecking a folder hides its tracks instantly (no rescan);
  the enabled/disabled state is persisted to `settings.json` as `DisabledFolders`.
  Filtering happens in `MainViewModel.ApplyToView` before the search filter is applied.
- **Folder-aware status counts.** The status bar ("484 artists · ...") counts only tracks
  from checked folders, tracked in `_folderFilteredLibrary`. Search counts ("X of Y
  tracks match") use the same filtered denominator.
- **Rescan button** — a small `↻` icon button sits to the left of the status text. Greys
  out while a scan is running. Replaces the Rescan item that was previously in the menu.
- **Search box in title bar.** `TitleBar.Header` alongside the AudioFool menu, saving
  vertical space for the browser panes. Width 333 px (session 4).
- **Bit-Perfect toggle in status bar** (session 4). Moved from the title bar to the
  bottom-right corner of the status bar, `FontSize="11"` matching the info text.
- **Libraries submenu** consolidates Add folder and folder checkboxes. Add folder sits
  below the folder list (separated by a rule). Rescan was moved to the status bar.
- **Now-playing indicator.** A small music note icon column (22 px, leftmost) in the
  track grid lights up on whichever row is currently playing. Uses `IsCurrentTrackConverter`
  (a `MultiBinding` comparing the row's `Track` to `NowPlaying`).
- **Volume defaults to 100%** on every launch instead of restoring the saved level.
  **Bit-Perfect does the same** from session 5: it starts off every launch regardless of
  what was saved, since exclusive mode silences every other app on the machine.

### UI changes from session 3
- **Theme system.** AudioFool menu → Themes submenu → checkable items (radio-button
  behaviour — only one can be active). The active theme is persisted to `settings.json`
  as `Theme` (string, default `"Dark"`). Panels, borders and backdrop switch instantly;
  the accent colour does not (see Accent colours below).
- **"Dark" theme** — the existing look. WPF-UI dark base, Mica backdrop.
- **"Vista" theme** — Windows Vista Aero Glass aesthetic. Switches the window backdrop
  from Mica to Acrylic (blur-through transparency), then merges a resource dictionary
  (`Themes/VistaTheme.xaml`) that overrides panel backgrounds with a blue-tinted glass
  gradient, borders with a glass-edge highlight, and slider brushes to match.

### Changes from session 4
- **Type-ahead scroll on Artists and Albums panes.** Hover the mouse over either list
  and start typing — the list selects and scrolls to the first match. Artists match
  against `SortKey` (leading articles stripped, matching the sort order); Albums match
  against `Title`. The keystroke buffer resets after 800 ms of inactivity. Backspace
  removes the last character; Escape clears immediately. Moving the mouse between panes
  resets the buffer. Typing is ignored when a text box has keyboard focus (e.g. the
  search box). Implementation is in `MainWindow.xaml.cs`: window-level `PreviewTextInput`
  and `PreviewKeyDown` handlers, with `GetTypeAheadTarget()` checking `IsMouseOver` on
  each list.
- **New app logo.** `logo.jpg` (abstract flower) replaces the cat icon. The .ico is
  generated from a letterboxed square (`Resources/logo_square.png`, dark background
  padding) via `tools/make-icon.ps1` with `Left=0 Top=0 Size=1.0`. The full rectangular
  logo is displayed as a 30 px-tall `Image` in the title bar header (not via
  `TitleBar.Icon`, which is too small for this image).
- **Search bar widened to 333 px.** Fixed width, aligned to end at roughly the right
  edge of the Albums panel.
- **Bit-Perfect toggle moved to the status bar** (bottom-right corner), `FontSize="11"`
  matching the status text. Capitalized as "Bit-Perfect".

### Changes from session 5
- **Version control.** See the Version control section above.
- **Shuffle and repeat**, as two buttons flanking the transport controls. Shuffle is a
  toggle; repeat cycles off → whole queue → this track. Both persist to `settings.json`.
  Architecture in the section below; the play order lives in a new `PlayOrder` class,
  deliberately kept free of BASS so it can be tested without a sound device.
- **Teal accent for the Dark theme** — `#14B8A6`. Vista keeps Windows blue `#0078D4`.
  See Accent colours below; there are two WPF-UI traps in there.
- **Bit-Perfect starts off** on every launch, no longer restored from settings.
- **The "Nothing playing" placeholder is gone.** The now-playing title is blank when
  nothing is loaded, leaving just the placeholder art tile.
- **The Year column was removed** from the track grid, taking it from 12 columns to 11.
  Every `DisplayIndex` after it was renumbered — leaving a gap there would let the
  width-distribution pass reorder the grid. The album pane still shows its year.
- **Column auto-fit** on double-clicking the divider between # and Song. See the
  section below. Every column now has an `x:Name`.

**93 tests pass** in `AudioFool.Core.Tests` — the original 73 plus 20 over `PlayOrder`.

### Changes from session 6

- **Diacritic-insensitive search.** "Bjork" now finds Björk, "Bela" finds Béla Fleck, etc.
  Uses `CompareInfo.IndexOf` with `CompareOptions.IgnoreNonSpace | IgnoreCase` (InvariantCulture)
  in `LibrarySearch.cs`. 9 new theory tests cover the affected artists and symmetry (accented
  query finds unaccented value). 11 artists and 238 tracks in the user's library were affected.
- **Double-click album plays first track.** `PlayAlbumCommand` on `AlbumList` —
  sets `SelectedAlbum` synchronously (which populates `Tracks`), then calls
  `PlayTrack(Tracks.FirstOrDefault())`.
- **Phantom horizontal scrollbar hidden.** WPF's star-column layout leaves ~0.25% rounding
  overflow. A `ScrollChanged` handler in `MainWindow.xaml.cs` (`HookTrackGridScrollBar`) hides
  the bar when `ScrollableWidth <= 4` px, toggling `HorizontalScrollBarVisibility` between
  `Hidden` and `Auto`.
- **Album header subtitle: one attribute per line.** The "Artist · Year · N tracks · Duration"
  single-line was replaced with an `ItemsControl` bound to `AlbumHeaderSubtitleLines` — one
  `TextBlock` per line. Track count removed. Font and size unchanged.
- **Tag and album art editing** (major new feature). Right-click a track → "Edit Tags…"
  (Title / Artist / AlbumArtist / Album / Year / Track # / Disc #). Right-click an album →
  "Edit Album Tags…" (Artist / AlbumArtist / Album / Year + art replacement). Art is always
  written both into every track's embedded tags AND as a folder `cover.jpg`/`cover.png`.
  Multi-disc albums (multiple sub-folders) get a cover file in every sub-folder.
  Partial batch failures (e.g. 9 of 11 files) commit all successful writes and name the
  failures in `StatusText`; nothing is rolled back. See new source files below.
- **Art picker opens in the album's own folder.** `InitialDirectory` set from
  `Path.GetDirectoryName(firstTrack.FilePath)`.
- **Auto-fit columns on album selection.** `AutoFitColumns()` is called automatically when
  `SelectedAlbum` changes (in the `BrowserList_SelectionChanged` deferred callback).
- **Track row selection highlight.** The `DataGrid.RowStyle` now uses the flat WinUI accent
  `AccentFillColorSelectedTextBackgroundBrush`. A Vista Aero Glass gradient was tried and
  reverted at user request. Critical constraint: `RowStyle` must NOT use `BasedOn` on the
  WPF-UI base — that base has an unconditional white `Background` setter that overrides
  every row, making the grid look broken. See the Gotchas section.

**113 tests pass** — the 93 from session 5 plus 9 diacritic search tests and 11 tag-writer tests.

Shuffle was also verified end-to-end against the real engine with real FLAC files, by a
headless probe driving `AudioEngine` in shared mode at zero gain — silent, and without
taking the device from the running app. Fourteen checks, including the one that matters
most: that the shuffled order is *stable across skips* rather than regenerated on every
track change.

### Deliberately not done

- **No TAK or DTS decoder.** un4seen publishes neither. Needs a third-party build or a
  libVLC fallback; `AudioEngine.CreateDecodeStream` is the single seam for that.
- No playlists or queue view. Shuffle and repeat are done (session 5); what is still
  missing is a visible, editable queue.
- Memory sits around 400–900 MB after a cold scan. A post-scan GC compaction runs. Never
  profiled.
- The seek bar reads ~200 ms ahead of what you hear (decode position versus device
  buffer). Invisible in practice.

---

## New source files added in session 2

| File | Purpose |
|---|---|
| `src/AudioFool/ViewModels/FolderFilterItem.cs` | Observable VM wrapping a folder path + `IsEnabled` bool. `DisplayName` is the last path segment; `FolderPath` is the full path (settable for drive-letter relocation). |
| `src/AudioFool/BindingProxy.cs` | `Freezable` subclass that inherits `DataContext`. Used to relay ViewModel commands into `CompositeCollection` items that otherwise have no DataContext. |
| `src/AudioFool/Formatting/IsCurrentTrackConverter.cs` | `IMultiValueConverter` — returns `Visible` when two bound values are the same object reference, `Collapsed` otherwise. Powers the now-playing indicator. |

## New source files added in session 3

| File | Purpose |
|---|---|
| `src/AudioFool/ViewModels/ThemeItem.cs` | Observable VM wrapping a theme name + `IsSelected` bool. Follows the same pattern as `FolderFilterItem`. |
| `src/AudioFool/ThemeService.cs` | Static helper that swaps resource-dictionary overlays and sets the window `BackdropType`. Called from `MainViewModel` on theme change and from `App.OnStartup` for the saved theme. |
| `src/AudioFool/Themes/VistaTheme.xaml` | Vista Aero Glass resource dictionary. Overrides `ControlFillColorDefaultBrush` (glass gradient), `ControlElevationBorderBrush` (glass-edge highlight), `ControlFillColorSecondaryBrush`, and slider brushes. |

## New source files added in session 6

| File | Purpose |
|---|---|
| `src/AudioFool.Core/Library/TagEdit.cs` | `TrackTagEdit`, `AlbumTagEdit`, `ArtPayload` — immutable records describing a pending write; no `null`-means-unchanged ambiguity. |
| `src/AudioFool.Core/Library/TagWriter.cs` | Static write layer: `WriteTrackTags`, `WriteAlbumTrackTags`, `WriteFolderArt`. Never throws — all results are `TagWriteResult`/`FolderArtWriteResult` records. Re-stamps `FileSize`/`ModifiedUtc` via `FileStamp.For(path)` after each write so `Track.MatchesFile` stays correct. |
| `src/AudioFool/ViewModels/TagEditViewModel.cs` | Backs `TagEditWindow` for both single-track and album-batch modes. `IsAlbumMode` flag drives which fields are visible. `BuildTrackEdit()` / `BuildAlbumEdit()` / `PickedArtPayload()` are read after `ShowDialog() == true`. |
| `src/AudioFool/TagEditWindow.xaml[.cs]` | Modal dialog (FluentWindow/Mica, owner-centered). Save/Cancel in code-behind set `DialogResult`. Art panel and Track#/Disc# row are hidden in album mode via `Visibility` bindings. |
| `tests/AudioFool.Core.Tests/TagWriterTests.cs` | Round-trip tests: all fields survive write+read; art bytes match; `FileStamp` changes after write; album mode leaves Title/Track# untouched; missing file fails cleanly. |
| `tests/AudioFool.Core.Tests/TestData/sample.flac` | 0.5 s silence fixture for tag-writer tests (generated by ffmpeg). `.gitattributes` marks as binary. |
| `tests/AudioFool.Core.Tests/TestData/sample.mp3` | Same for MP3 path (different TagLib code path). |
| `tests/AudioFool.Core.Tests/TestData/cover.jpg` | 8×8 gray JPEG for art round-trip tests. |

## New/modified resource files in session 4

| File | Purpose |
|---|---|
| `logo.jpg` | Source logo image (abstract flower, 1536×1152). |
| `src/AudioFool/Resources/logo.jpg` | Copy embedded as a WPF Resource for the title bar. |
| `src/AudioFool/Resources/logo_square.png` | Letterboxed 1536×1536 square (dark background) used as input for icon generation. |
| `src/AudioFool/Resources/AudioFool.ico` | Regenerated from `logo_square.png` — full logo visible at all sizes. |

---

## Architecture notes for the new features

### Library folder filtering
`AppSettings.DisabledFolders` (a `List<string>`) lists paths that are turned off.
`MainViewModel.FolderFilters` is an `ObservableCollection<FolderFilterItem>` kept in
sync with `MusicFolders`. When any item's `IsEnabled` changes, `OnFolderFilterItemChanged`
saves settings and calls `ApplyToView(keepSelection: true)` — instant, no rescan.

`ApplyToView` builds `visible` (folder-filtered tracks) before passing to
`LibrarySearch.Filter`, and stores the folder-filtered grouping in `_folderFilteredLibrary`
for the status bar. The view that drives the UI is either `_folderFilteredLibrary` (no
search active) or `LibraryScanner.Build(matched)` (search active).

Drive-letter relocation (`RelocateMovedFoldersAsync`) updates `FolderFilterItem.FolderPath`
in place alongside `MusicFolders[index]`.

### Theme switching
`AppSettings.Theme` (a `string`, default `"Dark"`) stores the active theme name.
`MainViewModel.ThemeItems` is an `ObservableCollection<ThemeItem>` built from the known
theme names at startup. Radio-button behaviour: when one item's `IsSelected` goes true,
`OnThemeItemChanged` unchecks the others, saves settings, and calls
`ThemeService.Apply(name)`. Unchecking the active theme is blocked — the handler
re-checks it immediately.

`ThemeService.Apply` is the single entry point for theme changes. For `"Dark"` it
removes any overlay dictionary and sets `WindowBackdropType.Mica`. For `"Vista"` it
merges `Themes/VistaTheme.xaml` into `Application.Resources.MergedDictionaries` and
sets `WindowBackdropType.Acrylic`. All brushes in the app use `DynamicResource`, so
the swap takes effect immediately.

To add a new theme: create a `Themes/FooTheme.xaml` resource dictionary, add
`"Foo"` to the theme-name array in the `MainViewModel` constructor, and add a case
to `ThemeService.Apply`.

**Accent colours (session 5).** Each theme names its accent explicitly:
Dark is teal `#14B8A6`, Vista is Windows blue `#0078D4`. Two things about this were
learned the hard way and are easy to trip over again:

- **`ApplicationAccentColorManager.ApplySystemAccent()` silently does nothing here.**
  It resolves the theme through `ApplicationThemeManager`, which this app never drives —
  the theme comes from a `ThemesDictionary` in `App.xaml` — so it leaves whichever accent
  was applied last in place. Use the explicit
  `Apply(color, ApplicationTheme.Dark)` overload instead.
- **Only the first `Apply` call in a process takes effect.** Verified by launching with
  each theme saved: Dark starts teal, Vista starts blue, but switching themes from the
  menu at runtime leaves the accent where it was. Everything else about the theme swap —
  panel brushes, borders, backdrop — does update live. Changing the accent of a running
  app would need the WPF-UI theme dictionary re-merged, which has not been attempted.

### Shuffle and repeat (session 5)
`PlayOrder` (`src/AudioFool.Core/Playback/PlayOrder.cs`) holds a permutation of the
queue's indices — `_order[p]` is the queue index of the p-th track to play — plus its
inverse for O(1) lookup. **The queue itself always stays in album order.** That is what
makes turning shuffle off restore the running order, and what makes Previous retrace
what was actually heard.

`AudioEngine` owns one `PlayOrder` and asks it for every successor and predecessor.
There were four places that used to compute `_index ± 1`; all four now call
`_order.Next(...)` / `_order.Previous(...)`, which return -1 rather than wrapping when
repeat is off:

| Where | Was |
|---|---|
| `Next()` | `_index + 1`, wrap at the end |
| `Previous()` | `_index - 1`, wrap at the start |
| `PrefetchAfter()` | `currentIndex + 1` — this one feeds the gapless splice |
| `OnCurrentStreamEnded` | uses `_prefetchedIndex`, so it inherits the above |

**The trap to know about:** `Play` was split into a public `Play` and a private
`PlayCore(..., bool resetOrder)`. Only a genuinely new queue may rebuild the order.
`JumpTo` — which is what `Next`, `Previous`, and every device-reopen path actually call —
passes `resetOrder: false`. Miss that and the queue re-shuffles on every single track
change: Next becomes random-walk and Previous can never retrace.

`Shuffle` and `Repeat` are both real properties now, not auto-properties. Changing
either changes which track comes next, so both call `RefreshPrefetch()` to throw away
the stream that was opened ahead of time and open the right one instead. Persisted to
`settings.json` as `Shuffle` (bool) and `Repeat` (string: `Off` / `All` / `One`).

In the UI the two buttons sit inside the transport `StackPanel`
(`MainWindow.xaml`, now-playing bar, `Grid.Column="2"`), flanking Previous/Play/Next.
They swap glyph *and* tint rather than tint alone — WPF-UI's `ArrowShuffleOff24` and
`ArrowRepeatAllOff24` are struck-through variants, and an accent colour on its own is a
weak signal next to the Primary-appearance play button. `Repeat` cycles through three
states, so it is a `ui:Button` driving `CycleRepeatCommand`, not a `ToggleButton`.

### Column auto-fit (session 5)
Double-clicking the divider between **#** and **Song** fits every column at once.
Every other divider keeps WPF's stock behaviour of fitting the single column it
belongs to. Implemented in `MainWindow.xaml.cs` as `TrackGrid_PreviewMouseLeftButtonDown`
plus `AutoFitColumns`.

Three things about it that are not obvious:

- **The hook is `PreviewMouseLeftButtonDown` with `ClickCount == 2`, not a
  double-click event.** `DataGridColumnHeader` wires its own handler to the gripper
  `Thumb` to auto-fit that one column. Tunnelling from the `DataGrid` is what gets
  there first and lets `e.Handled = true` suppress it. A bubbling `MouseDoubleClick`
  handler runs too late.
- **The divider is found by geometry, not by template part name.** The handler walks
  up to the `DataGridColumnHeader` and checks whether the click landed within 6 px of
  its left or right edge, then matches the column against `TrackNumberColumn` /
  `SongColumn`. Both sides of the divider are accepted, because whether the gripper
  belongs to the left column or the right one is a template detail. Every column now
  carries an `x:Name` so none of this depends on header strings.
- **Widths are measured by setting each column to `Auto` and reading `ActualWidth`
  back after `UpdateLayout()`.** Row virtualisation means that measures the realised
  rows, so the fit follows what is on screen. That is the useful answer; measuring
  26,000 rows would stall the UI.

The allocation order is the one that was asked for. `TrackNumberColumn` and the six
narrow facts (Time, Disc, Kind, Bitrate, Bit Depth, Sample Rate) are satisfied first
and always get their full width — they are the columns whose *headers* are wider than
their values, so clipping them costs a word rather than a character. What remains goes
to Song, then Artist, then Album, each holding back a 70 px floor for the ones behind
it. Song is then applied as a **star** column rather than a pixel width, so it collects
whatever is spare and keeps flexing when the window is resized. At minimum window
width the narrow columns alone overrun the space; the floors are deliberately allowed
to overflow into a horizontal scrollbar rather than collapsing Artist and Album to
20 px slivers.

`IndicatorColumn` is excluded throughout: it is a fixed 22 px by design and has no
header text worth fitting.

### Tag and album art editing (session 6)

`Track`/`Album` remain immutable. A write produces a new `Track` via two new copy methods
on `Track` itself — `WithTags(TrackTagEdit, FileStamp, string?)` and
`WithAlbumTags(AlbumTagEdit, FileStamp, string?)` — mirroring the existing `Relocated`
pattern. The new `Track` is spliced into `_library.AllTracks` by file path; then
`LibraryScanner.Build(newAll)` rebuilds the tree (~0.04 s over 26k tracks) and
`ApplyToView(keepSelection: true)` refreshes the visible lists.

`TagWriter` is the only entry point for file writes. `SaveTags(path, Action<TagLib.Tag>, ArtPayload?)`
opens one `TagLib.File.Create` session, applies the field setter, sets `Pictures` if art
is provided, calls `file.Save()`, catches every known exception, and returns a `(bool, string?)`
tuple. `TagWriteResult` and `FolderArtWriteResult` are the result records; they never throw
across the boundary.

`WriteFolderArt` is pure filesystem (no TagLib). It writes `cover.jpg` or `cover.png` based
on the picked image's actual format. If an existing cover has a different extension it deletes
the old file first — never leaving two competing cover files, never writing bytes under a
mismatched extension.

An album can span multiple physical directories (multi-disc sets, stray compilations).
`AlbumDirectories(Album)` collects distinct `Path.GetDirectoryName` values across all
`album.Tracks`. Folder art is written to every one of them, not just `Album.FolderArtPath`.

After any art write `AlbumArtService.InvalidateAlbum(album)` busts all four cache key
shapes for the album (thumbnail, full-viewer, per-track). Called with the *pre-edit* album
— its current identity and track paths are what is actually in the cache. If the album is
renamed, the new identity has no cache entry and decodes fresh automatically.

The first `ContextMenu` in the app: track rows get "Edit Tags…" wired to `EditTrackTagsCommand`;
album rows get "Edit Album Tags…" wired to `EditAlbumTagsCommand`. `DataGrid` rows don't
select on right-click by default, so a `PreviewMouseRightButtonDown` handler on the
`DataGrid.RowStyle` manually sets `row.IsSelected = true` before the menu opens.

### Type-ahead scroll (Artists and Albums)
Implemented entirely in `MainWindow.xaml.cs` code-behind — no new files. Three fields
on `MainWindow`: `_typeAheadBuffer` (the accumulated keystrokes), `_typeAheadTarget`
(which `ListBox` the mouse was over), and `_typeAheadTimer` (800 ms `DispatcherTimer`
that resets both).

`PreviewTextInput` and `PreviewKeyDown` are hooked at the window level in the
constructor. Each handler calls `GetTypeAheadTarget()` which returns `ArtistList` or
`AlbumList` based on `IsMouseOver`, or null. When the target changes, the buffer resets.
`SelectTypeAheadMatch()` does a `FirstOrDefault` with `StartsWith` — against `SortKey`
for artists, `Title` for albums — and sets the `ListBox.SelectedItem`, which triggers
the existing `BrowserList_SelectionChanged` scroll-into-view logic.

### Title bar layout (session 4)
`TitleBar.Icon` was removed. The logo is a 30 px-tall `Image` (full rectangular
`logo.jpg`) at the start of the `TitleBar.Header` `StackPanel`, followed by the menu
and a 333 px-wide search box. The Bit-Perfect toggle moved to the status bar (Grid
row 3, column 2).

### CompositeCollection in the Libraries submenu
WPF's `MenuItem` cannot mix `ItemsSource` items with static child items. The workaround:
`BindingProxy` (a `Freezable`) is declared as a `FluentWindow` resource with
`Data="{Binding}"` — Freezables inherit DataContext, so this ferries the ViewModel.
A `CollectionViewSource` bound through the proxy feeds the folder items. Static `MenuItem`
entries for Add folder bind their commands via
`{Binding Source={StaticResource Proxy}, Path=Data.AddFolderCommand}`.

---

## Verifying UI changes without wrecking the user's desktop

Learned the hard way — the user stopped a session over this. **Do not** drive the mouse,
send keystrokes to the focused window, or pull windows to the foreground while they are
working.

**Use UI Automation.** It sets values and activates controls without stealing focus or
moving the pointer:

```powershell
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
# Find the window by process id, then elements by AutomationId (x:Name in XAML).
# ValuePattern.SetValue(...) to type; SelectionItemPattern.Select() to pick a row.
```

Named elements available to query: `SearchBox`, `ArtistList`, `AlbumList`, `TrackGrid`,
`NowPlayingTitle`, `PositionText`, `DurationText`.

**Screenshots:** use `PrintWindow` with flag `2` (`PW_RENDERFULLCONTENT`), and call
`SetProcessDPIAware()` **first**. A DPI-unaware capture of a DPI-aware window silently
returns a misaligned or partial image. That produced a completely false bug report
mid-session: a layout was declared broken when it was fine, and only a diagnostic dump
of the actual row heights settled it.

**Verify what screenshots cannot show** with a small headless console app referencing
`AudioFool.Core`. Several are left in the session scratchpad. That approach caught things
no screenshot would: that DoP marker bytes were well-formed *before* any audio reached
the DAC, and that an advancing position proves the WASAPI callback is actually running.

---

## Gotchas that cost real time

- **`VirtualizationMode="Recycling"` plus a `Loaded` handler is a silent bug.** A recycled
  row gets a new `DataContext` but does **not** re-raise `Loaded`. Album art loaded that
  way disappeared as soon as you scrolled. Never load per-item data from `Loaded` in a
  recycling list.
- **`ScrollIntoView` scrolls the minimum distance**, so a selection entering from below
  lands flush against the bottom edge, half-cut. Scroll to the last item first, then to
  the target, to place it near the top.
- **`BasedOn` a WPF-UI control style silently drops local values on named template
  parts.** The volume/seek sliders use a custom template (`EdgeToEdgeSlider` in
  `App.xaml`) whose rail `Border` sets `Margin="10,0"` to tuck its rounded end caps under
  the circular thumb. With `BasedOn="{StaticResource {x:Type Slider}}"` the inherited
  WPF-UI style reset that `Margin` to `0` at runtime. Fix: drop `BasedOn` entirely.
- **Star-width DataGrid columns get reordered** by the width-distribution pass. Pin
  `DisplayIndex` on every column. The now-playing indicator column is DisplayIndex 0;
  all others shifted up by one in session 2.
- **XML comments cannot contain `--`.** Dashed separator comments break XAML compilation.
- **PowerShell `Test-Path` treats `[...]` as a wildcard.** Album folders like
  `[2014] Album` need `-LiteralPath`.
- **`perl -0777 -pe` mangles C# `$"..."` interpolation** — `$` is a perl sigil. For
  multi-line C# edits, split the file with `sed -n` and concatenate instead. For prose
  with apostrophes, a heredoc will also fight you; use the Write tool.
- **EXIF orientation:** `System.Drawing` ignores it, so a "portrait" phone photo loads as
  landscape pixels. `tools/make-icon.ps1` handles this now.
- **`ManagedBass` names differ from the C API** — `MixerAddChannel` not
  `MixerAddChannelOnce`, `MusicRamp` not `MusicRamps`. The NuGet packages ship XML docs
  beside the DLL; grep those rather than guessing.
- **un4seen third-party add-ons** live under `un4seen.com/files/z/2/...`, not `files/...`.
  And the APE plugin is `bassape.dll`, not `bass_ape.dll`.
- The DAC reports **"Busy"** when another app — or a second AudioFool instance — holds it
  exclusively. Not a code fault.
- **`DataGrid.RowStyle` must NOT use `BasedOn="{StaticResource {x:Type DataGridRow}}"`.**
  The WPF-UI base `DataGridRow` style has an unconditional `Background` setter that paints
  every row white regardless of any `IsSelected` trigger you add on top. The symptom is a
  white track grid after any `BasedOn` change. Fix: drop `BasedOn` entirely and set
  `Background="Transparent"` explicitly. Spent two attempts figuring this out in session 6.
- **The Edit tool silently replaces straight ASCII quotes with Unicode curly quotes**
  (`U+201C` / `U+201D`) inside C# string literals. This produces dozens of compile errors
  with no obvious cause. If you see `error CS1056: Unexpected character '"'` pointing at
  what looks like a normal string, run this to find and fix all instances in the file:
  ```powershell
  $p = "path\to\file.cs"
  $c = [System.IO.File]::ReadAllText($p, [System.Text.Encoding]::UTF8)
  [System.IO.File]::WriteAllText($p, ($c -replace [char]0x201C,'"' -replace [char]0x201D,'"'), [System.Text.Encoding]::UTF8)
  ```

---

## Testing a portable drive without unplugging anything

`subst` gives you a real drive letter you can mount, remove, and re-create elsewhere:

```powershell
subst X: C:\some\staging\folder   # "plug in"
subst X: /D                       # "unplug"
subst Y: C:\some\staging\folder   # "reconnect under a different letter"
```

Back up `library.json` before any drive test and restore it afterwards — the app will
happily rewrite it.

---

## Suggested next steps

1. A visible, editable queue view — now the most conspicuous missing player feature.
2. MilkDrop 3 / projectM visualisation. Scoped out in session 6 (LGPL-2.1, C API,
   `GLWpfControl` for OpenGL-in-WPF, no prebuilt `libprojectM.dll` — source only).
   Proposed next step: spike build of `libprojectM.dll`. No implementation started.
3. Profile the post-scan memory.
4. TAK and DTS via a libVLC fallback decoder, if those files matter.
5. Code signing would remove the SmartScreen warning on first launch, but is rarely worth
   the cost for a personal build.
