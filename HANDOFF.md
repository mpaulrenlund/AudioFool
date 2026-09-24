# AudioFool — session handoff

Updated 2026-09-24 after the eleventh build session. Read this alongside
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

`C:\MusicPlayer` is a git repository as of session 5, working on branch `main`.

**There is a remote**, added after session 7: `origin` is
<https://github.com/mpaulrenlund/AudioFool.git>, a **private** repo whose default branch
is also `main`. `gh` is authenticated on this machine as `marcusrenlund` over HTTPS, so
`git push origin main` works with no further setup. An earlier version of this document
said the repo was local-only; that stopped being true at the *Merge GitHub's initial
commit* commit.

Three decisions baked into the first commit, so you do not have to re-derive them:

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
with 0 warnings and passes the whole suite (73 tests when that was checked in session 5;
116 now). If you add a dependency that lives outside NuGet, re-run that check — it is
the only thing that catches a file you forgot to track.

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
  widened to 406 px, aligned with the Albums panel right edge.
- Gapless playback (BASSmix mixer plus a mixtime sync).
- Bit-perfect exclusive WASAPI with per-track sample-rate following.
- DSD: PCM conversion at up to DSD-rate ÷ 8, or DoP passthrough at DSD-rate ÷ 16.
- Library cache — **0.3 s startup** instead of 18 s, plus a ~1 s background change check.
- Portable-drive handling: drive-letter relocation, and an unreachable folder is never
  mistaken for a deleted library.
- Global hotkeys: **F9** play/pause, **F10** previous-or-restart, **F11** next.
- App icon built from `logo.png` (the 1994 PlayStation mark) by `tools/make-icon.ps1`.
  The same image is the title-bar identity at 30 px, in every theme.
- **Type-ahead scroll** on Artists and Albums panes — hover and type to jump to a match.

### UI changes from session 2
- **Library folder checkboxes.** Logo menu → Libraries submenu → one checkable item
  per configured folder. Unchecking a folder hides its tracks instantly (no rescan);
  the enabled/disabled state is persisted to `settings.json` as `DisabledFolders`.
  Filtering happens in `MainViewModel.ApplyToView` before the search filter is applied.
- **Folder-aware status counts.** The status bar ("484 artists · ...") counts only tracks
  from checked folders, tracked in `_folderFilteredLibrary`. Search counts ("X of Y
  tracks match") use the same filtered denominator.
- **Rescan button** — a small `↻` icon button sits to the left of the status text. Greys
  out while a scan is running. Replaces the Rescan item that was previously in the menu.
- **Search box in title bar.** `TitleBar.Header` alongside the AudioFool menu, saving
  vertical space for the browser panes. Width 333 px (session 4), 406 px from session 8.
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
- **Theme system.** Logo menu → Themes submenu → checkable items (radio-button
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
- **New app logo.** `logo.jpg` (abstract flower) replaces the cat icon. *(Superseded in
  session 8 by the PlayStation mark — see below.)* The .ico is
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
  (Title / Artist / AlbumArtist / Album / Year / Track # of total / Disc # of total). Right-click an album →
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

### Changes from session 8

- **New logo: the 1994 PlayStation mark.** `PlayStation-Logo-1994.png` at the repo root
  is the source. It is copied to `src/AudioFool/Resources/logo.png` and embedded as a WPF
  `Resource`. The old `logo.jpg` — the root copy and the one under `Resources/` — and its
  `.csproj` `Resource` entry are gone.
- **One brand mark for every theme.** PS1 used to fill the `AfBrandMark` slot with its own
  moulded badge of the four face-button shapes, and override `AfSizeBrandMark` to 22 px to
  suit it. Both overrides were removed, so all three themes now render the shared
  `Components.xaml` template — the logo image at 30 px. The four face-button geometries
  stay in `Ps1Theme.xaml`; the status lamp and the bit-perfect key still use them.
- **PS1 keeps its entrance animation on the logo.** The animated style was
  `Ps1BrandShapes` (a `StackPanel` style) and is now `AfBrandMarkImage` (an `Image`
  style), empty by default in `Components.xaml` and filled by `Ps1Motion.xaml`. The
  `ScaleTransform` it animates is declared *in* the template, which is the only place a
  storyboard in a style can reach one (see the motion note under the design system).
- **App icon regenerated.** `logo_square.png` is now a 1280×1280 transparent square — the
  logo trimmed to its alpha bounds (19,15 → 1259,956) and fitted to 92% of the width,
  centred. `tools/make-icon.ps1 -Left 0 -Top 0 -Size 1.0` turns that into the 9-frame
  `AudioFool.ico`. Transparent padding rather than the old dark letterbox, so the icon
  sits on any background.
- **Verified**, not assumed: all three title bars rendered off-screen with ThemeLab, and
  every .ico frame decoded through WIC (`IconBitmapDecoder` — 9 frames, 16→256, Bgra32)
  and eyeballed at 16/20/24/32/48/64 px on both dark and white. Note that
  `System.Drawing`'s `Icon.ToBitmap()` throws on the 128 and 256 px frames because they
  are PNG-compressed; that is a GDI+ limitation, not a bad file. Use WIC to check an icon.
- **The logo is now the menu.** The `MenuItem` that was labelled "AudioFool" is now the
  click target for the mark, which is drawn over it, so clicking the mark drops the same
  File-style menu. The word is gone. It is deliberately *not* the item's `Header` - that
  crops it to 20 px; see *Title bar layout* below for that and the width numbers.
- **Search box widened to 406 px**, spending the width the name freed while keeping its
  right edge level with the Albums panel, where it has always been.
- **ThemeLab gained `--menu` and `--menutree`.** `--menu` checks the logo actually opens
  the menu without a mouse: it hit-tests the centre of the mark and walks up the tree to
  confirm the hit lands inside the `MenuItem`, then expands through the automation peer
  and lists the submenu. `--menutree` prints every element under the item with its size,
  height limits and clip — which is what found the 20 px header cap. Popups render in
  their own window, so a screenshot could never have shown either.

### Changes from session 9

- **Track and disc totals.** `Track` gained `TrackCount` and `DiscCount`, read from
  TagLib's `Tag.TrackCount` / `Tag.DiscCount` in `TagReader.Read` and written back by
  `TagWriter.WriteTrackTags`. The tag dialog edits them on two separate rows — Track
  *n* of *total*, Disc *n* of *total* — replacing the single "Track / Disc" row.
- **The # and Disc columns show "3/12" and "1/2".** A bare number when the file names
  no total, blank when it names no number at all. `Display.NumberOfTotal` does the
  formatting; `NumberOfTotalConverter` is an `IMultiValueConverter` so the column can
  keep `SortMemberPath` on the raw number and still sort numerically. Both columns
  widened from 44/46 px to 56 px, and both are already in the auto-fit `mustFit` set,
  so they size themselves to the realised rows.
- **`LibraryCache.CurrentVersion` is 2.** This is the part worth understanding: adding
  a nullable field to `Track` does *not* normally need a bump, because it deserialises
  as null. Here null is the whole problem — every cached track still matches its file
  on size and write time, so the incremental scan would never re-read a tag and the two
  columns would stay blank permanently. The bump forces one full scan and is the only
  thing that fills them. **Measured on the real library, that scan took 56 s** from
  launch to `library.json` being rewritten — not the 17.6 s the tag-read row of the
  README's table would suggest. That figure is one phase of a warm run; a cold rebuild
  of 26,747 files off the USB SSD is three times it. The window is up and usable at
  3 s throughout, since the scan is background work.
- **Measured before designing.** A probe over the real library (1,500-file sample of
  26,747) found `TrackCount` on **74.7%** of files, `DiscCount` on **72.5%**, neither on
  19%, and only **12.2%** in multi-disc sets. That is what settled "show the total when
  it exists, bare number otherwise" over inventing a total from the album's track count.
  The finished rescan bears the sample out: of 26,747 tracks, 20,063 (**75.0%**) carry
  `TrackCount` and 20,044 (**74.9%**) carry `DiscCount`.
- **ThemeLab sample tracks carry counts now**, including one row with neither, so a
  render actually exercises both branches of the formatting.

### Changes from session 10

- **Library statistics window.** Logo menu → **Statistics…** (last item, below Themes)
  opens a modal `StatisticsWindow`: five headline tiles (tracks, albums, artists, play
  time, size on disk), then top 5 artists by track count, file types, audio quality,
  missing tags and tracks by decade, each as labelled bars.
- **All the arithmetic is in `AudioFool.Core`** — `Library/LibraryStatistics.cs`, pure
  and BASS-free, covered by 25 tests in `LibraryStatisticsTests.cs`. It opens no file,
  so it is recomputed every time the window opens: **48 ms over the real 26,747
  tracks**. The window is a snapshot and modal, so a rescan cannot change it underneath.
- **It counts what the status bar counts**: `_folderFilteredLibrary`, i.e. enabled
  folders only and ignoring any active search. When a folder is unchecked, a caution
  line at the top says so.
- **Top artists come from the artist tree**, not a fresh grouping, so names and counts
  are exactly the sidebar's (album artist, falling back to track artist). Bars are
  drawn against the leader rather than the whole library — five artists holding 1–2%
  each would otherwise all be slivers.
- **Quality tiers** (`LibraryStatistics.Classify`): DSD; tracker module; lossy split at
  256 kbps; lossless is hi-res when over 16 bit *or* over 48 kHz, CD quality otherwise.
  `.m4a` is lossy by extension, so `Kind == "ALAC"` overrides it, the same way
  `TagReader` decides.
- **Missing tags** checks every field the tag editor writes, plus the folder cover file.
  **Embedded artwork is not counted** and the row says so: pictures are never cached,
  so knowing would mean opening all 26,747 files. The disc rows carry a note that
  they are mostly single-disc albums. The summary line counts *essential* tags only
  (title, artist, album, year, track #): 99.4% on the real library, where "every tag"
  would read 66.7%, dragged down by disc fields nobody needs on a one-disc album.
- **Bars are two star columns** (`BarRow.Fill` / `Rest` are `GridLength`s), not a scaled
  rectangle, so they stay crisp and keep their end shape. The track is
  `AfStrokeDivider`, **not `AfSurfaceWell`** — PS1's panes are already wells, and a
  well-coloured track vanished into them. Corners use `AfRadiusWell` (square in PS1).
  Bar colour is the WPF-UI accent, or `AfStatusCaution` for missing tags, passed in
  through each `ItemsControl`'s `Tag` so one `DataTemplate` serves every section.
- **Sizing**: `SizeToContent="Height"` with `MaxHeight` capped at the work area, both
  released on `Loaded` so the window can still be resized and maximised. At 900 px
  wide the content is about 1,060 px tall; shorter screens scroll.
- **`Display` gained `Percent`** (with a `<0.1%` floor, so ten WAV files do not read as
  "0%"), **`Size`** (binary units, Explorer-style) and **`LongDuration`** ("81 d 0 h").
- **ThemeLab `--window stats`** renders the window from the real `library.json`
  (read-only via `LibraryCache.Load`, sample tracks if there is none) and prints
  every figure to the console. That is how it was verified: the printed totals match
  the session 9 measurements exactly (26,747 tracks, 25.0% without a track total,
  25.1% without a disc total), and all three themes were rendered and reviewed.

**Clickable rows** (later in session 10). Every Statistics row except a tag no track
is missing is a button:

- **An artist row selects that artist** (`MainViewModel.ShowArtist`), first clearing a
  library filter, and a search too if the search would hide them — applied at once
  rather than after the debounce, so the artist is there to select.
- **Every other row sets `MainViewModel.LibraryFilter`**, a `TrackFilter` (name plus
  predicate) the row carries from `LibraryStatistics`. Each filter is built from the
  same key its row was grouped on, and a test asserts, for every row of every
  section, that the filter matches exactly the count the row shows.
- **The filter sits under search, not instead of it**: `ApplyToView` applies folder
  checkboxes, then the filter, then the search query. "Missing Year" plus a search for
  an artist finds that artist's undated tracks. `_folderFilteredLibrary` stays
  unfiltered, so the status-bar denominator is still the whole library.
- **A chip beside the search box** (`LibraryFilterChip`) names the filter; clicking it
  clears it. It sits *after* the box so the box keeps its right edge. The status bar
  reads "70 of 26,747 tracks · Missing Year", or "Missing Year: no tracks left".
- **Fixing tags burns the filter down.** A save rebuilds through `ApplyToView`, so a
  fixed track leaves the filter; the save message gains "· 64 left: Missing Year"
  (`WithFilterProgress`). Session-only: never persisted, so the app cannot open showing
  a fraction of the library.
- **The window closes with the answer**: `Row_Click` sets `StatisticsViewModel.Chosen`
  and `DialogResult`, and `MainViewModel.ApplyStatisticsChoice` acts on it after the
  modal returns. Rows are real `Button`s with a full template of their own (hover
  wash, pressed state, the two-ring focus outline) — not `BasedOn` WPF-UI's button,
  which would fill every row. Disabled rows lose hover, cursor and tab stop together.
  Close is no longer `IsDefault`: Enter belongs to the focused row.
- **Verified with ThemeLab `--window click`** against the real library (below), not in
  the running app: 34 rows, 31 clickable (Title/Artist/Album inert). Year → 70 tracks
  over 5 artists; the chip's automation peer clears it and keeps the selection. Rush
  while searching "Buckethead" → search cleared, Rush selected. FLAC while searching
  "Rush" → 538 of 18,987, and clearing the chip leaves the search's 626. A simulated
  album fix under Missing Year → 64 left, view moves to the next artist, and
  `library.json`'s timestamp unchanged. The keyboard focus ring on a row was **not**
  rendered — an unshown dialog has no focus — it is the browser row's two-ring pattern.

**143 tests pass** — the 116 from session 9 plus 27 over `LibraryStatistics`.

### Changes from session 11

- **More fields in the tag dialogs.** Both gained **Publisher, Composer, Conductor,
  Genre and Comments** (multi-line). The album dialog also gained **Track Count** and
  **Disc *n* of *total***. Composer and Genre are multi-valued in TagLib, so they are
  edited as one string separated by semicolons (`TagDetails.Join` / `Split`).
- **The five detail fields are not cached.** Nothing else in the app shows them, so
  `Track` and `LibraryCache` are unchanged. That means no version bump and no
  56-second full rescan. `TagReader.ReadDetails` reads them when the dialog opens:
  21–87 ms for 7–26-track albums off the USB SSD. It returns null for an unreadable
  file, which the album dialog leaves out instead of treating as "all empty".
- **The album dialog writes only the fields you change.** These are the fields
  tracks legitimately disagree on: disc numbers in a multi-disc set, per-track
  comments. So each field is pre-filled only when every track agrees; otherwise it
  starts empty with a "Varies" placeholder. `AlbumTagEdit`'s optional members
  (`NumberEdit?` counts, `TagDetailsEdit` strings) are null for "keep", and the view
  model sets one only when its text differs from what it was pre-filled with.
  `NumberEdit(null)` means clear, which is how that differs from keep. The track dialog
  uses the same keep-unless-changed rule for the five detail fields. Artist / Album
  Artist / Album / Year keep their old behaviour: always written album-wide.
- **Each detail field has a clear (✕) button**, and it is the point of the feature:
  the user wants these fields mostly so they can strip them from the library. In the
  album dialog the button is the *only* way to clear a field whose tracks disagree,
  because its box already starts empty and an empty box means "keep". It adds the
  field to `_cleared` and changes the placeholder to "Will be cleared on every
  track". Typing into the box afterwards writes the typed text instead. The number
  fields have no clear button yet; a "Varies" Track Count or Disc cannot be cleared
  album-wide.
- **Year is now validated as you type.** `OnYearChanged` was missing, so "abc"
  saved as a cleared year.
- **The library is sparse on these tags.** In a 400-file sample: genre 3.5%,
  comment 1.3%, publisher 0.5%, composer 0.25%, conductor 0%. Empty boxes are
  normal. The Statistics *Missing tags* section does not count these fields; it
  would need them cached.
- **ThemeLab `--window tags --album "<title>"`** (or `--track "<title>"`) opens the
  dialog on real library entries and prints every prefill and placeholder.
  `--set "Genre=Rock;DiscNumber=2"` types into the boxes and prints what Save would
  write, `(keep)` for untouched. Nothing is saved.

**153 tests pass**: the 143 from session 10 plus 10 in `TagDetailsTests`
(round-trips on FLAC and MP3, null keeps, empty clears, album optional fields).

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


## New source files added in session 7

| File | Purpose |
|---|---|
| `src/AudioFool/Themes/DesignTokens.xaml` | Every colour, surface, text ramp, accent, stroke, spacing, type, radius, icon size, texture and motion value the app's chrome uses. Its defaults are the Dark theme. |
| `src/AudioFool/Themes/Components.xaml` | The reusable pieces built from those tokens: `AfPane`/`AfPaneDisplay`/`AfDeck`, the browser row template and its hover/selection states, art frames, the slider, text styles, and the four themeable slots. |
| `src/AudioFool/Themes/Ps1Theme.xaml` | The PS1 theme. Token overrides, WPF-UI key overrides for the stock controls, and the components it restyles: chassis panes, the display pane, grid header and cell, slider, segmented progress, the bit-perfect key, and three of the four slots (all but `AfBrandMark`). |
| `src/AudioFool/Themes/Ps1Motion.xaml` | PS1's animated states, merged only when Windows has control animations on. Row hover, selection marker, logo entrance. |
| `src/AudioFool/Formatting/LetterSpacing.cs` | `LetterSpacing.Spacer` attached property: interleaves a spacer character between letters and keeps the unspaced text as the automation name. WPF has no tracking property. |
| `tools/themelab/` | Renders the real windows off-screen to a PNG for theme review. Not in the solution. |

## New source files added in session 10

| File | Purpose |
|---|---|
| `src/AudioFool.Core/Library/LibraryStatistics.cs` | `LibraryStatistics.Compute(MusicLibrary)` plus the `Slice`, `ArtistStat`, `TagGap` records and `QualityTier`. No UI, no file access. |
| `src/AudioFool/ViewModels/StatisticsViewModel.cs` | Formats a `LibraryStatistics` once into `StatTile`s and `BarRow`s. No change notification — it is a snapshot. |
| `src/AudioFool/StatisticsWindow.xaml[.cs]` | The modal window. One `BarRowTemplate` for every section. |
| `tests/AudioFool.Core.Tests/LibraryStatisticsTests.cs` | Totals, ranking, album-artist grouping, shares summing to one, every quality tier, decade order, tag gaps, essential-tag count, and every row's filter matching its count. |

## Logo and icon resource files

| File | Purpose |
|---|---|
| `PlayStation-Logo-1994.png` | Source logo image (1280×974, transparent background). |
| `src/AudioFool/Resources/logo.png` | Copy embedded as a WPF Resource for the title bar. |
| `src/AudioFool/Resources/logo_square.png` | 1280×1280 transparent square, the logo trimmed to its alpha bounds and fitted to 92% of the width, used as input for icon generation. |
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

`ThemeService.Apply` is the single entry point for theme changes, and
`ThemeService.Names` is the single list of them (the theme menu is built from it).
For `"Dark"` it removes any overlay dictionary and sets `WindowBackdropType.Mica`;
for `"Vista"` it merges `Themes/VistaTheme.xaml` and sets `Acrylic`; for `"PS1"` it
merges `Themes/Ps1Theme.xaml` (plus `Ps1Motion.xaml` when animations are on) and
sets `None`. Everything themeable is referenced with `DynamicResource`, so the swap
takes effect live - see "Apply the theme before building the window" for the one
case where it does not.

To add a new theme: create a `Themes/FooTheme.xaml` resource dictionary overriding
the tokens and components it wants, add `"Foo"` to `ThemeService.Names`, and add a
case to `ThemeService.Apply`.

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

---

## The design system (session 7)

Every colour, size, radius, border, duration and easing the app's own chrome uses is
named in `src/AudioFool/Themes/DesignTokens.xaml`, and the reusable pieces built from
those tokens are in `Themes/Components.xaml`. Both are merged in `App.xaml` after
WPF-UI's dictionaries. Screens reference them by key; nothing in `MainWindow.xaml`
carries a literal colour or measurement any more.

**The defaults in `DesignTokens.xaml` *are* the Dark theme**, and the colour values in
it are the ones WPF-UI's dark dictionary actually resolves to at runtime — dumped from
a live app with the ThemeLab harness below, not guessed — so moving the app onto tokens
left Dark looking as it did. Where a token has no WPF-UI equivalent (the four accents,
texture, decor, motion) the default is the inert one: neutral colour, transparent
brush, zero opacity. A theme opts in.

Three conventions that are load-bearing:

- **`MainWindow` uses `DynamicResource` for styles and templates, not `StaticResource`.**
  A `StaticResource` is resolved once at load and would ignore a theme overlay.
- **A theme restyles a control WPF-UI already themes with an implicit style** in its
  own dictionary - `Style TargetType`, no key - and Dark keeps WPF-UI's look by there
  being nothing to find. PS1 does this for `DataGridColumnHeader`, `DataGridCell`,
  `ProgressBar` and `ToggleButton`.

  The obvious alternative is a trap, and it was tried: keying an optional style to
  `x:Null` and setting `Style="{DynamicResource X}"` on the control does **not** mean
  "leave it unset". It drops WPF-UI's implicit style and the control falls back to the
  *Aero* theme style, which gave the track grid a white header strip with unreadable
  titles and a near-white selected row - in Dark, not just in PS1.
- **Themeable slots** (`AfWindowDecor`, `AfBrandMark`, `AfStatusLamp`, `AfEmptyState`)
  are `ControlTemplate`s on a plain `Control`, so they cost one element and a theme can
  replace one without touching the window. Three are empty by default and draw nothing;
  `AfBrandMark` is the exception — it carries the logo for every theme, and a theme that
  wants to animate it fills the `AfBrandMarkImage` style instead of restating the
  template. The `Control` inherits the window's DataContext, so a filled slot can bind
  to the view model.

Two things could not be tokenised and are property-styled in `MainWindow.xaml` on
purpose, each with the reason in a comment there: the browser `ListBoxItem` styles and
the `DataGridRow` style, because both also carry a context menu bound through the
window's `BindingProxy` (and the row style an `EventSetter`), neither of which a
standalone dictionary can reach. Their *looks* still come from theme-owned resources —
`AfBrowserRowTemplate` for the rows, and tokens for the grid.

### PS1

`Themes/Ps1Theme.xaml` plus `Themes/Ps1Motion.xaml`. A first-generation PlayStation
reading of the app: moulded grey chassis, recessed wells, hairline bevels drawn with
borders rather than shadows, square corners, monospaced numeric columns, and four
accents used strictly by role (blue selects and acts, green confirms, red fails,
yellow cautions). The four face-button shapes carry status beside the word, never
instead of it. The track pane is the one surface with texture behind it.

It works on two levels: it redefines AudioFool's tokens *and* the WPF-UI keys the stock
controls resolve at runtime, so buttons, menus, scrollbars, text boxes and tooltips
follow without a hand-written template each. That is far less code than replacing their
templates, and far less to get wrong — it is also why the WPF-UI key list in that file
is long.

- **Motion is a separate dictionary** so honouring Windows' "show animations" setting is
  a matter of not merging it — `ThemeService` checks `SystemParameters.ClientAreaAnimation`.
  The obvious alternative, zeroing the duration tokens at runtime, is not available:
  a storyboard held by a style cannot read a `DynamicResource`, because applying a
  style seals it and freezes the freezables it holds. For the same reason the animated
  pieces (row hover, selection marker, logo entrance) were split into small styles of
  their own, so `Ps1Motion.xaml` replaces those rather than duplicating whole templates.
  A `ScaleTransform` declared *in a template* can be animated; one set through a style
  setter cannot.
- **`WindowBackdropType.None`.** PS1 is an opaque hardware surface; Mica would let the
  desktop through the chassis. `ThemeService.Backdrop` exposes the choice so
  `TagEditWindow` and `ArtWindow` do not open as glass in front of it.
- **The accent is pinned after `ApplicationAccentColorManager.Apply`.** That call writes
  its brushes straight into `Application.Resources`, which outranks every merged
  dictionary, so a theme cannot restate the accent by redefining those keys in its own
  file. `ThemeService` clears them, calls `Apply`, then copies PS1's values back over
  the top — WinUI derives a pastel accent for dark surfaces, and a primary key here is
  saturated blue with a white glyph.
- **Letter spacing** is `Formatting/LetterSpacing.cs`. WPF has no tracking property, so
  the spacer character is interleaved into the text and the unspaced original is kept as
  the automation name. Only for short labels whose text never changes — it applies on
  `Loaded` and does not watch `Text`.


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

### Title bar layout (session 4, reworked in session 8)
`TitleBar.Icon` was removed. The header `StackPanel` holds two things: a `Grid` with the
menu and the logo, and a 406 px-wide search box. Clicking the logo opens the File-style
menu. The Bit-Perfect toggle moved to the status bar (Grid row 3, column 2).

The logo is **drawn over the menu item, not inside it as the `Header`**, which is the
part worth understanding:

- **WPF-UI caps top-level menu header content at 20 px tall** and layout-clips anything
  taller. A 30 px mark set as the `Header` rendered 39×20 with 5 px cut off the top and
  the bottom — the `Control` reported `ActualHeight` 30 while carrying
  `Clip=0,5,39,20`. `Padding` on the `MenuItem` is ignored too; its template hardcodes
  14 px, which is what insets the logo from the window edge.
- So the item's `Header` is a transparent 20 px spacer, the mark is a sibling drawn on
  top of it, and `IsHitTestVisible="False"` on the mark lets the click fall through to
  the item underneath. WPF-UI's hover highlight and drop-down are untouched — the
  highlight now wraps the whole mark rather than a clipped strip of it.
- The spacer takes its `Width` from the mark (`{Binding ActualWidth, ElementName=BrandMark}`)
  instead of repeating 39, which is only 39 because of the image's aspect at 30 px.
- **The search box width is chosen so its right edge does not move.** At 406 px it ends
  at x=481 in a 1100 px window, exactly where the old 333 px box ended — level with the
  Albums panel's right edge. Change the logo or the menu padding and re-measure it.
- **`FontWeight` stays on the top-level `MenuItem`** even though its header is now a
  spacer: menu items inherit it, so removing it would quietly un-bold every submenu item.

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

**Measure a screenshot, do not eyeball it.** A capture only proves what you actually
read out of its pixels. Session 8 shipped a logo that was silently cropped from 30 px to
20 px because the render *looked* fine at 4x; the ink bounding box would have said 39x20
where 39x30 was expected. Both ThemeLab renders and `PrintWindow` captures are just
bitmaps, so scan them:

```powershell
# topmost / leftmost coloured pixel in the title-bar strip
$b = New-Object System.Drawing.Bitmap $path
for ($y=0; $y -lt 48; $y++) { for ($x=0; $x -lt 74; $x++) {
  $c = $b.GetPixel($x,$y)
  $spread = [Math]::Max($c.R,[Math]::Max($c.G,$c.B)) - [Math]::Min($c.R,[Math]::Min($c.G,$c.B))
  if ($spread -gt 40) { ... }   # coloured ink, not grey chrome
} }
```

Element rects are worth reading straight out of the live window too — UIA
`BoundingRectangle` on the window, the menu item and `SearchBox` is how the title-bar
geometry in this document was established, and it needs no screenshot at all.

**Verify what screenshots cannot show** with a small headless console app referencing
`AudioFool.Core`. Several are left in the session scratchpad. That approach caught things
no screenshot would: that DoP marker bytes were well-formed *before* any audio reached
the DAC, and that an advancing position proves the WASAPI callback is actually running.

---


---

## Apply the theme *before* building the window

`App.OnStartup` calls `ThemeService.Apply(settings.Theme)` before
`new MainWindow(...)`, and the order matters:

- A `DynamicResource` for a **Style or Template** is resolved while the element is
  still initialising — the style has to be in hand to build the template — so a theme
  merged afterwards is too late for it.
- The invalidation that would normally fix that up never arrives, because
  `Application.Resources` only notifies windows the application already has open, and
  at that point the window has not been shown.
- **Brush** references survive the wrong order, because they are evaluated lazily at
  first render. That is what makes this so easy to miss: getting it backwards looks
  like a *half*-applied theme — right colours, wrong panels — rather than no theme.

Switching themes from the menu later is unaffected: by then the window is open and does
get the invalidation.

## ThemeLab: reviewing a theme without touching the desktop

`scratchpad/themelab` (session 7) renders the real `MainWindow` and `TagEditWindow`
off-screen to a PNG. It references `AudioFool.csproj`, so it renders the actual windows
with the actual theme dictionaries — no mock, and no window on the user's desktop. It
also has a `--dump` mode that prints what a list of theme resource keys resolves to,
which is how the token defaults were baked from WPF-UI's real values instead of guessed.

```bash
ThemeLab.exe --theme PS1 --out shot.png [--w 1560 --h 900 --scale 2]
ThemeLab.exe --theme PS1 --focus ArtistList     # keyboard focus visuals do render
ThemeLab.exe --theme PS1 --window tags          # the tag dialog
ThemeLab.exe --theme PS1 --switch 1             # start in Dark, swap at runtime
ThemeLab.exe --theme Dark --dump keys.txt
ThemeLab.exe --theme Dark --menu               # does the logo open the menu?
ThemeLab.exe --theme Dark --menu --menutree   # sizes and clips under the menu item
ThemeLab.exe --theme PS1 --window stats --w 900 # statistics, from the real library.json
ThemeLab.exe --window click --click Year        # click a Statistics row, render the result
ThemeLab.exe --window click --click Rush --search Buckethead
ThemeLab.exe --window click --click Year --fix 1964   # simulate fixing the selected album
```

`--window click` loads the real `library.json` into a real `MainViewModel` through its
private `ApplyLibrary` (reflection — it is a dev tool), raises `Click` on the row's
`Button` inside a laid-out but unshown `StatisticsWindow`, passes the choice to
`ApplyStatisticsChoice`, prints the result and renders the main window. `--fix` goes
through the private `ReplaceTracksInLibrary`, in memory only. Neither writes the cache.

The `--menu`, `--menutree` and `--hidden` switches need a value after them (`--menu 1`) -
`Arg` reads the next argument, so a bare flag at the end of the line is silently ignored.
Leaving out `--out` writes `shot.png` into the current directory, i.e. the repo root.

**The build folder is not the app either.** Session 10 found AudioFool running from
`src\AudioFool\bin\Release\net10.0-windows\win-x64\AudioFool.exe`, launched from
Explorer, and it locked `AudioFool.Core.dll` so `dotnet publish` failed. The Start
Menu shortcut does point at the installed copy. If publish fails on a locked file,
check `ExecutablePath` of the running process before assuming it is the installed one.
It happened again in session 11, with a dialog open in it. Rather than killing the
user's window, publish through a different build folder:
`-p:BaseOutputPath=<scratch>\pubbin\` on the `dotnet publish` line. The installed copy
is not locked, so the copy step still works.

Three things it took a while to get right, all worth keeping if it is rebuilt:

- **It must not construct `AudioFool.App`.** `Application`'s constructor queues
  `OnStartup` onto the dispatcher, so the first time the harness pumps the queue the
  real startup path runs: it loads the user's settings, applies *their* theme over the
  one under test, and builds and shows a second `MainWindow` on the desktop. The
  harness has its own `LabApp.xaml` that merges the same dictionaries and declares the
  same converters, and nothing else. (Subclassing `App` to override `OnStartup` does
  not work — the generated `InitializeComponent` rejects a derived type.)
- **Relative pack URIs resolve against the entry assembly**, so `App.xaml` and
  `ThemeService` use the assembly-qualified form
  (`pack://application:,,,/AudioFool;component/Themes/...`). The harness also mirrors
  `Resources/logo.png` and `AudioFool.ico`, which `MainWindow.xaml` loads by relative URI.
- **Mica and Acrylic windows have a transparent background** — the composited backdrop
  belongs to the desktop, not the window — so a `RenderTargetBitmap` of Dark or Vista
  comes out on nothing. The harness paints `--bg` behind the window first. PS1 is
  opaque and needs none of that.

It renders off-screen at `Left = -20000` with `ShowActivated = false`, so nothing
appears and nothing takes focus. `Keyboard.Focus` still works on an unactivated
off-screen window, so focus rings can be reviewed.
## Gotchas that cost real time

- **WPF-UI clips a top-level menu header to 20 px tall.** Putting a 30 px image in a
  `MenuItem.Header` gives you a silently cropped image: the element reports
  `ActualHeight` 30 and WPF hangs a layout clip of `0,5,39,20` on it, so 5 px vanish off
  the top and the bottom. `Padding` on the `MenuItem` is ignored as well. Nothing errors,
  and at title-bar size the crop reads as "the logo looks a bit squat" rather than as a
  bug — the user spotted it before I did. Two lessons: draw over the item instead of
  filling its header (see *Title bar layout*), and when checking whether an image renders
  whole, **measure the ink bounding box** rather than eyeballing a screenshot. A four-line
  pixel scan for "any pixel whose channel spread exceeds 40" would have caught it
  immediately: 39x20 where 39x30 was expected. `ThemeLab --menutree` prints the sizes and
  clips of everything under the item, which is what finally located it.
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
