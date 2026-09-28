# AudioFool — session handoff

Updated 2026-09-28 after the fifteenth build session. Read this alongside
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
225 now, though the clean-clone check has not been repeated since). If you add a dependency that lives outside NuGet, re-run that check — it is
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
- **The # and Disc columns show "3/12" and "1/2".** *(Reverted in session 12: the
  user wants the bare number. The counts are still read, cached and edited.)* A bare number when the file names
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
- **Missing tags** checks every field the tag editor wrote as of session 10, plus the
  folder cover file. It does not count the five detail fields added in session 11
  (publisher, composer, conductor, genre, comment): they are not cached, and the user
  wants them gone anyway, so "missing" would be the goal rather than a gap.
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
  fields deliberately have no clear button: the user likes track and disc counts
  and declined one.
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

### Changes from session 12

- **The # and Disc columns show the bare number again** ("3", not "3/3"), at the
  user's request. Both bind `TrackNumber` / `DiscNumber` through the existing
  `NumberConverter`; `NumberOfTotalConverter` and `Display.NumberOfTotal` are gone,
  and the widths are back to 44 / 46 px. `TrackCount` / `DiscCount` are unchanged —
  still read, cached, edited in the tag dialogs and counted by Statistics. Only the
  grid stopped showing them. Verified with a ThemeLab render.
- **Right-clicking the album header art offers "Edit Album Tags…"**, the same command
  as the album row's menu, with `SelectedAlbum` as the parameter (both bound through
  the `Proxy`). Left-click still opens the art viewer. ThemeLab gained `--artmenu 1`,
  which opens that menu off-screen and prints each item's command, `CanExecute` and
  parameter; Dark and PS1 both resolve to the selected album.
- **Search Internet for cover art** (album tag dialog, beside Choose Image). Opens
  `ArtSearchWindow`, which shows covers from the iTunes Store, the Cover Art Archive
  and (with a key) fanart.tv. The user's rules: **JPEG only, at least 1,000 × 1,000**.
  - **Everything is in `AudioFool.Core/Art/`.** `OnlineArtSearch` does the HTTP and
    the parsing. `JpegSize` reads a JPEG's frame header. Both are covered by 27 tests
    in `OnlineArtSearchTests`, all offline.
  - **Every candidate is measured before it is shown.** The app fetches the first
    64 KB with a range request (512 KB if the frame header lies further in) and reads
    the size from the bytes. Format is judged by magic bytes, not the URL. The chosen
    cover is downloaded in full and checked again before `UseDownloadedArt` takes it.
    All three hosts were probed and answer range requests with 206.
  - **iTunes**: `artworkUrl100` is rewritten to `/10000x10000bb.jpg`. The store never
    upscales, so that returns the original size (1400–5000 px measured). The `.jpg`
    suffix makes it serve a JPEG even when the label uploaded a PNG. The old
    `100000x100000-999` trick now returns 400. If "artist album" finds nothing (a
    title the store spells differently), the search falls back to the artist alone.
  - **MusicBrainz**: one release-group search, score ≥ 90, at most 3. It feeds both the
    Cover Art Archive (front covers only) and fanart.tv
    (`/v3/music/albums/{rgid}`). It returns 503 when rate-limiting, which happened
    once in testing, so the app retries once after 1.2 s. The User-Agent is
    `AudioFool/1.0 (personal music player)`, with no email address in it.
  - **fanart.tv needs `FanartTvApiKey` in settings.json** and returns 401 without
    one; with no key, the status line says it was skipped. The user added a key in
    session 13 and **confirmed it in the running app**: *A Perfect Circle – Mer de
    noms* returned three fanart.tv covers, each measured at 1,000 × 1,000, beside
    iTunes and the Cover Art Archive. ThemeLab cannot check this — see the
    packaged-AppData gotcha below.
  - **Bandcamp is not searched.** Its search page returns a JavaScript "Client
    Challenge" to non-browsers. That is a bot check, and getting around it is not
    something to build.
  - **Relevance** (`OnlineArtSearch.Relevance`): the artist match counts 2 and the
    album match 1. Names are compared letters and digits only, ignoring case,
    accents and a leading "The", and either name may contain the other. With an
    artist to go on, a cover must match the artist, which drops karaoke versions
    and other artists' records that share a title (150cc's *Live Recordings* had
    returned Bob Dylan). The grid keeps itself sorted by relevance, then by pixel
    count.
  - **Non-square covers pass** if both sides are at least 1,000. The Cover Art
    Archive's *Goodbye Yellow Brick Road* includes a 1514 × 2140 DVD sleeve.
  - **The final status goes through the dispatcher.** ThemeLab pumps the dispatcher
    by hand, with no synchronization context, so code after `await` ran on the
    thread pool, and queued result callbacks overwrote the summary. The real app is
    unaffected, but the summary is now queued behind them either way.
  - **ThemeLab `--window artsearch --artist X --album Y [--use 1]`** runs a live
    search, renders the window and prints every result. `--use 1` downloads the top
    cover and passes it through `UseDownloadedArt` / `PickedArtPayload`. Verified:
    Rush *Moving Pictures* gives 3000 × 3000, `image/jpeg`, 922,550 B.
    **Pipe ThemeLab's output** (`| Out-File`): with `>` PowerShell does not wait
    for the process to exit, and with `| Select-Object -First` the process dies
    before it saves the PNG.

**Release dates in the Year box** (later in session 12). The box accepts `2026`,
`2026-10` or `2026-10-02` and is 140 px wide, to fit a full date plus WPF-UI's clear
button.

- **The old code destroyed dates.** `tag.Year = 2014` rewrites a FLAC's
  `DATE=2014-05-01` as `2014`, so every save cut a full date down to its year. A
  600-file sample found full dates on **36% of FLACs** (154 of 427). Any album saved
  in the dialog since session 6 has lost them.
- **For the dialog, the date is read from the file when it opens**, like the detail
  fields. (The album sort that came after it caches the date too; see below.)
  `Track.Year` stays an int. `TagDetails.Date` is an
  init-only member, `ReleaseDate` does the parsing, and `TagReader.ReadDate` reads
  each format's own field (Xiph `DATE`, ID3v2.4 `TDRC`, MP4 `©day`, APE `Year`,
  ASF `WM/Year`). The edit records gained `Date`, and `TagWriter.WriteDate` sets
  `Tag.Year` first, then writes the full date over it in those same fields.
- **The album dialog prefills a date only when every track has the same one.**
  Otherwise it shows the album's year. Year is still written album-wide.
- **TagLib bug: ID3v2.3 `TDAT` is written month-first** (`1025` for 25 October),
  although the spec says DDMM, and it is read back the same way. A correct `2510`
  written by hand came back as `2026-25-10`. TagLib's own round trip hides this, so
  it was found by reading raw frames. The workaround: a file given a full date is
  upgraded to ID3v2.4, which stores `TDRC` whole, and dates are read from v2.4
  only; v2.3 files show the year. `Mp3_full_date_is_stored_as_id3v24_tdrc` checks
  the bytes on disk. Verified on copies of a real MP3 and a real DSF, the DSF by
  following the metadata pointer at header offset 20.
- **The library now holds only FLAC, MP3 and DSF.** `library.json` still lists 2
  m4a and 10 wav files that are no longer on the drive. The MP4, APE and ASF date
  paths compile but have never met a real file.

**Albums sort by release date** (later still in session 12). The user noticed that
*Ephemeral Dance* (2026-10-02) sorted above *Remixes + More* (2026), alphabetically
within the year.

- **`Track.ReleaseDate`** is cached: the normalised date when the file names more
  than a year, null otherwise, and omitted from JSON when null. `Track.SortDate` is
  `ReleaseDate ?? "yyyy"`. `Album.SortDate` is the earliest across the album's
  tracks, matching how `Album.Year` takes the earliest year. `SortRules.SortAlbums`
  orders on it as ordinal text: ISO dates of mixed precision sort correctly that
  way, and a bare `2026` comes before `2026-10-02`. `WithTags`, `WithAlbumTags` and
  `Relocated` carry the date, so a save re-sorts the album list at once.
- **`LibraryCache.CurrentVersion` is 3, but a version 2 cache is still loaded.**
  Unlike the session 9 bump, it is shown straight away. `NeedsReread` passes
  `rereadTags: true` to `LibraryScanner.ScanAsync`, which re-reads every reachable
  file even when it matches, while still counting known files as seen, not
  removed. The window is never empty. The status line reads "Updating the library
  for this version... n of m files".
- **Three rules keep the re-read from being skipped.** Drive relocation (which runs
  *before* the scan) and `PersistLibraryAsync` (after a tag save) keep the loaded
  cache's version rather than stamping it current. A re-read that finds a folder
  unreachable also saves the old version, because the tracks carried over from
  that drive still lack the date.
- **Measured headless on the real library**, read-only: the re-read took **51.2 s**
  and read 26,694 files. 6,934 tracks carry a full date, and the album order
  changes for 63 of 494 artists. It also dropped 124 cache entries for files no
  longer on the drive (the m4a and wav files above among them). Cartoon Theory now
  ends `… FEEL [2022] | Remixes + More [2026] | EPHEMERAL DANCE [2026-10-02]`.
  **Not yet seen in the running app**: the first launch of this build does the
  re-read for real.

**218 tests pass**: 153 before session 12, 27 in `OnlineArtSearchTests`, 29 in
`ReleaseDateTests` and 9 in `ReleaseDateSortTests`.

### Changes from session 13

- **Taskbar thumbnail buttons**, as MusicBee has: hover the taskbar icon for
  **Previous, Play/Pause, Next, Stop**. `Services/TaskbarControls.cs` builds a
  `TaskbarItemInfo` with four `ThumbButtonInfo`s bound straight to the view model's
  existing commands, so they behave exactly like the window's transport (Previous
  restarts after 3 s; Play with nothing loaded starts the selected album). Created in
  the `MainWindow` constructor, disposed in `OnClosed`.
- **Play/Pause swaps glyph on `IsPlaying`**, through the view model's
  `PropertyChanged` in code, not a binding. `ThumbButtonInfo` is a `Freezable` hung
  off the window, and code-behind is simpler than relying on its inheritance context.
- **The glyphs are `DrawingImage`s on a 16-unit canvas**, not WPF-UI symbols: a thumb
  button wants an `ImageSource`, and WPF renders a `DrawingImage` at the system
  small-icon size for the current DPI, so they stay sharp. Each carries a transparent
  16×16 square, because a `DrawingImage` is sized to its content's bounds and would
  otherwise stretch every glyph to the edges.
- **Glyph colour follows the taskbar, not the app theme** — the flyout is drawn by the
  shell. `SystemUsesLightTheme` under `HKCU\...\Themes\Personalize` picks black or
  white (missing means dark), re-read on `SystemEvents.UserPreferenceChanged`
  (`General`).
- **The window title follows the track.** `MainViewModel.WindowTitle` is "Artist –
  Title" (just the title with no artist tag), "AudioFool" when nothing is loaded;
  `Title="{Binding WindowTitle}"`. The in-app `TitleBar` never displayed the window
  title, so the app looks the same; it is what the thumbnail heading, the taskbar
  tooltip and Alt-Tab read. Nothing else keyed on the fixed title — but a UIA script
  that finds the window **by name** would now miss it; find it by process id, as the
  snippets here already do.
- **Verified**: the glyphs rendered off-screen at 24 px and reviewed; the relaunched
  app read "AudioFool" through UIA with nothing loaded. The flyout itself cannot be
  seen without hovering the taskbar, which is off limits, so **the user confirmed**
  the buttons, the Play/Pause swap and the track title in the running app.
- **A selected track row no longer shifts right.** PS1's 3 px selection bar was a
  row `BorderThickness`, set only on selection, so it took layout space and pushed
  every cell of that row right by 3 px. The bar is now drawn by `IndicatorColumn`'s
  own cell style, a `Border` overlaid on the cell (focus still brightens it). Dark
  and Vista are unaffected (thickness 0). Verified with ThemeLab: the same row
  selected and unselected has identical glyph start positions, to ±1 px of
  anti-aliasing.
- **Edit tags on several selected tracks.** The track grid is
  `SelectionMode="Extended"` (Ctrl/Shift-click, Ctrl+A). "Edit Tags…" on a row
  that is one of several selected opens the dialog for all of them; a right-click
  on an unselected row replaces the selection first, as Explorer does. The window
  mirrors `TrackGrid.SelectedItems` into `MainViewModel.SelectedTracks` on
  `SelectionChanged`, since a DataGrid's `SelectedItems` cannot be bound.
  - **Every field is keep-unless-changed**, Artist / Album Artist / Album / Year
    included (the album dialog always writes those four). A hand-picked set has no
    reason to agree on them. Each pre-fills only when every selected track agrees,
    else "Varies". Album may stay empty when it varies; emptying one that was there
    is refused.
  - **No art** (`TagEditViewModel.ShowsArt` is album-only): it rewrites the folder
    cover, which is the whole album's.
  - **Core**: `TracksTagEdit` (all optional; `DateEdit` carries year plus date),
    `TagWriter.WriteSelectedTrackTags`, `Track.WithSelectedTags`. 7 tests in
    `SelectedTracksTagTests`. `TagEditViewModel(IReadOnlyList<Track>)` sets
    `IsAlbumMode` *and* `IsSelectionMode`; `IsAlbumMode` now means "several tracks".
  - **Verified** with ThemeLab: `--window tags --album "Goodbye Yellow Brick Road"
    --pick 1-20` opens it on 20 real tracks across both discs (disc # and track
    count "Varies", art hidden, an untouched Save writes nothing, `DiscNumber=1`
    writes only that). `--rows 1,2,3,4` selects four grid rows, and all four reach
    `SelectedTracks`. The right-click in the running app was **not** exercised
    (it needs the mouse).
- **The old track no longer plays on under a new one.** Starting a track directly
  (a new album, Next/Previous, a double-click) went through `PlayCore`, which
  unplugged the old stream from the mixer but left the WASAPI buffer
  (`BufferSeconds`, 0.2 s) full of audio already pulled from it. `PlayCore` now
  flushes the device (`OutputChain.Stop`, i.e. `BassWasapi.Stop(true)`) before the
  teardown. **A paused device cannot be flushed**: `Stop(true)` on a stopped
  device returns false and discards nothing, so when paused `PlayCore` disposes
  the chain and `EnsureOutput` reopens it. The gapless handover
  (`OnCurrentStreamEnded`) does not go through `PlayCore` and is unchanged.
  - **Measured with a headless probe** (`scratchpad/stale`), not reasoned. The
    probe mutes its own Windows audio session (`BassWasapi.SetMute(Session)`)
    but keeps the engine volume at 1, so BASSWASAPI's buffer holds real samples
    while nothing reaches the speakers. Track B is the silent test fixture, so
    any `BassWasapi.GetLevel` after B starts is track A. Before: A audible for
    **~205 ms** after B, playing or paused. After: **none** in either case;
    `Play` returns in 7 ms, or 28 ms from pause (the reopen). This is the
    technique for any "what is actually coming out" question: zero gain would
    make silence and stale audio indistinguishable.
  - Shared mode only. Exclusive mode uses the same flush but was not probed,
    because it would take the DAC.
- ~~**Relaunch minimised** after installing~~. **Wrong, corrected in session 15:
  never launch AudioFool for the user from the shell.** A process started from
  Claude's shell runs inside the Claude app's package container, so it reads the
  stale private `settings.json` copy (see the packaged-AppData gotcha). In session
  15 a relaunched app came up with the Vista theme and no Last.fm session, and
  had to be closed. Minimised was fine; the settings were not. After installing,
  ask the user to open it from their shortcut. A launch purely as a smoke test
  (does it start?) is still fine, provided it is closed straight after.

### Changes from session 14

- **In-place editing of #, Song, Artist and Album in the track grid.** Opened as
  a rename is in Explorer: **F2**, or a **second single click** on the one row
  already selected (after the double-click time, so a double-click still just
  plays). **Enter** saves and moves down a row, keeping the column, so F2, Enter,
  F2 walks an album's track numbers. **Esc** cancels. Clicking elsewhere in the
  window saves; switching to another app leaves the edit open.
  - **Nothing else opens an edit.** The grid's own "click a selected cell" would
    open one on the first half of every play double-click, and typing would start
    rewriting a tag on what looks like a list. `TrackGrid_BeginningEdit` cancels
    every edit except the slow click's (`_openingEdit`) and F2 with one row
    selected. F2 is recognised by having **no** `EditingEventArgs`: DataGrid
    routes it through `BeginEditCommand`, while its click and typing paths each
    pass their input event.
  - **`InlineEditColumn`** (`src/AudioFool/InlineEditColumn.cs`) is a
    `DataGridTextColumn` for the four columns. **`DataGridBoundColumn` coerces any
    column with a one-way binding to read-only**, and these must be one-way:
    `Track` is immutable and Song shows `DisplayTitle`, which has no setter. The
    subclass drops that coercion. The typed text never goes back through the
    binding: `TrackGrid_CellEditEnding` takes it from the edit box and calls
    `MainViewModel.ApplyInlineEditAsync`.
  - **Core**: `InlineTagEdit.Build(track, field, text)` turns the text into a
    `TracksTagEdit` for that one field, or nothing when it is unchanged, or an
    error. `TracksTagEdit` gained `Title` and `TrackNumber`, which only this path
    sets. The rules: trimmed; a title-less row starts with its file name and is
    not written unless changed; an empty # clears it (the count stays); # must be
    a whole number ≥ 1; Album cannot be emptied. Errors show as "Not saved: …".
  - **The grid cannot keep an edit open while its rows are cleared.**
    `MainViewModel.TracksChanging` is raised in `OnSelectedAlbumChanged`, the one
    place `Tracks` is cleared (album change, scan, save), and the window cancels
    any open edit. Inline saves are queued one at a time (`_inlineSave`), each
    applied to the library's current copy of the track, so two quick edits to one
    row don't undo each other in memory.
  - **`NowPlaying` is swapped for the updated track** in `ReplaceTracksInLibrary`.
    The now-playing note and the window title compare by reference, so the note
    had been disappearing after *any* tag save of the playing track, the dialogs
    included.
  - **The edit box is `AfCellEditBox`** in `Components.xaml` (right-aligned
    `AfNumericCellEditBox` for #): a template of its own, since WPF-UI's text box
    doesn't fit in a row and a bare style falls back to Aero's white box.
  - **Verified with ThemeLab `--window edit`** (below) in all three themes, on
    FLAC and MP3. The gates hold (click, typing, read-only column, two rows). F2
    and the slow click open an edit. Enter saves without playing. `abc` and an
    empty album are refused. Leaving the grid saves. A rebuild mid-edit cancels
    without throwing. Re-reading the file shows only the edited field changed, and
    a new album moves the track. **Not exercised in the running app**: the real
    mouse (the slow click was driven through `ArmSlowClick`) and a real keyboard.

**243 tests pass**: the 225 from session 13 plus 18 in `InlineTagEditTests`.

### Changes from session 15

- **Last.fm scrobbling.** Logo menu → **Last.fm…** (below Statistics). The user
  chose: an in-app dialog for the key, scrobbles plus now playing, and track artist
  with album artist sent separately. User-facing behaviour is in the README.
  - **Everything but the dialog is in `AudioFool.Core/Scrobbling/`**, BASS-free.
    `LastFmApi` signs calls: sort the parameters ordinally, concatenate each name
    and value, append the secret, MD5 of the UTF-8, excluding `format`. `PlayTracker`
    applies the rules. `ScrobbleQueue` is the offline file. `LastFmScrobbler` joins
    them.
  - **Auth is the desktop flow.** auth.getToken, then the browser opens
    `last.fm/api/auth`, then the view model polls auth.getSession every 3 s for
    5 min. Error 14 means not approved yet. The user's own API account is needed,
    since Last.fm issues keys per application. Key, secret, session key and user name
    go in `settings.json` (`LastFm*`). **My shell can't see that file** (the
    packaged-AppData gotcha), so ask the user whether they're connected.
  - **Listening time comes from the audio position, not a clock.** `MainViewModel`'s
    250 ms position tick calls `Advance(engine.Position, engine.CurrentTrack)`. Only
    forward steps ≤ 3 s count, so seeks, pauses and sleep add nothing. A jump back
    to under 3 s from past 3 s is a new play, because **the engine raises no event
    for a Repeat-One loop or a Previous that restarts**. The same file raising
    `TrackChanged` before it has scrobbled continues its play: an output-mode switch
    goes through `JumpTo` and would otherwise throw the time away.
  - **Ticks whose `CurrentTrack` isn't the tracker's are skipped.** A gapless
    handover swaps streams on the mixer thread, then posts `TrackChanged`. The tick
    in between reads the new track's ~0 s against the old one, which looked like a
    restart.
  - **Scrobbled at the threshold, not at track end**, so closing the app during
    the second half doesn't lose the play. Each is queued to disk first, then sent
    in batches of up to 50.
    - A failure backs off from 1 min, doubling to 30 min. The next tick after that
      retries, and so does a new scrobble.
    - Error codes 4/9/10/13/26 set `NeedsReconnect`. That stops sending, keeps the
      queue, and puts a status-bar message up once.
    - Per-scrobble ignored code 5 (daily limit) stays queued. Other ignored codes
      are dropped.
    - Entries older than 14 days are dropped on load, because Last.fm refuses them.
  - **Verified**:
    - 33 unit tests (`ScrobblingTests`), including the fake-HTTP scrobbler paths.
    - The live endpoint with a fake key: error 10 comes back as an auth failure.
    - A headless probe (`scratchpad/scrobprobe`) driving the real `AudioEngine`
      silently (shared mode, volume 0) over two real 41–44 s FLACs. Repeat One for
      100 s gave now playing ×3 at 44 s intervals and scrobbles ×2, each at 22 s
      with that loop's start time. A → B gapless gave exactly one now playing and
      one scrobble per track.
    - ThemeLab `--window lastfm` rendered all five states in Dark, plus PS1 and
      Vista.
    - The installed build launched minimised with no errors (a smoke test, closed straight after).
  - **Confirmed by the user in the running app**: connected with their own API
    account, and scrobbles appear on their Last.fm profile. `scrobbles.json` then
    held 0 pending, rewritten at 08:06, so each play went to disk and was removed
    once sent.
  - **Caught by ThemeLab**: the menu icon was first `Broadcast24`, which doesn't
    exist in WPF-UI 4.3. It compiled, but `MainWindow` threw at load, so the app
    would not start. `SymbolIcon` names are only checked at runtime. Render
    (or launch) after touching one. It is `Live24` now.

**A Last.fm indicator in the status bar** (later in session 15), left of
Bit-Perfect, as the user asked.
- It is a small `ui:Button` with a dot and a word, and the word changes with the
  state, so colour is never the only signal:
  - "Last.fm" (green)
  - "Last.fm: off" (idle grey)
  - "Last.fm: 3 waiting" (caution)
  - "Last.fm: reconnect" (danger)
- It is hidden until connected, and clicking it opens the Last.fm window.
- The state is `LastFmScrobbler.State` (`ScrobblerState`) in Core. **"Waiting"
  needs a failed send**, not just a non-empty queue: every scrobble sits in the
  queue for a moment before it goes, and that would flicker after every track.
  `Enabled` now raises `StatusChanged`.
- Visibility binds a plain bool (`IsLastFmShown`), not a `Style` on the button.
  A local style would need `BasedOn` WPF-UI's, the trap in the gotchas.
- **Verified** with ThemeLab `--lastfm scrobbling|off|failing|reconnect` (plus
  none), in all three themes, cropped and reviewed. It swaps the view model's
  scrobbler by reflection for one with a scratch queue: **the view model's own
  scrobbler reads the real `scrobbles.json`**, so a ThemeLab mode must never add to
  it.
- **Confirmed by the user** in the running app (PS1 theme): green dot and
  "Last.fm" beside Bit-Perfect. The other states have only been seen in ThemeLab.

**277 tests pass**: the 243 from session 14 plus 34 in `ScrobblingTests`.

**Cover size and format in the album dialog** (session 16). Under the 72 px art:
"1200 × 1200" and "JPG" or "PNG".
- **Read from the bytes, not a name.** `AudioFool.Core/Art/ImageInfo.cs` uses
  `JpegSize` for JPEG and the IHDR chunk for PNG. Anything else shows "Unknown
  format".
- **It describes the cover the preview shows.** That is embedded art in the first
  of five tracks that has it, else the folder file, which is the order
  `AlbumArtService` uses. The tooltip says which ("Embedded in the tracks" /
  "Folder file: cover.jpg").
- **A picked or downloaded cover replaces the text** with the new image's figures,
  and the tooltip then says it is written on Save.
- The buttons sit in a 72 px `Grid`, so they stay centred on the art rather than
  on the art plus its two lines.
- **Verified** with ThemeLab `--window tags --album`: *Saturn Return* reads
  1200 × 1200 JPG, and *Goodbye Yellow Brick Road* reads 1000 × 1000 JPG.
  `--useart <file>` (new) with the 1280 × 974 PlayStation logo gave "1280 × 974
  PNG". 7 tests in `ImageInfoTests`, 284 in total (**299** after the
  leading-zeros changes below).

**The art viewer's caption adds the format too**: "150cc - Live Recordings · 1500 ×
1500 · JPG".
- **This also fixes a size bug.** The caption used to print the decoded bitmap's
  size, and the full-size decode is capped at 2,000 px wide (`MaxViewerWidth`). So a
  3000 × 3000 cover read as 2000 × 2000.
- `AlbumArtService` now reads `ImageInfo` from the same bytes it decodes.
  `DecodeFileFull` reads the file whole and goes through `DecodeBytesFull`.
- It keeps the result in a `ConditionalWeakTable` keyed on the bitmap, and
  `InfoFor(bitmap)` returns it. The cache and its signatures are unchanged.
- **Verified** with the new ThemeLab `--window artview --album X` (or `--artfile
  <image>`), which prints the real viewer's caption:
  - *Saturn Return* reads 1200 × 1200 JPG, and *Live Recordings* 1500 × 1500 JPG.
  - Scratch 3000 × 2400 JPEG and PNG covers decode at 2000 × 1600 but are
    captioned 3000 × 2400 JPG and PNG.

**Leading zeros in the tag dialogs** (session 16). The user fixes "01" to "1" by
hand, so the dialogs must show the zero. The grid still shows the bare number.
- **The dialogs show numbers as the file spells them.** `TagReader.ReadDetails`
  gained `Numbers` (`NumberTexts`: track #, track total, disc #, disc total). They come
  from Xiph `TRACKNUMBER` / `TRACKTOTAL` / `DISCNUMBER` / `DISCTOTAL`, ID3v2 `TRCK` /
  `TPOS` ("01/12"), or APE `Track` / `Disc`. A spelling is used only when it is plain
  digits naming the number TagLib parsed; otherwise the number is shown. The cache
  still holds ints, so nothing changed there and no version bump was needed.
- In the album and selection dialogs, a mix of "01" and "1" reads as "Varies". Typing
  "1" over "01" counts as a change, because keep-unless-changed compares text.
- **TagLib pads every track number it writes**, and has no switch to stop it. Setting
  `Tag.Track = 1` writes `TRACKNUMBER=01` (FLAC) and `TRCK=01/12` (MP3). Setting only
  `TrackCount` rewrites an MP3's `TRCK` padded too. Before this change, every save
  from AudioFool padded track numbers. Disc numbers TagLib writes plainly.
  `TagWriter.WriteNumbers` is now the one place numbers are set. It respells Xiph
  `TRACKNUMBER` (when the number was set) and ID3v2 `TRCK` (when the number or total
  was) plainly afterwards.
- **The track dialog writes every number on Save**, as it always has. So saving any
  change there also unpads that track's "01" track or disc. The album dialog touches
  only the fields you change.
- **Measured** on an 800-file sample of the real library: 9 of 216 MP3s have a padded
  track number, and 6 of 584 FLACs a padded disc number. Examples: Lotus *Hammerstrike*
  (MP3, `02/10`), Magdalena Bay *A Little Rhythm and a Wicked Feeling* and Röyksopp
  *The Inevitable End* (FLAC, disc `01`).
- **Verified**: 11 tests in `NumberSpellingTests` read raw fields back from the files
  on disk. ThemeLab `--window tags` on those three real releases shows "02" of 10,
  "08" / "01", and "01" of "01". `--set "DiscNumber=1;DiscCount=1"` on *The Inevitable
  End* would write only those two. **Not changed:** F2 on the grid's # column starts
  from the cached number, so "01" shows as "1" there, and typing "1" counts as
  unchanged. Use the track dialog to fix those.

**Remove Leading Zeros in Edit Album** (session 16, at the user's request). A
"Numbers" row under Disc, in the album dialog only (`ShowsRemoveLeadingZeros`; not
in the several-tracks dialog).
- **It is enabled only when the album has a padded number.** Beside it: "45 of 89
  tracks", "None found", or "Removed on Save" once pressed. The count is made from the
  same `NumberTexts` the boxes are filled from.
- **Pressing it strips the zero from the three boxes** (track count, disc, disc
  total). A box is filled only when every track agrees, so saving that text to all
  of them is safe. It also sets `AlbumTagEdit.RemoveLeadingZeros`.
- **The writer handles the rest of each track.** It re-sets each number the file
  already has to the same value through `WriteNumbers`, which spells it plainly.
  That covers track numbers, which the album dialog has no box for, and fields
  shown as "Varies". A number the file lacks stays missing. A count or disc edit in
  the same save wins for its field.
- **Verified**: 4 tests in `NumberSpellingTests` (299 in total). ThemeLab's `--set`
  gained `^Zeros` to press the button. *Donkey Kong Country: Tropical Freeze* (MP3,
  89 tracks) reads 45 of 89. *The Inevitable End* goes to disc "1" of "1" with
  `remove zeros=True`. *Saturn Return* is disabled, and pressing it anyway writes
  nothing. Rendered with the album title at full length; the row fits 460 px.
- **The label first sat beside Track Count** and was cut off at 460 px, which is
  why it has a row of its own.
- **Lotus *Hammerstrike* is no longer padded.** The user fixed tracks 1–9 in Mp3tag
  on 2026-09-28, 13:13 to 13:16. That is why `library.json` was not rewritten then.
  The user tags with Mp3tag as well as AudioFool, so a file on the drive can change
  without the app knowing. The next scan's size and time check picks it up.

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
| `src/AudioFool.Core/Library/TagEdit.cs` | `TrackTagEdit`, `AlbumTagEdit`, `ArtPayload` — immutable records describing a pending write. Their positional fields are authoritative. Since session 11 their init-only members (`NumberEdit?` counts, `TagDetailsEdit`) use null for "keep", and `TagDetails` holds what a file currently has for the five detail fields. |
| `src/AudioFool.Core/Library/TagWriter.cs` | Static write layer: `WriteTrackTags`, `WriteAlbumTrackTags`, `WriteFolderArt`. Never throws — all results are `TagWriteResult`/`FolderArtWriteResult` records. Re-stamps `FileSize`/`ModifiedUtc` via `FileStamp.For(path)` after each write so `Track.MatchesFile` stays correct. |
| `src/AudioFool/ViewModels/TagEditViewModel.cs` | Backs `TagEditWindow` for both single-track and album-batch modes. `IsAlbumMode` flag drives which fields are visible. `BuildTrackEdit()` / `BuildAlbumEdit()` / `PickedArtPayload()` are read after `ShowDialog() == true`. |
| `src/AudioFool/TagEditWindow.xaml[.cs]` | Modal dialog (FluentWindow/Mica, owner-centered). Save/Cancel in code-behind set `DialogResult`. Album mode hides Title and the Track # row and shows the art panel and a standalone Track Count, via `Visibility` bindings. Disc and the five detail rows (each with a ✕ clear button) are in both modes. |
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

## New source files added in session 12

| File | Purpose |
|---|---|
| `src/AudioFool.Core/Art/JpegSize.cs` | Reads a JPEG's pixel size from its frame header (SOF0–SOF15, skipping DHT/JPG/DAC). `NotJpeg` / `NeedMore` / `Found`. |
| `src/AudioFool.Core/Art/OnlineArtSearch.cs` | The online cover search: the iTunes, MusicBrainz, Cover Art Archive and fanart.tv requests and parsers, range-request measuring, the size and format rule, and relevance ranking. Its parsers are public static so they can be tested offline. |
| `src/AudioFool.Core/Library/ReleaseDate.cs` | Parses and normalises `yyyy`, `yyyy-mm` and `yyyy-mm-dd`, strictly when typed and leniently from tags (it drops a time part). `HasMonth` decides whether a date field is needed. |
| `src/AudioFool/ArtSearchWindow.xaml[.cs]` | The Search Internet results window. Its item style is its own, with an accent ring rather than WPF-UI's filled selection. |
| `src/AudioFool/ViewModels/ArtSearchViewModel.cs` | Runs the search, inserts results in sorted order, loads previews, and downloads the chosen cover. Also `Decode`, used by the tag dialog for its preview. |
| `tests/AudioFool.Core.Tests/OnlineArtSearchTests.cs` | 27 tests: JPEG measuring, the size and format rule, URL rewriting, each source's parser, relevance. |
| `tests/AudioFool.Core.Tests/ReleaseDateTests.cs` | 29 tests: parsing, and date round trips on FLAC and MP3, including the raw-bytes ID3v2.4 check. |
| `tests/AudioFool.Core.Tests/ReleaseDateSortTests.cs` | 9 tests: album order by date, earliest-track dating, the v2 cache re-read. |

## New source files added in session 13

| File | Purpose |
|---|---|
| `src/AudioFool/Services/TaskbarControls.cs` | The four taskbar thumbnail buttons: builds the `TaskbarItemInfo`, draws the glyphs, swaps Play/Pause on `IsPlaying`, recolours them when the taskbar's light/dark mode changes. |

## New source files added in session 14

| File | Purpose |
|---|---|
| `src/AudioFool.Core/Library/InlineTagEdit.cs` | `InlineField`, `InlineEditResult`, and `InlineTagEdit.Build` / `InitialText`: one cell's text to a one-field `TracksTagEdit`. |
| `src/AudioFool/InlineEditColumn.cs` | `DataGridTextColumn` that stays editable with a one-way binding. |
| `tests/AudioFool.Core.Tests/InlineTagEditTests.cs` | 18 tests: unchanged text, the file-name title, each field alone, bad numbers, empty album, round trips on FLAC and MP3. |

## New source files added in session 15

| File | Purpose |
|---|---|
| `src/AudioFool.Core/Scrobbling/LastFmApi.cs` | Signed Last.fm 2.0 calls: getToken, getSession, updateNowPlaying, scrobble (batches ≤ 50). `LastFmException` classifies error codes. `ParseScrobbleOutcomes` reads per-scrobble ignored codes. |
| `src/AudioFool.Core/Scrobbling/ScrobbleEntry.cs` | One play as Last.fm sees it. Also the queue file's format. `From` refuses tracks with no title or artist. |
| `src/AudioFool.Core/Scrobbling/PlayTracker.cs` | The scrobble rule, counted from position steps. Pure, and every time is an argument. |
| `src/AudioFool.Core/Scrobbling/ScrobbleQueue.cs` | `%LOCALAPPDATA%\AudioFool\scrobbles.json`, saved atomically on every change, 14-day prune on load. |
| `src/AudioFool.Core/Scrobbling/LastFmScrobbler.cs` | Tracker → now playing, queue, batched flush, backoff, reconnect state. Takes a `TimeProvider` and an API factory for tests. |
| `src/AudioFool/ViewModels/LastFmViewModel.cs` | Connect (token, browser, poll), disconnect, the scrobbling switch, the queue status line. |
| `src/AudioFool/LastFmWindow.xaml[.cs]` | The modal dialog: setup panel or connected panel. |
| `tests/AudioFool.Core.Tests/ScrobblingTests.cs` | 34 tests: signing, entries, the rules (seek, pause, loop, same-file restart, short tracks), response parsing, queue, and the scrobbler against a fake HTTP handler. |

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

**Session 11 added "keep" semantics to part of the edit.** The rule to remember is
this: an album edit must never write a value the user did not choose to every track.
Artist, Album Artist, Album and Year are the old exception — always written
album-wide, pre-filled from the first track. Everything added since follows three
rules:

- **Pre-filled only when every track agrees.** Otherwise the field shows a "Varies"
  placeholder (`Shared` in `TagEditViewModel`).
- **Written only when the text changed.** `_initial` holds what each box was
  pre-filled with; `IfChanged` / `NumberIfChanged` return null ("keep") when the
  text still matches.
- **Clearing a varying field needs the ✕ button** (`ClearFieldCommand` → `_cleared`),
  because its box was already empty.

The five detail fields come from `TagReader.ReadDetails` when the dialog opens, not
from `Track`. `TagWriter.ApplyDetails` writes "" as null, which removes the frame.
`WithAlbumTags` applies the count and disc edits, and since session 12 the release
date, to the in-memory `Track` too, so neither the Disc column nor the album order
shows stale values until the next scan.

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
ThemeLab.exe --window tags --album "Saturn Return" --w 460   # album dialog on a real album
ThemeLab.exe --window tags --track "Polygon Weather"         # track dialog on a real track
ThemeLab.exe --window tags --album "Saturn Return" --set "!Comment;Genre=Ambient"
ThemeLab.exe --window tags --album "The Inevitable End" --set "^Zeros"   # press Remove Leading Zeros
ThemeLab.exe --window artsearch --artist "Rush" --album "Moving Pictures" --use 1 --w 820 --h 640
ThemeLab.exe --theme PS1 --artmenu 1          # the album header art's context menu
ThemeLab.exe --window tags --album "Goodbye Yellow Brick Road" --pick 1-8 --set "DiscCount=2"  # a grid selection
ThemeLab.exe --theme PS1 --rows 1,2,3,4       # several selected grid rows
ThemeLab.exe --window edit --theme PS1 --w 1300 --h 600   # in-place grid edits
ThemeLab.exe --window lastfm --state connected --w 480    # setup|waiting|connected|failing|rejected
ThemeLab.exe --theme PS1 --w 1300 --h 700 --lastfm failing   # the status-bar indicator
```

`--window lastfm` uses a scratch queue and a canned HTTP handler, so nothing
reaches Last.fm and the throwaway settings are never saved.

`--window edit` builds a library of four scratch copies of the test fixtures (three
FLAC, one MP3) and drives in-place edits through the real grid. That means real
key events, the slow click through `ArmSlowClick`, and real saves. It prints what
reached each file and renders the open edit box. **A save persists
`library.json`, and `LibraryCache.CachePath` is the real one.** A first run shrank
it to 1.7 KB. So this mode copies it aside first and restores it byte for byte in
a `finally`, then deletes the scratch tracks. It also installs a
`DispatcherSynchronizationContext`, without which the save's continuation runs
on the thread pool (see the art-search note) and the grid never updates.

`--window tags --album/--track` reads the real files' tags (read-only) and prints
every prefill and placeholder. `--set` types into the boxes (`Name=value`) or presses a
clear button (`!Name`), then prints what Save *would* write, with `(keep)` for untouched
fields. Nothing is saved. Good test albums: *Saturn Return* (comments vary per track),
*Goodbye Yellow Brick Road* (track count and disc vary), *Chronicles* (disc varies,
no disc total).

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
- **A commit message with double quotes silently fails from Windows PowerShell 5.1.**
  A here-string passed to `git commit -m` is split at each embedded `"`, git reads
  the rest as pathspecs, and nothing is committed, although the `push` after it
  still reports "Everything up-to-date". Write the message to a scratchpad file and
  use `git commit -F <file>`.
- **The sandbox guard also blocks `Remove-Item` on a path held in a variable**
  (`Remove-Item "$d\files\*"`), not only in combination with the PATH assignment.
  Overwrite with `Copy-Item -Force` instead, or spell the path out.
- **PowerShell does not wait for a GUI-subsystem exe redirected with `>`.** ThemeLab
  returned at once with an empty log and no PNG. Pipe it (`| Out-File`) instead, but
  not into `Select-Object -First`: that ends the pipe early and kills the process
  before it saves.
- **TagLib's ID3v2.3 date handling is wrong**: it writes `TDAT` month-first and reads
  it the same way, so a TagLib round trip passes while other players see day and
  month swapped. Check the bytes on disk. See *Release dates* under session 12.
- **Claude's shell does not see the real `settings.json`.** The Claude desktop app
  appears to run packaged (MSIX), and Windows gives its processes a private copy of
  `AppData\Roaming` files. In session 13, Explorer showed
  `%APPDATA%\AudioFool\settings.json` saved at 10:09 that day with a fanart.tv key.
  The same path read from the shell (sandboxed or not) and by ThemeLab was a 260-byte
  copy from 31 August, with a different theme and folder list and no key. So anything
  that reads settings from here — ThemeLab, a headless probe, a `Get-Content` — sees
  stale settings, and a write goes to the copy, not the app. `AppData\Local`
  (`library.json`) read current. To check a setting, ask the user or verify in the
  running app.
- **The same applies to AudioFool launched from the shell.** It inherits the
  container and runs on the stale settings: wrong theme and folders, no Last.fm
  session. A status-bar indicator that "wasn't there" in session 15 was this, not
  a bug. It is also why a UIA check of a shell-launched app says nothing about
  the user's settings.
- **TagLib writes track numbers zero-padded** ("01", "01/12") with no option to
  turn it off. Set numbers through `TagWriter.WriteNumbers`, never `tag.Track`
  directly. See *Leading zeros* under session 16.
- **`library.json` can list files that no longer exist.** It still held 2 m4a and
  10 wav files deleted since the last full scan. Before picking a sample file from
  the cache, check that it exists (`Test-Path -LiteralPath`).

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

*Done in session 13:* the running app has performed the version 3 cache re-read
(`library.json` is `"Version":3` with `ReleaseDate` on 6,934 tracks, matching the
headless figure), and fanart.tv is confirmed with a real key.

*Done in session 14:* in-place editing of #, Song, Artist and Album (commit
`8526fb6`, pushed).

*Done in session 15:* Last.fm scrobbling (commit `1fbdc9d`), confirmed working
by the user. If scrobbles ever stop, check `%LOCALAPPDATA%\AudioFool\scrobbles.json`,
which the shell *can* read. Entries piling up mean sending fails, and the Last.fm
window shows why. The status-bar indicator is done too. Possible follow-up, not built:
a Love button.

0. **Ask the user how in-place editing feels in the running app.** Only ThemeLab
   has driven it. The real mouse (the slow click's timing against a double-click)
   and real key presses were off limits. Worth asking: does Enter moving down a
   row suit them, and is the slow click too easy or too hard to hit?
0. **Queued tracks keep their old tags.** `AudioEngine` holds the `Track` objects
   the queue was built from, and `OnEngineTrackChanged` sets `NowPlaying` from
   them. Edit a track that is waiting in the queue, by either the grid or a
   dialog, and when it comes up the now-playing bar and window title show the
   old title and artist until it is played again from the grid. The file and the
   grid are right. The fix would be to look the track up by path in
   `_library` in `OnEngineTrackChanged`, or to refresh the engine's queue on
   save. Not done: nobody has hit it, and it touches playback.
1. A visible, editable queue view — now the most conspicuous missing player feature.
2. **Library-wide tag stripping**, if the user wants it. They keep their tags lean and
   use the new dialog fields mainly to *clear* publisher, composer, conductor, genre
   and comment. Clearing album by album is slow over ~2,400 albums; a one-shot "strip
   these tags from every track" would do it at once. They like track and disc counts,
   so those must never be in the strip set. Ask before building: it rewrites every
   file on the drive.
3. **Dates lost to earlier saves cannot be recovered from the files.** Before
   session 12, every tag-dialog save cut a full date to its year. If the user wants
   them back, MusicBrainz release dates are the source (the release-group search in
   `OnlineArtSearch` is most of the lookup already). Offer it; don't build it
   unasked, since it writes to many files.
4. **Show the full date in the album header?** It shows only the year today, while
   the sort uses the date. Not asked for.
5. MilkDrop 3 / projectM visualisation. Scoped out in session 6 (LGPL-2.1, C API,
   `GLWpfControl` for OpenGL-in-WPF, no prebuilt `libprojectM.dll` — source only).
   Proposed next step: spike build of `libprojectM.dll`. No implementation started.
6. Profile the post-scan memory.
7. TAK and DTS via a libVLC fallback decoder, if those files matter.
8. Code signing would remove the SmartScreen warning on first launch, but is rarely worth
   the cost for a personal build.
