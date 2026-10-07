# AudioFool — session handoff

Updated 2026-10-06 after the thirty-sixth build session. Read this alongside
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
299 now, though the clean-clone check has not been repeated since). If you add a dependency that lives outside NuGet, re-run that check — it is
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
  The same image is the title-bar identity at 30 px.
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
- **Theme system.** *(Removed in session 18: PS1 is the only theme.)* Logo menu → Themes submenu → checkable items (radio-button
  behaviour — only one can be active). The active theme is persisted to `settings.json`
  as `Theme` (string, default `"Dark"`). Panels, borders and backdrop switch instantly;
  the accent colour does not (see *The accent* under *The theme*, below).
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
- **Teal accent for the Dark theme** *(Dark removed in session 18)* — `#14B8A6`. Vista keeps Windows blue `#0078D4`.
  See *The accent* under *The theme*, below; there are two WPF-UI traps in there.
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
  and (with a key) fanart.tv. The user's rules: **JPEG only, at least 600 × 600** (lowered from 1,000 on 2026-10-06).
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
  - **Non-square covers pass** if both sides are at least 600. The Cover Art
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

### Changes from session 17

**PS1 was replaced with a grey-console theme**, at the user's request, from a
written brief: the original grey PlayStation hardware, not a dark games UI. The
rule the user set is that the grey is the main surface and the controller colours
are sparse punctuation, each with one meaning. It keeps the name "PS1", so the
saved setting and the Themes menu are unchanged. It was done in rounds: the user
approved a plan, a first pass shipped, and they sent a screenshot of it running,
with critique. Two revisions followed.

**Where it ended up** (commit `eea12aa`, pushed and installed, 2026-09-29):
- Warm greys, about 15% darker than the brief: shell `#A7A4A3`, surface `#B6B3B1`.
- **Play/Pause is the only coloured key**: green while it shows Play, red while it
  shows Pause.
- Controller-shape labels (removed in session 18): a blue ✕ on ARTISTS, a red ○ on ALBUMS, a green △ on
  the album title.
- The playing row and the seek bar are green, and a library scan fills a yellow bar.
- The grid has no focus bar. There is no pink, and the logo keeps its colours.

The design-system section's *PS1* entry below is the current reference,
including the full list of accent uses. The notes that follow are the history.
**Earlier values in them are superseded**: the first pass's lighter greys, a blue
Pause key, and yellow Previous/Next keys.

- **The palette was given, not chosen**: shell `#C5C1C0`, surface `#D6D2D0`,
  recessed `#AAA6A4`, shadow `#777473`, ink `#242424`. Green `#00AC9F` means
  playing, blue `#2E6DB4` the selected artist and album, red `#DF0024` errors and
  Last.fm "reconnect", yellow `#F3C300` only while scanning. **The user ruled out
  pink** (`#D15A9C` in the brief) entirely. The multi-colour logo is kept as an
  agreed exception.
- **Green, yellow and the greys are close in brightness** (green 1.9:1 on the
  surface, yellow 1.3:1). The user accepted drawing those marks with a 1 px ink
  keyline (`AfStatusKeyline`). Accent-coloured text uses darkened variants that
  clear 4.5:1 (`#245A96` links, `#A3001C` errors).
- **PS1 runs on WPF-UI's Light base.** `ThemeService.SetBase` swaps the
  `ThemesDictionary` instance in `Application.Resources` (Dark for the other two
  themes). *(`SetBase` went in session 18: `App.xaml` now starts on Light.)* Any WPF-UI key the overlay misses then falls back to a light value, not
  white text on grey. The overlay restates both levels of WPF-UI keys: the shared
  palette (`ControlFillColorDefaultBrush`...) and the per-control keys
  (`ButtonBackground`, `TextControlBackground`, `ContextMenuBackground`,
  `ToggleSwitchFillOn`...).
- **WPF-UI's accent is a dark-grey key in PS1** (`#4A4746`, light text). Play,
  Save, Connect, check boxes, switches and the Statistics bars carry no controller
  colour.
- **New tokens**, whose defaults are the old values, so Dark and Vista did not
  change:
  - `AfStrokeTrackSelectionBar`: the track grid's own selection marker, clear in
    PS1 so a selected, playing row shows only green.
  - `AfStateQueue`: shuffle and repeat when on. Ink in PS1.
  - `AfSliderSeekFill`: the seek bar's travelled part.
  - `AfStatBarCaution`: the Statistics missing-tag bars.
  - `AfStatusKeyline` and `AfStatusKeylineThickness`.
- **New slot `AfNowPlayingGlyph`.** The track grid's now-playing mark is a
  template now. Dark and Vista keep the blue note; PS1 draws a green ▶ with a
  keyline.
- **The dialogs paint `AfSurfaceShell` on their root grid.** They had no shell of
  their own, so under PS1 the window behind them was transparent. The old dark PS1
  hid this. The token is transparent in Dark and Vista.
- **Removed from PS1**: the scanlines, the dither, the low-poly line art, the
  Consolas numerals (Segoe UI with `Typography.NumeralAlignment="Tabular"` now),
  and the logo's entrance animation. The row hover fade and the selection marker
  animation remain in `Ps1Motion.xaml` (the marker went in session 18).
- **Fixed on the way: the PS1 slider never drew its travelled part.** WPF-UI's
  implicit `RepeatButton` style still applies with `OverridesDefaultStyle="True"`,
  and it collapsed the segment to 0 px wide. The old blue fill had never shown.
  `Style="{x:Null}"` on the two repeat buttons fixes it.
- **Not done: the tag dialog's ✕ buttons stay ink.** The plan was red when armed,
  but "armed" is the view model's private `_cleared` set, which nothing binds to.
  Showing it would be a functional change.
- **Verified**, with no window on the desktop:
  - ThemeLab renders of the main window, the tag editor, Statistics, Last.fm (setup
    and failing), the Last.fm indicator states, and the logo menu (`--menushot`).
    All used a magenta `--bg` so an unpainted gap would show. There were 0 magenta
    pixels.
  - A pixel count of the main window, logo excluded: 99.9% neutral. Saturated
    pixels are 0.11% (green 0.09%, blue 0.02%, no red, yellow or pink).
  - **Dark and Vista are pixel-identical to the previous commit.** Six renders from
    a build of `129a715` in a scratch worktree were compared: 0 differing pixels.
  - Runtime switching Dark ↔ PS1 works. It has the existing limitation that the grid
    headers keep the previous theme's style until restart, and the accent too (see
    below). The pre-change build does the same.
  - 299 tests pass. The installed build starts (smoke test, closed at once).
  - The user then saw it in the running app and sent a screenshot with
    critique, which led to the revisions below.

**Revised after the user's first look** (same session):
- **About 15% darker, at the user's request** ("a little too light"). Shell
  `#A7A4A3`, surface `#B6B3B1`, shadow `#656362`. The recess `#999593` is only 10%
  darker, so ink on it still clears 4.5:1. Every derived grey moved with them.
  Secondary text went to `#353332` and tertiary to `#3B3938` to keep 4.5:1 on the
  shell. The darkened accent text moved too: links `#143860`, errors `#7A0015`,
  caution `#463700`. The contrast table is in the header comment of
  `Ps1Theme.xaml`, measured, not estimated.
- **Coloured transport keys**, as the user specified at that point. Both of these
  choices were later changed, see below:
  - Play/Pause is green while it shows Play and blue while it shows Pause.
  - Previous and Next are yellow.
  - All three have an ink outline. Glyphs are ink, except a light glyph on blue.
  - These are the one place accents are fills.
  - Implemented as `AfPlayKey` / `AfSkipKey` styles. The defaults in
    `Components.xaml` are WPF-UI's button plus the old `Appearance`, so Dark and
    Vista are unchanged (0-pixel diff again).
- **`Appearance` moved from `MainWindow.xaml` into those styles.** WPF-UI colours
  a *Primary* button in its **template's** triggers, and those outrank every style.
  PS1's Play key is therefore `Secondary` plus its own colours.
- **The grid's keyboard-focus bar is gone** (`AfStrokeSelectionBarThickness` 0).
  The user found the black bar on a selected row distracting. Selection is still
  the grey row.
- ThemeLab gained `--paused 1` (the transport key shows Play).
- Accent share of the window is now 0.35%, up from 0.11%.

**Second round of feedback** (same session):
- **The Pause key is red** (`#DF0024`) instead of blue. It has a light glyph at
  4.2:1, where ink would only reach 3.1:1. Blue is no longer used on the keys.
- **Controller-shape labels** (removed in session 18): an outlined blue ✕ before ARTISTS, a red ○ before
  ALBUMS, and a green △ before the album title over the tracks. The track pane has
  no text label of its own. Each shape is an ink stroke with the colour stroked on
  top, which is the keyline. They use three new slots, `AfPaneMarkArtists` /
  `AfPaneMarkAlbums` / `AfPaneMarkTracks`, which are empty in `Components.xaml`, so
  Dark and Vista are unchanged. Each pane label in `MainWindow.xaml` is now a
  horizontal `StackPanel` of the mark plus the text. The album title is a
  `DockPanel`, so its trimming still works.
- **The scan progress bar is yellow** with an ink outline, matching the scanning
  lamp.
- **Previous and Next went back to plain grey keys** (2026-09-29, the user's
  call). PS1 no longer restates `AfSkipKey`, so they use the neutral default from
  `Components.xaml`. Play/Pause is now the only coloured key. The
  `Style="{DynamicResource AfSkipKey}"` hooks in `MainWindow.xaml` stay, so a theme
  can still colour them.
- ThemeLab gained `--scanning 0.4`.
- Verified: renders of playing, paused and scanning. Dark and Vista are still a
  0-pixel diff against `129a715`. Accents are 0.37% of the window. 299 tests pass.
  The installed build starts.

### Changes from session 18

- **The blue selection bar on Artists and Albums is gone** (PS1), at the user's
  request: the dark-grey row highlight is enough. See the design-system *PS1*
  entry. Verified with a ThemeLab render: 0 blue pixels at the selected rows'
  left edges. 299 tests pass. Installed.
- **The controller-shape pane marks are gone too** (the blue ✕, red ○ and green
  △ before ARTISTS, ALBUMS and the album title), at the user's request. PS1 no
  longer fills `AfPaneMarkArtists` / `AfPaneMarkAlbums` / `AfPaneMarkTracks`, so
  they fall back to the empty templates in `Components.xaml` and take no space;
  the labels now line up with the row text, as in Dark. The slots stay in
  `MainWindow.xaml`. Blue is now used only for links. Verified with a ThemeLab
  render; installed.
- **No focus ring on a focused artist or album row** (PS1), at the user's
  request: the ink outline a click leaves on the row. New token
  `AfBrowserRowFocusInset` (default 1, the old `AfFocusRingInset`; PS1 0) drives
  only the two rings in `AfBrowserRowTemplate`, so buttons, Statistics rows and
  the other controls keep their focus rings. The arrow keys move the selection
  in these lists, so the grey row still shows where the keyboard is.
  - ThemeLab gained **`--focusrow ArtistList|AlbumList`**, which focuses the
    selected row. `--focus` focuses the `ListBox`, which draws no row ring, so it
    could not show this state.
  - Verified: in PS1 a focused row renders pixel-identical to an unfocused one,
    for both lists. Dark still draws its ring (1,144 px differ between the two
    focus renders). 299 tests pass. Installed.

**Dark and Vista were removed; PS1 is the only theme** (at the user's request, to
stop spending effort maintaining themes they don't use).
- **Gone**: `Themes/VistaTheme.xaml`, `ViewModels/ThemeItem.cs`, the Themes menu,
  `MainViewModel.ThemeItems` and its handler, `AppSettings.Theme`,
  `ThemeService.Names` / `Backdrop` / `SetBase`, and runtime switching. ThemeLab
  lost `--theme`, `--switch` and `--from`.
- **Changed**: `ThemeService.Apply()` takes no argument and always applies PS1.
  `App.xaml` (and ThemeLab's `LabApp.xaml`) start on WPF-UI's Light base. Every
  window says `WindowBackdropType="None"` in XAML rather than setting it in code.
- **Kept**: `DesignTokens.xaml` and `Components.xaml` as the base layer under PS1.
  See the design-system section for why they weren't folded together.
- **Verified**: ten ThemeLab cases rendered before and after on magenta. All are
  0 pixels different with 0 magenta, except the logo menu, which is 80 px shorter
  with Themes gone. 299 tests pass. The installed build starts under the shell's
  stale settings, which still say `"Theme": "Vista"`, and shows the main window.

**The title-strip seam is gone** (the user's request). A 1 px warm-grey line
(`AfStrokeWarm`, `#8F8B8A`) ran the full width under the title bar and showed in
the gaps between and beside the panes. The `Border` in `MainWindow.xaml` and the
token (in both `DesignTokens.xaml` and `Ps1Theme.xaml`) were deleted outright
rather than made transparent, now that there is only one theme. Verified: the
render differs from the previous one in exactly one row, 1,300 px, and nowhere
else. Installed.

**The output readout moved from the status bar to under the volume slider**
("Shared 96 kHz/32-bit", "Exclusive 192 kHz/24-bit (bit-perfect)"), at the user's
request. It is right-aligned to the slider, in the small type size, with the
device name still in its tooltip.
- **It takes no layout space.** The volume group is now a `Grid` holding the old
  slider `StackPanel` and a zero-height `Canvas` at its bottom, with the readout
  at `Canvas.Right="0"`, `Canvas.Top="6"`. A Canvas neither measures nor clips
  its children. So the slider stays level with the seek bar, and the long
  exclusive readout runs left under the duration rather than widening the column
  and resizing the seek bar whenever the mode changes.
- **It sits outside the dimmed volume group**, so it stays at full strength in
  exclusive mode, which is when it matters.
- ThemeLab's sample readout now uses the real format, and **`--output "<text>"`**
  overrides it.
- **Verified**: shared and exclusive renders both fit inside the deck with no
  overlap. A pixel diff against the previous render changed only the two
  readout regions: the new one under the slider and the old one in the status
  bar. 299 tests pass. Installed.

### Changes from session 19

- **A queued track now shows its new tags when it comes up.** Before this, a track
  edited (in the grid or a dialog) while it waited in the queue came up with its old
  title and artist in the now-playing bar and window title. It was also scrobbled
  under them, and the grid's now-playing note didn't show on its row. The cause:
  `AudioEngine` plays the `Track` objects its queue was built from, and a save
  replaces them in the library with new ones.
  - **The fix is in the view model, not the engine.** `OnEngineTrackChanged` looks
    the engine's track up by path (`MainViewModel.LibraryCopyOf`) and uses the
    library's copy for `NowPlaying`, the scrobbler's `TrackStarted` and the art. It
    falls back to the engine's copy if a rescan dropped the file. The engine's queue
    is untouched, so `engine.CurrentTrack` can still be stale. Anything new that
    reads it for tags should go through `LibraryCopyOf` too. The scrobbler's
    `Advance` compares by path, so it is unaffected. `ApplyInlineEditAsync` uses the
    same helper for the lookup it already did.
  - **Verified** with the new ThemeLab **`--window queue`**, which runs the real
    engine silently (shared mode, volume 0, silent fixtures) over the four scratch
    tracks of `--window edit`. It starts One, pauses, edits Three's title and Four's
    artist, and resumes. It then checks the now-playing title and artist, the window
    title, the scrobbler's track and the note for three hand-overs: a gapless one
    into the unedited Two (the control), Next into Three, and gapless into Four.
    Before the fix, Three and Four were stale on every check, and Two passed. After
    it, all three pass, with `engine.CurrentTrack` still showing the old title.
    `library.json` hash unchanged, and the scratch folder deleted. 299 tests pass.
    Installed; the installed build starts.
- **A retag of the playing track reaches the scrobbler** (later in session 19).
  `ReplaceTracksInLibrary` swapped `NowPlaying` but not the tracker's `Track`, so
  that play would have scrobbled under the old tags. It now calls
  `LastFmScrobbler.TrackRetagged`, which calls `PlayTracker.Retag`. That swaps the
  track when the path matches and leaves the play itself alone: time heard, start
  time and whether it has scrobbled. "Now playing" is re-sent only if the
  `ScrobbleEntry` it would show changed, so a comment or cover save sends nothing.
  4 new tests in `ScrobblingTests` (303 at that point). It only became reachable
  with the next change: until then, the playing track could not be saved at all.
- **The playing track and the next one can be saved now.** Until this change, any
  save to a file the engine had open failed with "the file may be open in another
  program". That was the playing track (paused included) and the next one, opened
  ahead for the gapless handover (`_prefetchedStream`). It failed from the grid
  and the dialogs, for FLAC and MP3 alike.
  - **The cause, measured** against a paused stream: opening for writing with
    `FileShare.None` is refused, and with `FileShare.ReadWrite` it is allowed.
    BASS (`Bass.CreateStream(path, ...)`) shares its handle. TagLib's default
    `LocalFileAbstraction` opens for writing unshared, and that was what failed.
  - **The fix**: `TagWriter` opens files through its own `SharedFile`
    `IFileAbstraction`, which shares reads and writes. Every save goes through
    it. Reads (`TagReader`) are unchanged.
  - **Measured before building** (scratch probe, no sound device, on copies of a
    real FLAC, MP3 and DSF). Each copy was decoded while being saved, then
    compared sample by sample with an untouched copy:
    - A small edit (title) is written in place. The size doesn't change, and the
      open stream's output is **bit-identical**, for all three formats.
    - A save that grows the tag (a 300 KB comment) moved the MP3's audio by
      599 KB, and grew the FLAC by 286 KB. The open stream then read the wrong
      bytes: nearly every sample in the next 6 s differed, up to full scale. It
      would audibly jump back and lose its end.
    - In every case, a fresh stream on the written file matched the original
      exactly. Only a stream already decoding the file is hurt.
  - **So a resizing save is refused for a file playback holds.** This was the
    user's choice, over reopening the stream or accepting the jump. The engine's
    new `HoldsFile(path)` answers under its lock. The view model passes it to
    `WriteTrackTags` / `WriteAlbumTrackTags` / `WriteSelectedTrackTags` as
    `holdsFile`. For a held file, `SaveTags` makes the save on a scratch copy in
    `%TEMP%` (`AudioFool-trial-*`, same extension, deleted afterwards). If the
    copy changed size, it refuses with "it is playing or up next, and this change
    would rewrite the whole file. Save it again when it isn't loaded" (**superseded in
    session 37**: such a save is now made while the engine lets go of the file). Otherwise
    it makes the real save.
    - The usual trigger is **Edit Album Tags with a new cover while the album
      plays**. The other tracks and the folder `cover.jpg` are written; the
      playing and next tracks are named as failed, and re-saving the album later
      fills them in.
    - A held DSF costs a full copy (about 330 MB) per save.
    - Not handled: a stream opened *during* a resizing save, if the track changes
      at that moment. The window is the length of one save.
  - **Verified**:
    - 8 tests in `TagWriteSharingTests` (**311** in total). They cover: a save
      against a BASS-style open handle; a held file taking a same-size save; a
      held file refusing a resizing one and staying byte-identical, with no trial
      file left; and the same resizing save succeeding when nothing holds the
      file. The MP3 fixture has no tag, so any title grows it. The same-size test
      seeds it with one save first, which gives it the padding real files have.
    - ThemeLab `--window queue` against the real engine. `HoldsFile` is true for
      the playing and prefetched tracks and false beyond them. The prefetched
      track saves, then plays through the gapless handover with its new title.
      The playing track saves, and the now-playing bar and scrobbler follow. A
      300,000-character title on the playing track is refused and leaves the file
      byte-identical. Playback then resumes.
    - Installed; the installed build starts.
  - Session 14's note that the now-playing note "had been disappearing after any
    tag save of the playing track" can't have been observed: that save always
    failed before this change.
- **Darker banding in the track grid** (PS1), at the user's request. The
  alternate row (`AfSurfaceRowAlternate`) went from `#B2AEAD` to `#AEABA9`: 8
  levels under the panel `#B6B3B1` instead of 4. That is where the shared hover
  grey sat, so hover on a banded row would have vanished. The grid now has its own
  hover, **`AfSurfaceTrackHover`** `#A8A5A3`, between the band and selection
  (`#A19D9C`, unchanged). This mirrors `AfSurfaceTrackSelected`. The browser
  lists keep `AfSurfaceRowHover`, unchanged, since they have no banding.
  Verified with a ThemeLab render: the banded rows sample at `#AEABA9`, and a
  pixel diff against the previous render changes only those rows. The hover
  state can't be rendered without a mouse, so it is unseen. Installed.
- **The search box is lighter than the other text boxes** (PS1), at the user's
  request. They thought it had changed colour; it hadn't since `eea12aa`, and it
  was the shared recess `#999593`. They chose to take it back to the first grey
  pass's recess, **`#AAA6A4`**, for the search box alone. Hover `#AEAAA8` and
  focus `#B1ADAB` keep the old +4 / +7 steps. It has a style of its own,
  `AfSearchBox`. That is a plain `BasedOn` WPF-UI's `TextBox` in
  `Components.xaml`; the PS1 version restates the three `TextControlBackground*`
  keys in its `Style.Resources`. So the dialogs' text boxes, the slider tracks
  and the art mats keep `#999593`.
  - At this shade the box is only 3 levels off the title strip (`#A7A4A3`). It
    reads as outlined and flat rather than recessed. The user was shown this.
  - Verified with ThemeLab: rest `#AAA6A4`, focused (`--focus SearchBox`)
    `#B1ADAB`. The main window's diff is confined to the box (x 76–479, y 7–40),
    and the tag dialog is 0 px different. Installed.

### Changes from session 20

The user reported that in-place editing in the track grid didn't work well: the slow
click didn't open an edit, and Enter didn't carry on to the same field on the next row.

- **The slow click never worked with a real mouse.** When the second click lands on the
  cell that already has focus, the grid tries to open an edit of its own on mouse-down.
  `TrackGrid_BeginningEdit` refuses that attempt, which is right, but it also stopped the
  slow-click timer that the same click had just started. Session 14 missed this because
  it called `ArmSlowClick` directly. Now the timer is stopped only when an edit really
  opens.
- **Enter now opens the same field on the next row**, so a column can be typed straight
  down (`SaveAndEditNextRow`). Esc stops. On the last row, Enter just saves. The "next"
  row is chosen before the save, so a renumbered row that re-sorts doesn't change it.
- **A save no longer loses the grid's place.** Every save rebuilds the track list
  (`OnSelectedAlbumChanged` clears `Tracks`), which emptied the grid's selection and its
  current column. `OnTracksChanging` now records the selection (by path, because the
  save replaces the `Track` objects), the current cell and whether the grid had focus.
  `RestoreGridPosition` puts them back at `Loaded` priority. Another album's rows match
  nothing, so choosing a different album behaves as before.
- **The next row's edit opens after the rebuild**, since opening it earlier would be
  cancelled by the rebuild. `PendingEdit` is the target:
  - `RestoreGridPosition` opens it once the rows are back.
  - A save that writes nothing, or fails, never rebuilds, so the save's completion opens
    it instead.
  - Each Enter gets its own `PendingEdit`, so an earlier save that finishes late can't
    open a later target.
  - `OpenPendingEdit` selects the row again rather than trusting the current selection.
    After a commit, WPF sometimes moves focus, and with it the selection, back to the row
    just edited, and that can happen before the rebuild records the selection. It
    focuses the edit box at `ContextIdle`, after the grid has settled its own focus.
- **A rebuild in the middle of an edit keeps the edit.** Before, the edit was cancelled
  and the half-typed text was lost. Now its text and cursor position are kept, and it
  reopens once the rows are back. This covers a scan, or an earlier save, landing while
  you type.
- **Verified** with ThemeLab `--window edit`, which gained three checks:
  - **Real click**: routes mouse down and up through the grid's own handlers onto the
    focused cell. It failed on the old code and passes now.
  - **Enter walk**: after the save's rebuild, # is open and focused on the next row. An
    unchanged Enter also moves on, Esc stops, and the last row just saves.
  - **Rebuild mid-edit**: the edit comes back with "Half typed" still in it.
  - All three passed in 8 of 8 runs. Before the last two fixes, the Enter walk failed
    about one run in four, which is how the focus race was found.
  - `library.json` hash unchanged, 311 tests pass, `--window queue` still passes, and
    the installed build starts.
  - **Not yet tried by the user** with a real mouse and keyboard.
- **Known gap**: while the save before an Enter is still running (the tag write plus
  the rebuild, and behind the previous save's `library.json` write), no edit is open, so
  anything typed in that moment is lost.

### Changes from session 21

**Clicking the ARTISTS header toggles the artist order** between A–Z and most recently
added first, at the user's request. The header reads "ARTISTS · RECENT" in that mode.
**The app always opens A–Z** (the user's request). The toggle lasts for the session only
(`MainViewModel._artistsByRecent`). It was first saved as `AppSettings.ArtistsByRecent`,
which has been removed; a leftover key in `settings.json` is ignored on load and dropped
on the next save.

- **"Added" is the file's creation time, not its modified time.** The user asked for
  "most recently updated", and that was built first, from `ModifiedUtc`. The real
  library showed why it doesn't work: about 10,400 files were rewritten on 24–25
  September, most likely a bulk retag, so the list was a big tie, and any tag edit would
  jump an artist to the top. Creation time is when the file arrived on the drive. A
  copy sets it, and tag saves leave it alone: TagLib writes in place, and a test checks
  this. In a 400-file sample, 85% were created in July (the initial copy), with a
  steady trickle since. The user chose "recently added".
- **`Track.AddedUtc`** is cached, and omitted from JSON while null. `ArtistGroup.LastAddedUtc`
  is the newest across the artist's tracks. `SortRules.SortArtistsByRecent` orders
  newest first, and ties fall back to A–Z.
- **No cache version bump and no re-read.** The creation time comes free with the stat
  the scan already makes (`FileStamp.CreatedUtc`, an init-only member, not part of the
  stale check). A reused track that lacks it gets it there. `ScanSummary.Backfilled`
  counts these and makes `AnyChanges` true, so the cache is saved and the view rebuilt.
  Measured read-only on the real library: 0.5 s, all 26,795 tracks. Top of the list:
  Connor Kaminski, Keyan, Loam, Ro1 (all 28 September). 75 artists have additions since
  August; the 424 from the July copy tie and fall back to A–Z. The installed build's
  smoke-test launch did this for real, and `library.json` now carries the dates.
- **A toggle starts again at the top** of the new order and selects its first artist
  (`ApplyToView(keepSelection: false)`), as startup does. It first kept the selection.
  The user found that dragged them down to wherever 150cc, the A–Z first artist the
  app opens on, landed in the recent order. Verified with `--clickartists 1
  --clickfrom 250` in both directions: the first artist is selected and the scroll
  offset is 0.
- **Caveat:** copying the library to a new drive resets every creation time, which
  would make the whole library one tie again.
- **The header is a `Button` with `Style="{x:Null}"` and a bare template**, so the A–Z
  state renders pixel-identical to before (0 px diff on magenta). Hover and keyboard
  focus underline the label. A focus-ring border was tried first, but it added 2 px of
  height and pushed the list down. There are two fixed labels, not one bound label,
  because `LetterSpacing` applies once and doesn't follow text changes.
  `BoolToVisibilityConverter` gained `ConverterParameter=Inverse`.
- **Verified**: 7 new tests (318 in total). ThemeLab renders of both states. ThemeLab
  `--window click --clickartists 1` clicks the header through its automation peer on the
  real library: the selection is kept, the tooltip flips, and `settings.json` is
  restored byte for byte. ThemeLab loads the cache without scanning, so every date
  there reads as unknown. The real order was checked by a headless probe instead. New
  ThemeLab switch: `--artistsort recent`.

**Also: the Enter walk from session 20 still has a timing gap.** Re-running `--window
edit` found the next row's edit box sometimes opens *without keyboard focus*, about 1 run
in 10 to 15. Session 20's "8 of 8" was luck. `OpenPendingEdit` now looks the cell up
again when it focuses the box, and retries once at `ApplicationIdle`; straight after a
rebuild, the row may not be realised yet. That brought it to 1 failure in 16, and not
to zero. When it fails, focus sits outside any cell, and adding tracing hides the
failure. The harness now logs the focused element's visual chain at that point, to
find it next time. **Ask the user** whether, in the running app, the next row's box
ever opens without taking their typing.

**The window opens where it was last closed** (later in session 21, the user's request).
- **Saved on `Closing`** as `AppSettings.Window`, a `WindowBounds`: the normal
  rectangle in physical pixels, plus whether the window was maximised. **Applied in
  `SourceInitialized`**, before the window is first shown, so it never appears anywhere
  else first. With nothing saved, or a spot that's no longer reachable, it keeps
  `FitToWorkArea`'s centred size, as before.
- **Win32, not WPF's Left/Top/Width/Height** (`Services/WindowPlacement.cs`). The app
  is PerMonitorV2, where a DIP is a different number of pixels on each monitor, and WPF
  doesn't see Aero Snap. Capture uses `GetWindowRect` for a normal window, so a snapped
  window keeps its snapped spot. For a maximised or minimised window it uses
  `GetWindowPlacement`'s normal rectangle, converted from workspace to screen
  coordinates (they differ when the taskbar is on the top or left). Minimised-while-
  maximised comes back maximised. Apply uses `SetWindowPos`, twice if needed, because
  arriving on a monitor with a different DPI rescales the window once. It then sets
  `WindowState = Maximized` if saved that way.
- **Reachability** (`WindowBounds.IsReachableOn`, in Core, 9 tests): at least 120 px of
  a 32 px title band must land on one monitor's current work area. This rejects an
  unplugged monitor, a smaller remote-desktop screen, and a title strip off the top or
  behind the taskbar.
- **ThemeLab closes the real MainWindow**, which now saves settings. It restores
  `settings.json` byte for byte around that `Close()`, so a render can't store its
  -20000 position.
- **Verified**: ThemeLab **`--window placement`** runs apply-then-capture on a window
  that gets a handle and is never shown (`IsWindowVisible` false throughout). It passes
  for the primary, a window hanging off an edge, maximised, and one spot on each of the
  three monitors: (0,0)–(3440,1392) primary, (3440,11)–(6880,1403) and
  (-1920,10)–(0,1042). A spot 50,000 px off is refused. **All three monitors are
  96 dpi, so the DPI-rescale retry has not been exercised.** 327 tests pass. Not
  launched from the shell: that instance would save into the stale container
  settings.

### Changes from session 22

The user pointed AudioFool at a copy of the library on a micro SD card (`E:\Music`,
exFAT, label "M", 26,795 tracks; the SSD `D:` was not plugged in). The app said "No
audio files found". The files were fine: the scanner's own walk found all 26,795 in
0.4 s. The folder list was the problem. Libraries showed `C:\Users\MarcusRenlund\Music`,
`E:\Music` and **`E:\Music` again**, all unticked.

- **How the duplicate happened.** `E:\Music` had been added by hand while the list
  still had `D:\Music`. The next start found `D:\Music` missing, confirmed the same
  files on `E:` and re-pointed that entry to `E:\Music`. Nothing checked whether it
  was already listed. Each entry has its own tick, and a track is hidden if *any*
  entry covering it is unticked. The menu closes after every click, so ticking "both"
  easily ticks one twice. `C:\Users\...\Music` is `AppSettings.WithDefaults`, which
  adds the Windows Music folder when the list is empty, so the list was empty at
  some point.
- **Fixes**, in the new `AudioFool.Core/Library/MusicFolderList.cs` (`Normalize`,
  `SameFolder`, `Distinct`, `WithoutFolder`) and `MainViewModel`:
  - Duplicates (ignoring case and a trailing separator) are merged at startup. The
    merged entry is ticked if any copy was, and the settings are re-saved.
  - A relocation onto a folder that is already listed drops the old entry, which
    keeps the listed one's tick, and de-duplicates the rebased cache by path.
  - Add folder uses the same comparison, and stores the normalised path.
  - **"All N tracks are in unticked folders. Tick a folder under Libraries…"**
    when tracks exist but every one is hidden. "No audio files found" is now shown
    only when there really are none.
  - **"Add folder…" is no longer a checkable item.** A `MenuItem` that is its own
    container is given the parent's `ItemContainerStyle`, which made it checkable and
    bound its tick to nothing. It and the new item set
    `Style="{DynamicResource {x:Type MenuItem}}"` locally, which WPF leaves alone.
- **Remove folder** (at the user's request): Libraries → Remove folder ▸ lists every
  folder. Clicking one stops watching it and takes its tracks out of the library and
  `library.json`. Tracks that another folder still covers (a parent or child) stay.
  Nothing on disk is touched; Add folder brings it back at the cost of a tag read.
  It refuses while a scan runs, since the scan would put the tracks back. There is
  no confirmation. `RemoveFolderCommand` takes the `FolderFilterItem`, bound through
  the `Proxy` from a second `CollectionViewSource` (`RemoveFoldersSource`).
- **Verified**:
  - 9 tests in `MusicFolderListTests` (**336** in total).
  - ThemeLab **`--window folders`** (new) against the real card: `[E:\Music,
    e:\music\, E:\Music]` merges to one; the user's case (`D:\Music` plus a
    hand-added `E:\Music`, only the card in) starts with one ticked `E:\Music` and
    26,795 tracks; the unticked message; Remove leaves 0 tracks and an empty cache.
    Settings and `library.json` restored byte for byte.
  - ThemeLab **`--libshot <png>`** (new) renders the Libraries submenu and prints each
    item's check state. It confirms Add folder is no longer checkable and that each
    Remove row carries `RemoveFolderCommand` with its folder.
  - Installed. **Confirmed by the user** in the running app: one ticked `E:\Music`,
    the `C:\Users\...\Music` entry removed with Remove folder, library showing.
- **Known, not fixed: the card's files all look changed.** exFAT stores write
  times to the second (the card's read `…:52.0000000Z` where NTFS on the SSD has
  `…:50.5608732Z`), so `Track.MatchesFile` fails for nearly every file, and the first
  start off the card re-reads every tag: **6 minutes** from the SD card, with the
  cached library on screen throughout. Later starts off the card are quick, because
  the cache then holds the card's times. Switching between the SSD and the card
  pays it each time. A ≤ 2 s tolerance when the size matches would avoid it. **The
  user declined it for now** (2026-09-30); don't build it unasked.
- **The card is a near-exact copy.** Checked against the cache as saved on 29
  September, 15:04 (the SSD itself wasn't plugged in): all 26,795 tracks are on the
  card with matching sizes, and it has no audio the cache lacks. **One file is
  older**: `150cc\[2016] Show Recordings\Show 1.flac`, modified on the SSD 28
  September 13:02, on the card 24 September 08:48, the same size. Every cached tag
  matches, and the card's copy has no genre, comment, publisher or composer. So the
  edit was to something the cache doesn't record: a detail field, embedded art, or a
  number's spelling. Comparing the two files needs the SSD. The user was told to copy
  it over.

### Changes from session 23

- **A broad PS1 "hardware" restyle was built and reverted** at the user's request
  (lighter raised headers and deck, darker recessed lists, bevels everywhere, darker
  text). Never committed. The user now points out specific fixes one at a time; make
  the targeted change asked for rather than proposing another sweeping restyle.
- **Album header art is 135 px** (`AfSizeArtHeader`, was 104). The user tried 150 in the
  running app, after ThemeLab mockups at 104, 156 and 208, then settled on 135. The header art already decodes at 320 px
  (`AlbumHeaderArtWidth`), so it stays sharp up to about 200% scaling. The header grows by 31 px, about
  one fewer track row at 900 px tall.
- **The album facts (title, artist, year, duration) sit at the top** beside the art,
  not centred on it: `VerticalAlignment="Top"` on the header's text `StackPanel` in
  `MainWindow.xaml`.
- **The search box has no placeholder text** (`PlaceholderText=""`, was "Search"), the
  user's own edit. The magnifier icon still marks it, and its automation name is still
  "Search".
- Verified with a ThemeLab render: the frame measures 135 px and the title starts
  level with the art's top. 336 tests pass. Installed.

### Changes from session 24

- **An album row shows its full title as a tooltip** after 1 s of hover, only when
  the title is trimmed. The tooltip sits on the row's `Grid` in the Albums
  `DataTemplate` (`ToolTipService.InitialShowDelay="1000"`), and
  `AlbumRow_ToolTipOpening` cancels it when the title fits. It measures the title
  with `FormattedText` against the `TextBlock`'s `ActualWidth` at hover time, so a
  resized pane needs nothing. The `Grid`'s `Tag` carries the title `TextBlock`.
- **Verified** with the new ThemeLab **`--albumtips 1`**, which raises
  `ToolTipOpening` on each realised album row and prints shown or hidden. Run it
  with `--window click --click FLAC --search "Metal Gear"` to get long titles: the
  two that fit are hidden and the seven trimmed ones shown, matching the user's
  screenshot. 336 tests pass. Installed; **confirmed by the user** in the running app.
- **Bit-Perfect greyed out on another computer** (question only, nothing changed).
  The v0.1.0 release zip carries all 13 BASS DLLs. The button is disabled when
  `BassRuntime.ProbeOutputDevice` finds no exclusive-mode rate on the default device
  at startup: Windows' "Allow applications to take exclusive control" unticked,
  Bluetooth/virtual devices, or the device changed after launch (it probes once).
  ~~Offered: re-probing when the default device changes~~ Done in session 37, with
  output following the device. (A tooltip saying why it is disabled was turned down
  in session 37, along with the chip's tooltip.)

### Changes from session 37 (2026-10-06): saving to the playing track, 600 px covers, darker now-playing row

- **Cover search shows covers from 600 × 600** (was 1,000). `OnlineArtSearch.MinimumSize`;
  the status line reads the constant instead of repeating it.
- **Now-playing row `#B4C6BF`** (was `#C3CDC9`), the user's pick. Text contrast about 4.5:1.
- **The Bit-Perfect chip has no tooltip** (the user's call: not necessary). Its accessible
  name is unchanged. This also settles session 24's offer of a tooltip saying why it is
  disabled: don't add one.
- **Output follows the Windows default device** (the user's call: playback follows,
  and Bit-Perfect is remembered). Before, the device was probed once at startup and the
  shared chain built once, so a device switched to mid-session was ignored until restart
  (not tested before the change; that is what the code did).
  - `BassRuntime` registers `BassWasapi.SetNotify`; on `DefaultOutput` (2) it waits
    500 ms for the other roles' notices, then `ReprobeDefaultDevice()`: the device is
    compared by WASAPI ID, and the same device is **not** probed again (one held in
    exclusive mode refuses format checks, which would grey the button). A new device
    gets the full probe (rates, mix rate, DSD conversion rate, which now falls back to
    88.2 kHz when it has no exclusive rates) and raises `DefaultOutputChanged` (worker
    thread).
  - `MainViewModel.FollowDefaultDeviceAsync` (on the UI thread) calls
    `AudioEngine.SwitchDevice(mode)` off it. **`IsExclusiveOutput` is now the setting
    *and* `SupportsExclusive`**: the setting is the user's choice for the session (still
    reset to Shared each launch), so it survives a device that can't do it.
  - `SwitchDevice` reuses the save release: `Release(_ => true, closeDevice: true)` frees
    the current and next streams and the device connection, then `Reacquire` reopens on
    the new default at the same position. Stopped: only the connection is closed.
  - **Verified**: ThemeLab **`--window device --long "a.flac;b.flac"`** calls
    `SwitchDevice` directly on copies of real tracks (shared, silent): playing 27 ms,
    same position, plays on; the next track opened ahead again; paused stays paused at
    the same position and resumes; stopped closes the connection and the next play
    works; the view model's handler reports the device. `ReprobeDefaultDevice()` on an
    unchanged device returns false and leaves the rates alone. **Not verified: a real
    switch of the default device** (it is a Windows setting, so it is the user's to make),
    exclusive mode, Bluetooth.
- **The flaky test, found: two, one a real bug.** 60 sequential runs: 0 failures. 64 runs
  8 at a time (load, as when the quality check runs): 3 failures.
  - **`QualityScanner` could throw at the end of a check**: the loop checked
    `threads.Any(IsAlive)`, then `threads.First(IsAlive)`; the last worker finishing
    in between made `First` throw, losing the final save and the summary. Now
    `FirstOrDefault(...)?.Join`. Caught as `QualityCheckTests.The_scanner_checks_each_track_once...`.
  - **`TagWriteSharingTests` checked all of %TEMP% for trial copies**, so another run's
    (the app's, ThemeLab's, a parallel test run's) failed it. Trial copies are now
    named `TagWriter.TrialPrefix` + GUID + the file's own name, and the test looks only
    for its own file's.
  - After both: 64 parallel runs, 0 failures. **515 tests.**
- **ThemeLab's intermittent Tab walk no longer reproduces**: 24 of 24 complete cycles
  (8 idle, 16 under the parallel test load), against about 1 in 4 turning back after
  Mute in session 5. Not bisected; some later change (Repeat/Shuffle moved after Next,
  `ClickFocus`, the slider work) presumably fixed it. Treat it as closed; if it comes
  back, note the run.
- **The D: drive was unplugged mid-session** (the default output also moved from the
  Topping DAC to "Realtek XU", so probably a dock). The `--long` checks were rerun on a
  60 s generated WAV in the session scratchpad: released for 5 ms, back at the same
  position; `--window device` all OK (switch 273 ms on the Realtek device).
- **A save that resizes the playing or next track goes through now** (the user's call,
  reversing session 19's refusal). The usual trigger is a new cover in Edit Album Tags
  while the album plays: before, those two tracks were named as failed.
  - `TagWriter` takes an **`IFileHolder`** (`HoldsFile`, `WhileReleased`) instead of
    `Func<string, bool> holdsFile`; `AudioEngine` implements it. The trial save on a
    scratch copy stays: a same-size save is still made with the stream reading (no gap),
    and only a resizing one calls `WhileReleased(path, save)`.
  - **Playing track**: the device is flushed (as `PlayCore` does), the stream freed, the
    save made, a new stream opened on the rewritten file, set to the old position, and
    started with a 5 ms fade-in (`OutputChain.FadeInNextBlock`; not for DoP). **Paused**:
    the chain is closed (it can't be flushed) and reopened, still paused. `Position`
    returns the held position during the save so the seekbar stays still. No
    `TrackChanged` is raised, so the scrobbler, waveform and now-playing bar carry on.
  - **Next track**: its prefetched stream is dropped and opened again after the save.
    If the playing track ends during the save, the end sync sets `_advanceAfterRelease`
    instead of stopping, and the release moves on to the next track when the save is done.
  - Anything that starts playing during the save wins (`PlayCore` clears the release
    state; `Reacquire` checks the queue reference and index). If the rewritten file won't
    open, playback stops.
  - ~~Still not handled: a stream opened *during* a resizing save~~ Fixed later in
    session 37: `IFileHolder` is now one call, **`Saving(path, needsRelease, save)`**, and
    `TagWriter` makes *every* save through it, held or not. The engine marks the path
    as being written (`_writing`), and `OpenStream` (used by `PlayCore`, `PrefetchAfter`
    and `Reacquire` instead of `CreateDecodeStream`) waits on `_gate` until it's done.
    The other way round, a save waits for an open already under way (`_opening`)
    before asking `HoldsFile`, so that answer is true. The release path ends the
    writing mark before `Reacquire`, which opens the same file. All saves are
    serialised by `_releaseGate`. `WhileReleased(path, write)` stays as
    `Saving(path, () => true, write)` for ThemeLab. Verified with ThemeLab
    `--window queue`: a save stretched to 600 ms on a worker, and Next from the UI
    thread onto that track (once not held, once the opened-ahead track while let go):
    the jump returns at 603–605 ms, after the save (600–601 ms), and plays the track.
    A UI-thread Next can therefore block for as long as a save takes (34–66 ms
    measured above); only if it lands on the track being saved.
  - **Verified**: `TagWriteSharingTests` reworked (a resizing save to a held file is made
    inside the release; a same-size one isn't released; a failed trial isn't released).
    **514 tests**. ThemeLab `--window queue` against the real engine: paused resize keeps
    position (60 → 60 ms) and state; the next track grows and is reopened ahead; a track
    ending during the next one's save hands over to it. New `--long "a;b"` (copies real
    tracks into the scratch folder, saves a big comment while playing at 0:30;
    `--longcomment N` sets its length): FLAC released for 34–39 ms, MP3 66 ms, DSF 37 ms
    (with a 3 MB comment; 300 KB fitted the DSF in place), each back at the same position
    and playing on. Shared mode only: exclusive mode can't be silenced, so it wasn't run.
    Not verified: listening to the gap, exclusive mode, DoP.

### Changes from session 36 (2026-10-06): a waveform in the seekbar

The user asked to see the track's quiet and loud parts behind the seekbar. One commit:
**`676ec97`**, pushed to `origin/main`. **512 tests pass** (503 + 9). Installed. The user
has used it in the real app and says it moves smoothly on their 144 Hz monitor.

**What it looks like now** (each point is the user's call, in the order they were made):

1. Four mockups (mirrored outline, thin bars, waveform replacing the track, rising above
   the track); the user picked **the waveform replaces the track**.
2. **28 px tall** (`playbackBar.waveformHeight`). It was 22 px, the slider's click strip;
   the user asked for 30% bigger. 29 would put the 14 px handle on a half pixel. `SeekBar`'s
   own Height is set to the token, so its click strip is 28 px; the volume slider's stays
   22, and both stay centred on the same line (measured).
3. **No handle** on the waveform: teal meeting grey is the only marker. The `Handle`
   thumb is at Opacity 0, not removed, so dragging it still works.
4. A **2 px teal playhead line** was added for quiet passages, then **removed** at the
   user's request. Its property and token are gone. Accepted trade-off: in a near-silent
   stretch the split sits only on the 2 px floor (`playbackBar.waveformFloor`).
5. The split **glides** instead of stepping 4 times a second.

The plain groove with its handle still shows until a track's levels are read, and when
they can't be (drive out, undecodable file). The volume slider never shows a waveform.
No new colours: `slider.seekFill` played, `slider.track` the rest. Spec 6.7 Zone 3
describes all of this. `design/progress.md` records where it departs from the spec: the
volume slider no longer matches the seekbar's style, and there's no 14 px handle.

**How it works**

- **`Waveform` / `WaveformLevels`** (`AudioFool.Core/Analysis/Waveform.cs`). A
  decode-only float stream, like `TrackAnalyzer` and never the engine's, so it runs beside
  playback. 600 RMS columns, linear, scaled so the loudest is 1. RMS rather than peak,
  because a loud master's peaks draw a solid brick. Samples go into small blocks (4 per
  column when the length is known, 2,048 frames when it isn't), and the blocks are shared
  out among the columns at the end, so a VBR MP3's estimated length still fills evenly.
  DSD is read at 44.1 kHz, falling back to `DsdAnalysisRate` if that's refused. Returns
  null for a missing or undecodable file, with no message.
- **`MainViewModel.NowPlayingWaveform`**. `OnNowPlayingChanged` cancels any read in
  progress and starts a new one on the thread pool, and drops a result for a track that's
  no longer playing. A tag-save rename keeps the same path, so the file isn't read again.
  It also runs for the restored track at launch, so the stopped bar shows the waveform.
  `Dispose` cancels the read and waits up to 1 s for the decode (`_waveformDecode`, not
  the async method, whose continuation needs the UI thread) before BASS shuts down.
- **`theme.slider`** (Chrome.xaml) has a `theme:WaveformView` behind its `Track`. Only
  `SeekBar` sets `theme:SeekWaveform.Levels` and `.IsPlaying`. A trigger on
  `SeekWaveform.IsShown` hides the groove, makes the fill and the handle see-through
  (they still take clicks and drags), and shows the waveform.
- **`WaveformView`** (Theming/WaveformView.cs) builds one frozen `StreamGeometry` per
  size and set of levels, then draws it twice, clipped at the split.
  - The glide: while `IsPlaying`, the split runs on from the last real value at one
    second per second, capped at `MaxLeadSeconds` (0.4). A late tick therefore stalls it
    briefly instead of letting it run on, and each new value restarts the clock.
  - It redraws on every `CompositionTarget.Rendering` frame, only while playing, visible
    and showing a waveform. A split that hasn't moved at all is skipped.
  - `Levels` is `IReadOnlyList<float>`, because an array-typed property inside a
    template is a XAML compile error (MC4102).

**Verified**

- Timing, from a headless probe on files not read recently: FLAC 112–404 ms, DSD64
  ~1.2–1.3 s, a 35-minute MP3 2 s. ThemeLab: FLAC 428 ms, DSD64 513 ms.
- ThemeLab **`--window waveform --file <audio> [--at 0.4] [--noglide 1]`** (new). It
  plays at volume 0 and prints when the levels arrive and that the volume slider stays
  plain. It samples the drawn split for 2 s, then renders the window and a 3× crop of the
  seekbar (`*.seekbar.png`).
  - Glide on *On The Run* at 507 px: 8 moves of up to 0.62 px before, 64–69 of at most
    0.18 px after, none backwards.
  - Renders looked at: Ihlo *Union* at 2:27 and 4:50 (a quiet stretch), *Speak To Me*
    (DSD) and the live MP3.
- `--window click --lastplayed` now prints the waveform's column count (600 for the
  restored, stopped track).
- **Not measured: the real frame rate on the user's monitor.** The user says it's smooth.
  See the trap below.

**Traps found this session**

- **Probe windows from the sandboxed shell render at ~32 fps**, whether off-screen or a
  1 px window on the 144 Hz primary. `Timeline.DesiredFrameRate` (60 or 144) and
  `timeBeginPeriod(1)` made no difference. A frame rate measured that way says nothing
  about the real app. Monitors: `DISPLAY3` 3440×1440 at 144 Hz (primary), `DISPLAY2`
  3440×1440 at 100 Hz on its right, `DISPLAY4` 1920×1080 at 60 Hz on its left.
- **A `VisualBrush` crop of the seekbar is shifted about 7 px.** It aligns to the
  visual's content bounds, and the `Track` overhangs by half a handle. Crop the
  full-window render instead, as `--window waveform`'s numbers do; the 3× `.seekbar.png`
  shows the shift.
- **ThemeLab's `--window waveform` and `seek` start real playback**, which can save
  settings. The writes re-read `settings.json` and change one value, and this session's
  file was byte-identical to the 10/5 backup afterwards. Still, back it up before a run
  that plays, as the feedback memory says.
- **No Python on this machine.** Use the Edit tool or perl for multi-line edits.

**Not done**: no disk cache (each track is read on every play, which is fine at these
timings), no fade-in (the spec has no motion tokens).

### Changes from session 35 (2026-10-05): last track on reopen, quality tier in the bar, Statistics tweaks

Four small requests from the user, one commit: **`309fb20`**, pushed to `origin/main`.
**503 tests pass.** All installed; the installed build starts. **Ask the user** to try
the three things only the real app can show: double-clicking the status line, a real
close-and-reopen (the bar should show the last track, and Play should start it), and the
quality label on a few kinds of file.

- **Double-clicking the status bar's dot or text opens Statistics** (the user's request,
  no tooltip). A `MouseBinding` (`LeftDoubleClick` → `ShowStatisticsCommand`) on
  `LibraryDot` and `StatusLine` in `MainWindow.xaml`. The text block is now left-aligned
  with a transparent background, so the target is the text itself, not the empty space to
  its right. It works whatever the line says (counts, scan or save messages).
- Verified: build clean, ThemeLab render of the status bar unchanged, 497 tests pass, and
  the installed build starts (UIA found `SearchBox`). **The double-click itself has not
  been exercised**: it needs the real mouse. One test run had a single failure that
  didn't repeat in seven more runs; the change is XAML only, so it is an existing flaky
  test, not yet identified.
- **QUALITY CHECK rows run from the highest claimed quality to the lowest** (the user's
  order): Fake 24-bit, Fake hi-res, Possibly fake hi-res, Likely / Possibly transcoded
  lossless, Upscaled / Possibly upscaled MP3. Only `QualityStatistics.Rows` changed; the
  saved results are keyed by flag, so `quality.json` needs no version bump. A new test
  pins the order (**498** tests). ThemeLab `--window stats` with a 20 s sample check
  prints the rows in that order. Installed; the installed build starts.
- **The now-playing bar shows last session's track at launch** (the user's request), instead
  of the blank art tile: art, title, artist, format line and duration, at 0:00, stopped.
  `RestoreLastPlayed` → `ShowRestoredTrack`, only when the song itself was found and shown
  (not for an album-only match). Nothing is loaded into the engine and nothing goes to
  Last.fm. **Play starts that track** within its album (`_restoredQueue`, captured at restore
  and mapped through `LibraryCopyOf` at play time), even after browsing elsewhere; the
  first track the engine reports clears it, and Play then behaves as before. This is not
  "resume on launch" (still not picked): nothing plays until Play is pressed, and it starts
  from 0:00. Since the bar is filled by `NowPlaying`, the window title and the row's
  now-playing triangle also show the track, just as they do after Stop.
  - Verified with ThemeLab `--window click --lastplayed "Ihlo|Union|D:\...\05. Triumph.flac"`
    (render and printout of the bar: art 128 px, "FLAC · 1,516 kbps · 44.1 kHz" before the
    next change made it "Hi-Res Lossless", 4:54) and
    the new **`--restoreplay 1 [--restoreplayfrom <artist>]`**, which presses Play at engine
    volume 0: Triumph starts, Next goes to Parhelion; the same after moving to Rush first.
    The shell's settings copy was backed up and restored (hash equal). 498 tests.
    Installed; the installed build starts. **Not seen on a real relaunch yet.**
  - Known gap: a background scan that changes the library rebuilds `Track` objects, so the
    row triangle can drop off until playback starts (the bar keeps its track).
- **The now-playing format line is the quality tier** (the user's call): "Hi-Res
  Lossless", "CD Quality Lossless", "Lossy" or "DSD", from `LibraryStatistics.Badge`
  (`Classify`, so it always agrees with Statistics; both lossy tiers read "Lossy"). The
  kind, bitrate and sample rate are no longer in the bar; they remain in the grid's
  columns. **Statistics' Audio Quality rows are renamed** to "Hi-Res Lossless", "CD Quality
  Lossless", "Lossy - Over 256 kbps" (256 itself included, as before), "Lossy - Under 256
  kbps", "Tracker Module", and **DSD is last** (`QualityTier` reordered). 5 new tests
  (**503**). ThemeLab: the bar reads "Hi-Res Lossless" for Ihlo's *Triumph* (24-bit), and
  `--window stats` shows the five rows in order; both rendered, looked at. Installed. Also
  recorded under Decisions in `design/progress.md`.

### Changes from session 34, part 2 (2026-10-05): the quality check in Statistics

The user asked for the Analyze findings across the library in Statistics ("Likely
Transcoded or fake Flac, Upscaled mp3, etc."). **Their choices**: a sampled check, started
by a button; all four kinds of problem; the *possibly* cases as separate rows.
**497 tests pass** (479 + 18). Commit `5b71b19`, pushed, with part 1.

- **Statistics → QUALITY CHECK** (left column, under Audio Quality), seven rows, yellow
  like Missing tags, each clickable to filter the library: Likely / Possibly transcoded
  lossless, Upscaled / Possibly upscaled MP3, Fake / Possibly fake hi-res, Fake 24-bit
  (`QualityStatistics.Rows`). A summary line ("982 of 26,861 tracks checked...") and one
  button: Check quality / Check the rest / Stop checking / All checked. **The section is
  live** (`QualitySectionViewModel`), unlike the rest of Statistics: the check runs in
  `MainViewModel` and carries on after the window closes; its progress and each saved
  batch update the open window. Progress also shows in the status bar (text and the
  scan bar: `ShowsProgress` / `ProgressFraction` now cover both a scan and a check).
- **Sampled reads** (`AnalysisSampling.Library`: three 10 s slices at ¼, ½, ¾; a track
  under a minute is read whole). `SpectrumAccumulator.Restart()` between slices so no
  transform spans a join; tested (a join heard as a click fills in the empty band).
  MP3s skip Prescan when sampled. **Measured**: the 30 test files gave the same verdict
  sampled as whole, 6-10× faster. Throughput is linear in threads: 13/s on 1, 42/s on
  4, 74-83/s on 8. `QualityScanner` uses a quarter of the cores, at most 4, at
  BelowNormal priority: about 11 minutes for the library.
- **The run over 1,200 random tracks changed three rules** (the flagged files were read
  through one by one):
  - **A cutoff under 11 kHz is the music, not an encoder** (`LowestEncoderCutoffHz`):
    *Father* (Aphex Twin, 2.2 kHz), an N64 Zelda track (6.4 kHz) and a Liquid Tension
    Experiment track (6.7 kHz) had been called lossy. Now *Can't tell*.
  - **MP3s less than 2 kHz short of their bitrate's cutoff are only *possibly*
    re-encoded** (new flag `PossiblyReEncodedLossy`): many 192 kbps files stop at 16 kHz,
    which older iTunes and Xing encoders do at any bitrate.
  - **Fake 24-bit counts samples, not bits** (`SpectrumAnalysis.ExtraBitsShare`): Bit
    Brigade's *Batman* is 16-bit music with 24-bit fades, so any-sample-uses-24-bits said
    "real" while the music isn't. Under half the non-silent samples using more than 16
    bits is now *Not true 24-bit*. Real 24-bit measured 99.4-99.6%.
- **The noisiest row is Possibly transcoded lossless** (19-20.6 kHz): ~2% of tracks,
  many of them early digital masters filtered at 20 kHz (*Brothers in Arms*, Doobie
  Brothers compilations, live Ozric Tentacles). That's why it's a separate row. A known
  way to separate those from LAME (LAME's patchy content above 16 kHz over time) was not
  built.
- **Results**: `%LOCALAPPDATA%\AudioFool\quality.json` (`QualityCache`), saved every
  30 s, at the end and on Stop; closing the app waits up to 3 s for the last save.
  Keyed by **file name + size + write time**, not path, so results follow a drive-letter
  change and a retagged file is checked again; `QualityCache.CurrentVersion` (1) must be
  bumped whenever the rules change, which makes the next check re-read everything. A
  file that's missing (drive out) isn't recorded; one that can't be decoded is, as
  *Can't tell*. The rows' filters match by key too, so a fixed (retagged) file drops out.
- **`Finding.Flag` / `Opinion.Flags`** (`QualityFlag`) are what's counted; the Analyze
  window is unchanged apart from the three rules above.
- **Verified** with ThemeLab **`--window stats --qualitycache <scratch file>
  [--qualitycheck 1] [--qualitystop <s>]`**, which loads the real library, runs the
  real check (reading `D:\Music`, results only in the scratch file) and prints the
  section. Stopped at 20 s: 982 tracks, 53 flagged, the section and button right; then
  resumed, which skipped those 982 and checked the other **25,879 in 9 min 26 s** with
  no failures. Whole library: likely transcoded lossless 135, possibly 519; upscaled MP3
  154, possibly 346; fake hi-res 75, possibly 52; fake 24-bit 123 (1,404 flags). The
  biggest albums per row are believable: stream-captured live sets (Justice at the Accor
  Arena as FLAC, Justice's Coachella as MP3), game rips (Wipeout Omega), live discs sold
  as hi-res (Rush's *Grace Under Pressure* Super Deluxe, Bring Me the Horizon's Royal
  Albert Hall), and 24-bit releases of 16-bit masters (Howard Shore's *The Two Towers*,
  *Sempiternal*). Render checked, 0 magenta. **The real app hasn't run a check**: those
  results are in a scratch file, so the user's first press of Check quality reads the
  whole library (about 10 minutes).

### Changes from session 34 (2026-10-05): Analyze, a spectrogram and a quality opinion

Right-click a song → **Analyze…** (the user's request). User-facing behaviour is in the
README's *Analyzing a track*. **479 tests pass** (436 + 15 in `SpectrumAnalysisTests` +
28 in `QualityOpinionTests`). Installed; the installed build starts. Committed with part 2 as `5b71b19`, pushed.

- **The user's choices**: a spectrogram (over an average-spectrum curve or both), a
  heat-map colour scale (over greyscale or teal; it adds colours outside the spec's
  roles, kept to the picture and defined as tokens), and a separate, non-modal window.
- **Core, `AudioFool.Core/Analysis/`**:
  - `Fft` (radix-2, tables built once) and `SpectrumAccumulator` (pure, no BASS): Hann
    windows of about 11 Hz per bin at any rate (4,096 at 44.1/48 kHz, doubling with the
    rate), channels averaged by power so out-of-phase content can't cancel, a full-scale
    sine at 0 dB. It keeps the mean and peak spectrum per bin and an 800 × 512 picture
    (mean power per band per time slice). For a lossless source over 16 bits it ORs
    every sample on the 24-bit grid to count the bits used.
  - `TrackAnalyzer`: its own decode-only float stream (never the engine's or the
    device), MP3s prescanned, **DSD converted at 176.4 kHz** through BassDsd's
    `CreateStream` frequency argument rather than the global `DSDFrequency`, which
    playback owns. About 400× real time off the USB SSD: 0.2–0.9 s for a CD-rate track,
    1.3–2 s for 96/192 kHz and DSD128.
  - `QualityOpinion`: pure, tested on synthetic spectra. The rules and why are in its
    doc comment. A **cliff** is ≥ 20 dB lost within 0.5 kHz against the median of the
    kilohertz below (so one loud FM-synth partial can't fake one), never coming back
    within 15 dB. Lossless: cliff < 19 kHz → *Made from a lossy file*; 19–20.6 kHz →
    *Possibly from a lossy file*; 21 kHz+ is a converter's filter. Lossy: a cutoff under
    LAME's usual one for the stated bitrate → *Re-encoded from a lower bitrate*. Hi-res
    and DSD: a cliff under 24.5 kHz, a notch at 22.05 or 24 kHz with louder content
    both sides (imaging), or 25–32 kHz at decoder silence → *Not true hi-res*; 25–32 kHz
    ≥ 35 dB under the 14–19 kHz treble → *Possibly not*. 24-bit using ≤ 16 bits → *Not
    true 24-bit*. Too quiet, too short, or no treble → *Can't tell*. The worst finding
    sets the verdict; findings are listed worst first.
- **Measured on the real library before setting the thresholds** (headless probe in
  the session scratchpad, `aprobe`): 25 real files and five fakes made from them (an MP3
  decoded to a 16-bit WAV, twice; a CD track and an MP3 upsampled to 96 kHz through
  BASSmix; 16-bit padded to 24). Every genuine CD-rate file reads Consistent, including
  Bring Me the Horizon and James Taylor, whose converter walls are at 21.1–21.3 kHz;
  every fake is caught. MP3 cutoffs: 320 kbps 20.3 kHz (ELO's 21.6), 198 kbps 18.8, a
  48 kHz 128 kbps 16.8. Two real hi-res files are flagged: **Metallica's 96 kHz *My
  Friend of Misery*** (a gap at 22.05 kHz with the treble mirrored above it, plainly
  visible in the picture) and, as *possibly*, **Evangelion's 192 kHz *Interference of
  Others*** (only noise above 25 kHz, 43 dB down). BBNG, Daft Punk, RHCP, Muse, Pet
  Sounds, the 16/96 MMW and the Pink Floyd DSD128 read Consistent.
- **Not decided by the data**: the 19–20.6 kHz band. A 320 kbps MP3 decoded to FLAC
  cuts at 20.2 kHz and reads *Possibly*, not *Made from*; no honest master in the sample
  had a wall that low, but the line was drawn conservatively. **Bit counting needs BASS
  to hand back exact 24-bit values as float**; it does (real 24-bit FLACs read 24, the
  padded fake 16).
- **App**: `AnalysisViewModel` runs it on `Task.Run` and builds the frozen bitmap there
  (`Services/SpectrogramImage`, the six `color.spectrogram.level*` tokens between
  `analysis.floorDb` −120 and `analysis.topDb` −20). `AnalysisWindow` draws the axes,
  colour legend and the dashed cutoff marker (`color.spectrogram.marker`, the slider
  thumb's off-white) in code from the picture's size; the picture is an `ImageBrush`
  so the row, not the 512 px bitmap, sets its height (min `analysis.pictureHeight`
  300, growing with the window). Placed over the main window, each further one stepped
  down a title bar's height; kept on screen as the opinion makes it taller. Escape or
  Close closes it and cancels. `MainViewModel.AnalyzeTrackCommand`; the row's menu
  item sits under Edit Tags….
- **Verified** with ThemeLab **`--window analysis --file <audio> [--state
  working|error]`** (runs the analysis with the dispatcher pumped, then renders and
  prints the opinion, the picture frame, every axis label and the marker) on Metallica,
  the upsampled-MP3 fake, a 320 kbps MP3, the DSD and the two states: renders checked,
  0 magenta on `--bg "#FF00FF"`. **`--trackmenu 1`** opens a song row's menu off-screen:
  Edit Tags… and Analyze… both bound, enabled, parameter the row's track.
  `--window tokens` passes; `ThemeTokensTests` counts 59 colours.
- **Not exercised in the running app**: the real right-click, the window shown on
  screen (placement, cascading, resizing, Escape), and cancelling by closing mid-read.
  The user should try it.

### Changes from session 33 (2026-10-05): full release date in the header, reopen where you left off

Commits `49e04de` and `b9fa5b2`, pushed to `origin/main`. **436 tests pass** (425 + 5
for the header date + 6 in `LastPlayedTests`). Both installed.

- **The album header's year line shows the full date** (the user's request), "2022-05-13"
  when the files carry one, "2022-05" for a month, else the year, else "Year unknown":
  `Album.DateDisplay` = `SortDate ?? YearDisplay`, bound through `AlbumHeaderYear`. It is
  the date the album *sorts* by, the earliest across its tracks, so a year-only track in
  an otherwise dated set makes the header show the bare year. The Albums list subtitle
  still shows only the year (not asked for). This closes "Show the full date in the album
  header?" in *Suggested next steps*. Seen in the running app only as far as the install
  went; the user has not commented on it.
- **The app reopens on the song that was playing** (the user's request: "open it at the
  position of the last Artist/Album/Song"). It selects; it does **not** play or load the
  engine (offered: resume playback too; not picked).
  - **Saved**: `AppSettings.LastPlayed` (`LastPlayedTrack(FilePath, Artist, Album)`, the
    artist and album as the sidebar names them), written from `OnEngineTrackChanged` →
    `RememberLastPlayed`, so each *started* track, not a clicked row, and a crash keeps
    it. Like `SaveVolumeOnly`, it re-reads settings from disk and changes only this field,
    so it can't clobber folders edited elsewhere. Skipped when unchanged.
  - **Restored**: `ApplyLibrary` calls `RestoreLastPlayed` once, on the first library with
    tracks in it (the cached one at startup, or the scan's if there is no cache).
    `MusicLibrary.Locate` finds it by path; failing that (a drive that changed letter) by
    artist and album name, then the file name within that album; an album with no file
    match opens the album with no song selected. If the artist or album isn't in the view
    (gone, or its folder unticked) nothing happens and the first artist opens as before.
    The song is highlighted through `RevealTrackRequested`, which `MainWindow` answers
    by selecting and scrolling the grid at `Background` priority (the rows bind after the
    album selection that raised it). The Artists and Albums lists scroll through their
    existing `BrowserList_SelectionChanged`.
  - **Known gap**: the background scan that follows startup calls `ApplyLibrary` with
    `keepSelection: true` when it finds changes. That keeps the artist and album but
    clears the song highlight (the track list is rebuilt). Not handled; the restore
    runs only once.
  - **Verified** with a new ThemeLab option, **`--lastplayed "Artist|Album|path"`** (with
    `--window click` or `--window main --typeahead zzzz`), against the cached library
    with a throwaway settings object: the exact path, an `E:` path (drive changed) and a
    missing album each give the expected artist, album and song row, with nothing
    playing and the selection stable after 800 ms. **ThemeLab always selects row index 4
    before it renders (`grid.SelectedIndex = --row`, default 4), so a render shows row 5
    highlighted whatever was restored.** Trust the printed "opened on:" line, which is taken
    before that. The real app's first-launch behaviour (no saved song yet, so it opens on
    the first artist) and the real restore on a relaunch **have not been seen**.
  - The shell's `settings.json` is the packaged-AppData copy, so it never shows what the
    real app saved; ask the user whether it worked.
- **Andy Timmons albums are repaired** (the user's note, 2026-10-05); the "not repaired"
  line in session 32's section is corrected. Not to be raised again.
- **The Statistics quality tiers were explained, not changed** (the user: "keep it as it
  is"): lossless is *Hi-res* when over 16 bit or over 48 kHz, else *CD quality*;
  `LibraryStatistics.Classify`. A track with no bit depth or sample rate lands in CD
  quality.
- **The user's screenshot confirms** the session 32 playback bar (Previous on the Songs
  panel edge, Repeat over Shuffle) and the 4 px menu corners in the running app.

### Changes from session 32 (2026-10-02): a filtered album is no longer taken for the whole

The user noticed that the Statistics **Track total** and **Disc total** filters showed
almost every album with exactly 9 tracks.

- **What it was.** A search or Statistics filter builds its albums from the tracks it
  matched, so an album there can be a part of one. The header counted only that part.
  In 55 of the 68 albums missing a track total, the tracks missing it are exactly 1–9.
  All 570 were last written on 25 September, before AudioFool rewrote numbers at all
  (28 September), one file a minute or so apart. That looks like "01/13" being fixed
  to "1" by hand, which drops the total, and only 1–9 have a zero to fix. The cause
  is a guess; the dates are measured.
- **The trap was real.** On 2 October, 07:44–07:52, tracks 1–9 of four Andy Timmons
  albums (*Ear X-Tacy*, *Pawn Kings*, *Orange Swirl*, 13 tracks each, and *Ear X-Tacy 2*,
  10) were written as "1/9" … "9/9" on `D:\Music`, read from the raw `TRCK` frames.
  AudioFool never fills a total in by itself, so 9 was typed, presumably from the
  header, and Edit Album Tags opened from a filtered album wrote only those 9 tracks.
  **Repaired by the user** (confirmed 2026-10-05); nothing more to do or report
  on it.
- **The header says "9 of 13 tracks"** while a search or filter shows part of an
  album (`Album.TrackCountDisplayWithin`), "13 tracks" otherwise. The duration and
  the track list still cover only the shown tracks.
- **Edit Album Tags always edits the whole album** (the user's call), from the album
  row and the header art alike: `EditAlbumTags` looks the album up with
  `MusicLibrary.WholeAlbumOf` in `_folderFilteredLibrary`, by the first track's
  path, since the two builds can settle on different spellings of a name. Ticked
  folders only, as before. To edit just the shown tracks: select them in the grid,
  then Edit Tags.
- **Verified**: 6 new tests in `AlbumHeaderTextTests` (**400** in total). ThemeLab
  `--window click --click "Track total" --search "Andy Timmons"`, which now prints the
  header and the album the dialog would get, on the real cache: *Ear X-Tacy* shows 9
  tracks, header "9 of 13 tracks", the dialog would edit all 13; unfiltered it reads
  "13 tracks". Render checked. Installed; the installed build starts.

**Albums that disagree** (later in session 32, the user's request: "where the Track
Count or Disc # Varies", for the cleanup ahead). A new Statistics section under
Missing tags, yellow like it, with three rows:

- **Track total varies**: tracks *on the same disc* name different totals, or some
  name one and some don't. A set whose discs each carry their own total (79 albums
  today) is fine. **60 albums, 831 tracks.**
- **Disc total varies**: the tracks name different disc totals, or some have none.
  **30 albums, 600 tracks.**
- **Disc # doesn't fit** (the user's choice over "every album whose disc # varies",
  which would be 123 ordinary multi-disc sets of 126): some tracks have a disc number
  and some don't, or a disc number is past its own total. **4 albums, 76 tracks**:
  both Wipeout soundtracks, *And-Thology 2*, and Boys Noize *Mayday* ("disc 2 of 1" on
  every track).
- **Clicking a row shows whole albums**, header "13 tracks", so Edit Album Tags fixes
  it, and a fixed album leaves the filter. `TrackFilter` gained
  `TrackFilter.ForAlbums(name, Func<Album,bool>)`, which groups the tracks it's
  given with `LibraryScanner.Build` (the sidebar's own grouping) and keeps every track
  of a matching album. `Matches(track)` became `Apply(tracks)`, since an album filter
  can't judge one track. `ApplyToView` calls it on every rebuild (one more `Build`,
  ~40 ms, only while such a filter is on).
- **Built from cached ints**, so "01" against "1" isn't caught, though the album
  dialog says "Varies" for it. That needs the spellings, which aren't cached.
- The checks are public (`LibraryStatistics.TrackTotalVaries`, `DiscTotalVaries`,
  `DiscNumberDoesNotFit`). 8 new tests, and the every-row test now covers these rows
  (**408** in total).
- **Statistics is about 215 px taller**: content 1,294 px at 900 wide (was 1,079).
  On 1080p it caps at the work area and scrolls, with no bar.
- **Verified** on the real cache with ThemeLab: `--window stats` (now prints the
  section; 0 magenta, render checked) and `--window click --click "<row>"` for each
  row: whole albums shown, Edit Album Tags would get every track. Installed; the
  installed build starts.
- **Tab in an in-place edit walks the column** (later in session 32, the user's
  report: Tab while editing # jumped to the Previous button). The table is one Tab
  stop (session 29), so Tab left it. Now `TrackGrid_PreviewKeyDown` sends Tab in an
  edit box to `SaveAndEditNextRow(1)`, as Enter does, and Shift+Tab to `(-1)`, the row
  above. On the last row (or first, going up) it saves and stays in the table. Tab
  outside an edit is unchanged. ThemeLab `--window edit` gained a Tab walk: after a
  save, unchanged, up a row (called directly: WPF reads the real Shift key, so
  Shift+Tab itself is untested), and the last row, all OK; `library.json` and
  `settings.json` unchanged. Not yet tried with the real keyboard. Installed.
- **The arrow keys carry on from a type-ahead match** (later in session 32, the
  user's request: type "King" over Artists, then Down to Kingdom Hearts). Type-ahead
  selected the row but left keyboard focus where it was, so the arrows went
  elsewhere. `SelectTypeAheadMatch` now also calls `FocusSelectedRow`, which focuses
  the selected `ListBoxItem` at `ContextIdle`, after the selection's own scroll
  (scrolling it in first if it has no container yet). The list's own arrow keys
  then move the selection. Artists and Albums both; no match leaves focus alone.
  Rows still draw no focus outline (session 26's decision). ThemeLab **`--typeahead
  <text> [--typeaheadlist AlbumList] [--tabfrom X] [--keys "Down,Up"]`** sets the
  hover target directly (hover can't be faked), types, then sends real key presses:
  "King" → King Gizzard, Down → Kingdom Hearts, Down → Koan Sound, Up → Kingdom
  Hearts, with focus on each row. Installed.
- **A long dialog title no longer pushes Close off the window** (later in session 32,
  the user's screenshot: *Andy Timmons Band Plays Sgt. Pepper*'s album dialog, Close
  half cut off and clicking it unreliable). The window was the right size; WPF-UI's
  TitleBar puts its header in an **Auto** column (`[Auto, *, Auto, Auto=buttons]`), so
  the title was offered unlimited width, never trimmed, and the row came to 473 px in
  a 460 px window. `theme.dialogTitle` is now a DockPanel whose `MaxWidth` is the
  bar's width less the three window buttons and the left margin
  (`Theming/TitleRoomConverter.cs`, from the tokens), so a long title ends in "…".
  ThemeLab's dialog title-bar printout now walks the header's ancestors with their
  widths and column definitions, which is how the Auto column was found. Verified:
  that album's Close at x 414–460 (was 427–473); all five dialogs' Close ends at
  the window's edge; a short-title dialog 0 px different, 0 magenta. Installed.
- **Close hovers grey, its X staying red** (later in session 32, the user's call,
  overriding spec 6.1's red fill with a white X), on the main window and every
  dialog, matching Minimize and Maximize. `PaletteRedBrush` (the key WPF-UI's title
  bar reads for close's hover) now points at `color.window.buttonHover` in
  `MainWindow.xaml` and `theme.dialogTitleBar`. The red X is set on the Path in
  `theme.windowButton`'s Close trigger: WPF-UI sets close's
  `MouseOverButtonsForeground` to white itself, which a style setter can't beat.
  Verified: ThemeLab `--winhover Close` at 1720 wide (at 1300 the window is wider
  than the bitmap and the buttons fall outside it): close's background `#AFACA8`, the
  same as Maximize's hover, the X still 52 red pixels, none white; a dialog's brush
  dump gives close `MouseOverBackground` `#0F000000` (Maximize's) and the Path red.
  Recorded under Decisions in `design/progress.md`. Installed.
- **Two-digit track numbers are no longer cut off while edited in place** (later in
  session 32, the user's screenshot). The edit box filled only the # cell's 16 px
  number column; it now spans the whole cell while editing (details in
  `design/progress.md`). ThemeLab `--window edit` gained a step that types "12" and
  "123" (the latter on the widened column) and reports `OK` / `CLIPPED` from the text
  box's extent against its viewport. Installed.
- **The speaker and volume fill are a lighter grey** (later in session 32, the user's
  call), `#75726E`, the colour of Shuffle and Repeat when off. The Statistics bars share
  `slider.volumeFill` and lightened with it, as the user asked. Details in
  `design/progress.md`.
- **The status line's tooltip is gone** (the user's call): it repeated the text.
- **Clicking a playback control no longer leaves the teal focus ring on it** (the
  user's call). `Theming/ClickFocus.cs` (`theme:ClickFocus.Skip`) keeps keyboard focus
  where it was during a click; Tab still shows the ring. ThemeLab `--clickfocus 1`
  checks it. Details in `design/progress.md`. Installed.
- **Menu corners match the spec** (later in session 32, from the open item in
  `design/progress.md`): the logo drop-down and submenus are 4 px (`radius.surface`), item
  highlights 3 px (`radius.row`), context-menu items too. WPF-UI's templates hard-code
  8 and 4, so `Theming/MenuCorners.cs` sets the named template parts (`SubmenuBorder`,
  `Border`) when a menu opens, from `ThemeService.Apply()`. **A class handler on
  `Loaded` never fires for a `MenuItem`** (traced, not guessed), hence `SubmenuOpened`
  and `ContextMenu.Opened`; the items wait for `DispatcherPriority.Loaded` because
  data-bound rows get containers only once the open menu is laid out. Verified with
  ThemeLab `--window main --menu 1 --menucorners 1` (every submenu opened, two sample
  folder rows added; `--menucorners <png>` renders the drop-down) and `--artmenu 1`.
  The shadow and slide-in WPF-UI gives the drop-down are unchanged. Installed; the
  installed build starts. Not seen in the running app with a real pointer.
- **Save Embedded Art** (later in session 32, the user's request). A third button in
  *Edit Album Tags…*, under Choose Image / Search Internet: it writes the art embedded
  in the album's files to `cover.jpg` beside them, **at once** (not on Save, and the
  files are written even if the dialog is then cancelled).
  - **Core**: `AudioFool.Core/Art/EmbeddedArtExtractor.cs`, BASS-free, 15 tests in
    `EmbeddedArtExtractorTests` on real FLAC and MP3 fixtures (one asserts a JPEG comes
    out **byte for byte**). Per folder (a "Disc 1/" + "Disc 2/" set gets one each) the
    picture with the most pixels wins, the larger file on a tie; `TagReader.ReadEmbeddedArt`
    picks front cover else first within a file. **An existing `cover.jpg` is replaced
    only by more pixels** (the user's choice; an unreadable one is replaced). Other
    cover files are left alone. `Describe` writes the message; outcomes are Saved /
    KeptExisting / NoArt / Unsupported (a GIF, say: 2 of the library's 2,398 folders) /
    Failed.
  - **PNG becomes JPEG** (the user's choice, over keeping `cover.png`; it is the one place
    anything is re-encoded). `Services/CoverJpeg.cs`, passed in as a delegate because Core
    has no codec. **Both Windows encoders halve the colour (4:2:0) even at quality 100**
    (WPF's `JpegBitmapEncoder` and GDI+, measured from the SOF header, and they produce
    the same file size), so it uses the new package **BitMiracle.LibJpeg.NET 1.5.324**
    (BSD-style, managed, netstandard2.0): quality 100, 4:4:4, optimised Huffman tables.
    GDI+ only decodes; transparency is composited onto white by hand, pixel for pixel.
    Checked on a 1200 × 1200 PNG with semi-transparent edges: over 64,800 pixels incl. all
    four edges, mean difference 0.95 of 765, worst 7. ImageSharp was ruled out for its
    split (commercial) licence. About 4.5% of the library's albums (109 of 2,398) have
    PNG art.
  - **Wiring**: `TagEditViewModel.SaveEmbeddedArtCommand` (album dialog only); the result
    goes to `ArtMessage`, shown on the footer line when there is no validation error
    (`FooterMessage`). The cover paths go to `SavedCovers`; `MainViewModel`
    `FinishAlbumDialogAsync` waits for the extraction, then `AdoptFolderCoversAsync` sets
    the tracks' `FolderArtPath` (`Track.WithFolderArt`, tested to copy every field) and
    drops the album's cached pictures, **before** Save runs: Save writes
    `track.FolderArtPath` back, and a stale null would have undone it.
  - **Layout**: the right-hand column is top-aligned and no longer pinned to the art's
    72 px, to fit the second button row; it is about as tall as the art column, so the
    dialog is no taller.
  - **Verified** with ThemeLab **`--window tags --album x --scratch <folder>
    --saveembedded 1`**, which builds the album from *copies* in a temp folder and presses
    the button: *The Advantage* (26 MP3s): `cover.jpg` 76,161 B, 576 × 576, identical to the
    embedded picture; *Unity*, likewise; a PNG scratch album as above. The real
    `D:\Music` folders were not written to. **Not exercised in the running app**: the
    real button press, `AdoptFolderCoversAsync` (it persists `library.json`) and the
    cancelled-dialog path. Installed; the installed build starts.
- **The playback controls moved left, and Repeat/Shuffle became a small stack** (later
  in session 32, the user's call, in two steps). First the divider was centred in the
  Shuffle–Previous gap (since replaced). Then, after a ThemeLab render comparing 28,
  30 and 32 px: the transport is **Previous, Play/Pause, Next, then Repeat over
  Shuffle** as 32 px buttons (`theme.modeButton`, tokens `modeButtonSize` /
  `modeButtonGap`), and **Previous's left edge is on the Songs panel's outer edge**
  (x 528 at the default widths; it follows the splitters; `--playprobe 1` prints
  `off by 0 px`). The now-playing zone is 451 px, the minimum window width **1,333 px**
  (was 1,422 this morning), and the Tab order is Previous → Play/Pause → Next → Repeat
  → Shuffle. **Repeat-one's "1" moved out of the icon onto a 13 px red disc on the
  button's top-right corner**: inside the 16 px icon it was a smudge at 6.5, 9 and 11 px.
  Details, numbers and the spec rewording in `design/progress.md`. Checked: probe at
  three Albums widths and at the minimum width, the Tab walk, `--window tokens`, focus
  rings on the stack (`--focus X --focusvisual 1`), the badge at 4x, 425 tests.
  **Seen in the running app** (the user's screenshot, 2026-10-05: Previous on the
  panel edge, the Repeat/Shuffle stack, the 4 px menu corners).
- **The shell's `library.json` is current as of 2 October, 08:03**: the installed
  build's smoke test rescanned `D:\Music` into the container copy. The figures above
  include this morning's "of 9" edits.

### Changes from session 31 (2026-10-01): new PS1 theme, old theme files removed (step 6, done)

The last item of `design/progress.md`, which has the decisions. **The new theme is now
the only one**: the app's look comes from `theme-tokens.json` (via `TokenResources`)
and `Theming/Chrome.xaml`, over WPF-UI's Light base, and nothing else.

- **Deleted**: `Themes/DesignTokens.xaml`, `Components.xaml`, `Ps1Theme.xaml`,
  `Ps1Motion.xaml` (the whole `Themes/` folder), every `Af*` resource, and
  `ThemeService`'s accent pinning. `MainWindow` lost the `AfWindowDecor` slot (the
  `color.window.bg` border already covered it).
- **No WPF-UI key overrides were needed in their place.** With `Ps1Theme.xaml` gone, 154
  of the 161 WPF-UI keys it restated fall back to WPF-UI's Light values, and none of them
  shows: every visible piece has a template of its own or reads tokens. The one
  exception was the filter chip's hover and press (a stock `ui:Button`), which now set
  `control.faceHover` / `status.chipBorder` / `text.primary` like `theme.chip`.
- **WPF-UI's accent** is now `color.control.iconNeutral` (was a hard-coded `#4A4746`).
  Nothing visible reads it; it keeps the Windows accent out of any stock state.
- **The logo menu's items are regular weight** (the user's call), like the context menus.
- **Verified**, against a ThemeLab build of HEAD in a scratch worktree, on 35 renders on
  magenta: the main window in eleven states (including 125%), the menus, tooltip and
  context menu, and every dialog in every mode. All 0 px different, apart from the three
  logo drop-down renders, which are 5–10 px narrower for the regular weight, and 0 px
  different from an intermediate build that changed only the weight. New ThemeLab **`--brushdump 1`** writes every
  brush and corner radius held by every element beside each PNG, including the hover,
  press and disabled brushes a stock control holds ready, which a render can't show. That
  is what found the chip. **`--dumpkeys <file>`** gives `--dump` a key list. Also: the
  token check, `--tabwalk 18`, the in-place edit suite (with `library.json` and
  `settings.json` byte-identical afterwards) and 394 tests. Installed; the installed
  build starts.
- **Found, not fixed**: ThemeLab draws the main window at 1554 × 978 inside a 1720 × 1080
  bitmap, so the main renders have magenta outside the window, at HEAD as well. Probably
  the saved window placement in the shell's container `settings.json` (session 21).
  `--artmenu` had found no menu since session 27, because it looked the art up by the old
  `AfArtFrameLarge` style; fixed.

### Changes from session 30 (2026-10-01): new PS1 theme, dialogs (step 6)

Step 6 of `design/progress.md`, which has the details, measurements and decisions.
The spec doesn't cover the dialogs; the user chose to match the main window using
the existing tokens, one dialog at a time.

- All five dialogs (art viewer, Last.fm, Statistics, cover art search, Edit Tags) are
  on the central theme and use **no `Af*` resource**. Shared pieces in
  `Theming/Chrome.xaml`: `theme.dialogTitleBar` + `theme.dialogTitle` (set
  `Header="{DynamicResource theme.dialogTitle}"` on the bar itself: WPF-UI sets
  `Header` locally), `theme.dialogButton` (`.primary` is semibold), `theme.dialogTextBox`
  (`.multiline`), `theme:Placeholder.Text` (`Theming/Placeholder.cs`),
  `theme.dialogIconButton` + `theme.clearGlyph`, `theme.checkBox`, `theme.link`.
- The user's calls: light buttons, primary told apart by weight only; coloured window
  buttons on dialogs; errors and cautions in `text.heading` semibold, no colour;
  Statistics' missing-tags bars yellow; no focus outline on Statistics rows.
- **Selection grey only reads on panel grey** (`#BAB7B2` against the window's
  `#BAB7B3`), so lists in dialogs sit in a `theme.panel`.
- ThemeLab: `--window artview` renders (`--artfile` for any image); the dialog modes
  raise `Loaded` on the title bar (`RaiseLoaded`) so WPF-UI colours the window buttons,
  and print title bar, fields and buttons. A dialog's last bitmap row can show magenta
  when its content height isn't whole: ThemeLab rounding, not a gap.
- ~~**Still to do:** delete the old theme files.~~ Done in session 31.
- 394 tests. Installed.

### Changes from session 29 (2026-10-01): new PS1 theme, polish (step 5)

Step 5 of `design/progress.md` (parts 1–3), which has the details, measurements and decisions.

- Focus outlines, accessible names, a working Tab order, Escape clears search (parts 1–2).
- Part 3: header title box matches the mockup (39 px); the Last.fm chip shows **only
  while disconnected or needing reconnect**; the empty state, the in-place edit box,
  tooltips, context menus and the logo drop-down are on the central theme (tooltips
  and menus app-wide, dialogs included). Empty-state wording is
  `Core/Library/EmptyStateText` (tested).
- ThemeLab: `--empty library|search`, `--popupshot <png>` (menus and a tooltip drawn
  without opening a popup), `--dpi`, `--focusvisual`, `--peers`, `--tabwalk`. The
  `--window edit` mode must run from ThemeLab's default `bin\Release\...` folder: it
  finds the test fixtures relative to its own exe.
- 394 tests. Installed.

### Changes from session 28 (2026-10-01): new PS1 theme, playback bar and status bar

Step 4 of `design/progress.md`, which has the details, measurements and decisions.

- The playback and status bars are on the central theme (`theme.transportButton`,
  `theme.slider`, `theme.chip` and friends in `Theming/Chrome.xaml`; the molded
  button face is `Theming/MoldedFace.cs`). The Shuffle button lines up with the start
  of the song list, measured live by `MainWindow.AlignPlaybackBar`, so it follows
  the splitters.
- **New behaviour:** the speaker mutes and unmutes (restoring the level;
  `Core/Playback/VolumeState`), and the status bar shows the library size in whole GB
  (`Core/Library/LibrarySummary`). The output readout reads "Shared · 96 kHz / 32-bit
  (resampled)" / "Exclusive · 44.1 kHz" (`Core/Playback/OutputReadout`).
- **Minimum window width is now 1,422 px**, so the seek track keeps at least 120 px.
- ThemeLab: `--playprobe 1` measures both bars; `--shuffle 1`, `--repeat all|one`,
  `--muted 1`, `--exclusive 1` (settings file restored afterwards) and
  `--albumswidth N` set up states for it.
- 384 tests. Installed.

### Changes from session 27 (2026-10-01): new PS1 theme, album header and song table

Step 3 of `design/progress.md`, which has the details, measurements and decisions.

- The album header and song table are on the central theme (`theme.song*` in
  `Theming/Chrome.xaml`). Column widths, alignments and text styles come from
  `songTable.columns` through `MainWindow.ApplySongTableLayout`. The 22 px music-note
  column is gone; the now-playing triangle sits in the # cell, flagged by the inherited
  `TrackRow.IsNowPlaying`.
- **Column auto-fit is removed** (the user's call). Columns can still be resized and
  reordered by dragging, and headers still sort, with no arrow. The header lost its
  disc-count line. Duration reads "1:06:27" (the clock format; the user overrode the spec).
- ThemeLab: `--songprobe 1` prints the header and table geometry and each row's colours.
  `--longalbum N` gives a 100+ track album, `--longtitle "<t>"` a long header title,
  and `--window clicks` checks that a double-click plays the clicked row (silent,
  scratch tracks).
- **Two-line title clamp fixed**: layout rounding broke WPF's trimming, so long album
  titles showed one line and "…". Clamped type styles now turn layout rounding off
  (`TokenResources`). ThemeLab `--albumprobe 1` prints each album title's clamp;
  `--clampprobe "36,44,53"` renders the longest at each MaxHeight (`--clampstack`,
  `--clampround 0` to experiment).
- 366 tests. Installed.

### Changes from session 26 (2026-10-01): new PS1 theme, title bar and panels

Step 2 of `design/progress.md`, which has the details, measurements and decisions.

- Title bar, the three panel shells, and the Artists and Albums rows are on the central
  theme. The new components are in `src/AudioFool/Theming/Chrome.xaml` (keys `theme.*`).
- **No horizontal scrolling anywhere**, and vertical scrollbars are hidden (the user's
  call). The window's minimum width is calculated from the tokens so the song table fits:
  1,277 px today (was 900).
- `LetterSpacing.Em` spaces labels by em. `MainWindow.IsTrimmed` handles the album
  title's two-line clamp.
- ThemeLab: `--winhover Minimize|Maximize|Close` (WPF-UI's own hover on one window
  button) and `--type "<text>"` (text in the search box).

### Changes from session 25 (2026-10-01): new PS1 theme, foundation

The user has a full design for a new, lighter PS1 theme in `design/`: `theme-spec.md`
(source of truth), `theme-tokens.json`, `progress.md` (the five-session plan, with
notes and decisions) and `screenshots/`. **Read `design/progress.md` before any theme
work**; it supersedes the design-system notes below for anything the new theme
covers. Work is on `main`, no branch, at the user's request.

- **One central theme from `theme-tokens.json`**, embedded in the exe and loaded first
  in `ThemeService.Apply()`. Resources are named by JSON path (`color.panel.bg`,
  `type.albumTitle`, `albums.rowHeight`). XAML reaches them with `{DynamicResource}`,
  `{theme:Token}` (converts a number to the property's type) and `{theme:Thickness}`.
  The full mapping and its one limit (style setters) are in `design/progress.md`.
- **No screen changed yet.** The old `Af*` / `Ps1Theme.xaml` resources still drive
  every window. Seven ThemeLab renders are 0 px different from before.
- **ThemeLab `--window tokens`** checks all 332 resources from the real MainWindow,
  the XAML usages, and the font files, and draws a specimen sheet. It exits 1 on a
  problem.
- **Fonts**: WPF resolves "Segoe UI Variable Text" to the real variable font, with
  real 400 and 600 faces.
- `CLAUDE.md` was UTF-16, which git treats as binary; it is UTF-8 now.
- 27 tests in `ThemeTokensTests`, **363** in total.

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
| ~~`src/AudioFool/ViewModels/ThemeItem.cs`~~ | *Deleted in session 18* with the Themes menu. Was the observable VM for one theme menu item. |
| `src/AudioFool/ThemeService.cs` | `Apply()`, called once from `App.OnStartup` before the window is built: merges `Ps1Theme.xaml` (plus `Ps1Motion.xaml` when Windows animations are on), then applies and pins the accent. Since session 18 it no longer switches themes or sets a backdrop. |

## New source files added in session 6

| File | Purpose |
|---|---|
| `src/AudioFool.Core/Library/TagEdit.cs` | `TrackTagEdit`, `AlbumTagEdit`, `ArtPayload` — immutable records describing a pending write. Their positional fields are authoritative. Since session 11 their init-only members (`NumberEdit?` counts, `TagDetailsEdit`) use null for "keep", and `TagDetails` holds what a file currently has for the five detail fields. |
| `src/AudioFool.Core/Library/TagWriter.cs` | Static write layer: `WriteTrackTags`, `WriteAlbumTrackTags`, `WriteFolderArt`. Never throws — all results are `TagWriteResult`/`FolderArtWriteResult` records. Re-stamps `FileSize`/`ModifiedUtc` via `FileStamp.For(path)` after each write so `Track.MatchesFile` stays correct. |
| `src/AudioFool/ViewModels/TagEditViewModel.cs` | Backs `TagEditWindow` for both single-track and album-batch modes. `IsAlbumMode` flag drives which fields are visible. `BuildTrackEdit()` / `BuildAlbumEdit()` / `PickedArtPayload()` are read after `ShowDialog() == true`. |
| `src/AudioFool/TagEditWindow.xaml[.cs]` | Modal dialog (FluentWindow, `WindowBackdropType="None"`, owner-centered). Save/Cancel in code-behind set `DialogResult`. Album mode hides Title and the Track # row and shows the art panel and a standalone Track Count, via `Visibility` bindings. Disc and the five detail rows (each with a ✕ clear button) are in both modes. |
| `tests/AudioFool.Core.Tests/TagWriterTests.cs` | Round-trip tests: all fields survive write+read; art bytes match; `FileStamp` changes after write; album mode leaves Title/Track# untouched; missing file fails cleanly. |
| `tests/AudioFool.Core.Tests/TestData/sample.flac` | 0.5 s silence fixture for tag-writer tests (generated by ffmpeg). `.gitattributes` marks as binary. |
| `tests/AudioFool.Core.Tests/TestData/sample.mp3` | Same for MP3 path (different TagLib code path). |
| `tests/AudioFool.Core.Tests/TestData/cover.jpg` | 8×8 gray JPEG for art round-trip tests. |


## New source files added in session 7

| File | Purpose |
|---|---|
| ~~`src/AudioFool/Themes/DesignTokens.xaml`, `Components.xaml`, `Ps1Theme.xaml`, `Ps1Motion.xaml`~~ | *Deleted in session 31.* The old design system (`Af*` keys) and the old PS1 theme, replaced by `theme-tokens.json` and `Theming/Chrome.xaml` (sessions 25–31). In git history before that. |
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

## New source files added in session 25

| File | Purpose |
|---|---|
| `src/AudioFool.Core/Theme/ThemeTokens.cs` | Parses `design/theme-tokens.json` into colours, numbers, texts, flags, shadows and type styles keyed by JSON path. `ThemeColor` (hex / `rgb()` / `rgba()`), `ShadowLayer` (CSS box-shadow lists), `ThemeTokenException` names the bad path. |
| `src/AudioFool/Theming/TokenResources.cs` | Loads the embedded JSON and builds the resource dictionary: brushes, text styles, radii, drop-shadow effects, inset-shadow parts. |
| `src/AudioFool/Theming/TokenExtensions.cs` | `{theme:Token key}` and `{theme:Thickness ...}` markup extensions. |
| `tests/AudioFool.Core.Tests/ThemeTokensTests.cs` | 27 tests: colour and shadow parsing, the path walk, reference checks, the shipped file. |
| `tools/themelab/TokenSheet.cs` | `--window tokens`. |

## New source files added in session 34

| File | Purpose |
|---|---|
| `src/AudioFool.Core/Analysis/Fft.cs` | Radix-2 complex FFT of one fixed size. |
| `src/AudioFool.Core/Analysis/SpectrumAccumulator.cs` | Samples in, `SpectrumAnalysis` out: mean and peak spectra, the 800 × 512 picture, the peak sample, the 24-bit bit count. Pure. |
| `src/AudioFool.Core/Analysis/TrackAnalyzer.cs` | Opens a decode-only BASS stream (DSD at 176.4 kHz) and feeds the accumulator, with progress and cancellation. `AnalysisException` for a file it can't read. |
| `src/AudioFool.Core/Analysis/Waveform.cs` | The seekbar waveform: `Waveform.Read` decodes a file on its own stream into 600 RMS columns (`WaveformLevels`), null if it can't. |
| `src/AudioFool/Theming/WaveformView.cs` | Draws those levels in `theme.slider`, played part in the slider's Foreground; `SeekWaveform.Levels` switches the groove for it. |
| `src/AudioFool.Core/Analysis/QualityOpinion.cs` | `QualityClaim` (what the file says), the cliff and image-notch detectors, and the `Opinion`: verdict, headline, findings worst first, cutoff. Pure. |
| `src/AudioFool/Services/SpectrogramImage.cs` | Paints the picture with the heat-map tokens; the legend gradient. |
| `src/AudioFool/ViewModels/AnalysisViewModel.cs` | Runs the analysis in the background; the headline, explanations and footer figures. |
| `src/AudioFool/AnalysisWindow.xaml[.cs]` | The non-modal window: picture, axes, legend, cutoff marker, opinion. |
| `tests/AudioFool.Core.Tests/SpectrumAnalysisTests.cs` | 19 tests on synthetic audio: the FFT, 0 dB calibration, power averaging, the picture's rows, short and silent tracks, bit counting and its share, restarts between slices, where slices go. |
| `tests/AudioFool.Core.Tests/QualityOpinionTests.cs` | 36 tests on synthetic spectra, one per rule and flag, plus the claim and kHz wording. |
| `src/AudioFool.Core/Analysis/QualityCache.cs` | (part 2) The saved check results, `quality.json`, keyed by file name + size + write time. |
| `src/AudioFool.Core/Analysis/QualityScanner.cs` | (part 2) The library check: sampled reads on background threads, progress, periodic saves, stop and resume. |
| `src/AudioFool.Core/Analysis/QualityStatistics.cs` | (part 2) The QUALITY CHECK rows and their filters. |
| `src/AudioFool/ViewModels/QualitySectionViewModel.cs` | (part 2) The live Statistics section: rows, summary, Check / Stop button. |
| `tests/AudioFool.Core.Tests/QualityCheckTests.cs` | (part 2) 6 tests: the cache's key and round trip, the scanner with a fake analyser (skip, missing, unreadable, cancel), the rows and filters. |

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

### The theme (one, since session 18)
PS1 is the only theme. The Dark and Vista themes, the Themes menu, `ThemeItem`,
`AppSettings.Theme` and runtime switching were removed in session 18 at the user's
request, so that no session spends time keeping them working. A leftover `"Theme"`
key in `settings.json` is ignored on load and dropped on the next save.

Since session 31 it is the **new** PS1 theme alone, specified in `design/` and built
from `design/theme-tokens.json`. **`design/progress.md` is the reference** for how
screens use it (the token-usage table) and for every decision.

`ThemeService.Apply()` runs once in `App.OnStartup`, before the window is built. It
merges the token resources (`Theming/TokenResources.cs`, from the embedded JSON) and
then `Theming/Chrome.xaml` (the `theme.*` components, plus implicit `ToolTip`,
`ContextMenu` and `MenuItem` styles), then sets WPF-UI's accent. `App.xaml` starts on
WPF-UI's **Light** base and holds nothing themeable, and every window declares
`WindowBackdropType="None"` in its XAML.

**The accent** is `color.control.iconNeutral`, the theme's dark grey. Nothing visible
reads it (every button, check box and text box has a template of its own); it is set
so a stock state can't pick up the Windows accent.
- **`ApplicationAccentColorManager.ApplySystemAccent()` silently does nothing here.**
  It resolves the theme through `ApplicationThemeManager`, which this app never
  drives. Use the explicit `Apply(color, ApplicationTheme.Light)` overload.
- It writes its brushes straight into `Application.Resources`, which outranks every
  merged dictionary. The old theme copied its own values back over them; nothing needs
  to now.

**WPF-UI's own keys are not overridden.** The old `Ps1Theme.xaml` restated about 160 of
them; since session 31 they are WPF-UI's Light values, and a ThemeLab brush dump
(`--brushdump 1`) showed none reaching anything visible. Where a stock control still
shows (the filter chip is a `ui:Button`; menu items use WPF-UI's templates), its
colours are set from tokens on the control or in its style's own resources, as the
`MenuItem` styles in `Chrome.xaml` do. A new stock control needs the same treatment.

Still load-bearing from the old design system:

- **Use `DynamicResource` for styles, templates and brushes in windows.** The theme
  is merged before any window is built, so `StaticResource` would probably work too,
  but it has not been tested.
- **`WindowBackdropType.None`** on every window, and a dialog paints `color.window.bg`
  on its root (see *A dialog has no shell* under Gotchas).
- **Letter spacing** is `Formatting/LetterSpacing.cs` (`LetterSpacing.Em`). WPF has no
  tracking property, so spacer elements are interleaved into the text and the
  unspaced original is kept as the automation name. Only for short labels whose text
  never changes.
- **Don't key an optional style to `x:Null`** and set `Style="{DynamicResource X}"`
  hoping it means "leave it unset": it drops WPF-UI's implicit style and the control
  falls back to the *Aero* theme style (a white grid header, a near-white selected
  row). Give the control a real style.

## The old design system (sessions 7–30, removed in session 31)

From session 7 every value lived in `Themes/DesignTokens.xaml` (`Af*` tokens),
`Themes/Components.xaml` (components and "themeable slots": `ControlTemplate`s on a
plain `Control`), `Themes/Ps1Theme.xaml` (the grey-console PS1 of sessions 17–18:
`Af*` overrides, about 160 WPF-UI key overrides, implicit `DataGridColumnHeader`,
`DataGridCell`, `ProgressBar` and `ToggleButton` styles) and `Themes/Ps1Motion.xaml`
(the row hover fade, merged only when Windows animations were on). The session notes
above that name `Af*` keys describe that system; they are history. The files are in git
history before session 31.

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

### Column auto-fit (session 5; removed in session 27)

**Removed at the user's request in session 27.** Columns now start at the spec widths from
`songTable.columns` and are never refitted; see `design/progress.md`. The notes below are
history.

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

### Title bar layout (session 4, reworked in sessions 8 and 26)
**Since session 26** the positions are calculated, not fixed (spec §2): `MainWindow.AlignTitleBar`
centres the 40 × 36 logo slot over the ARTISTS label and sizes the search box to end at
the Albums panel's right edge, rerunning when either panel resizes. `FitLogoMenuToSlot`
sizes the menu item's spacer so the whole item is the slot's width. The window buttons are
WPF-UI's `TitleBarButton`s with a new template (`theme.windowButton` in
`Theming/Chrome.xaml`), *not* replacements, so the maximize button's non-client hit test,
which opens Windows 11 snap layouts, is untouched. The 406 px figure below is history.

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

**Three checks from session 17 for any theme change.** The scripts lived in that
session's scratchpad, which is gone, so rebuild them. Each is a few lines of
`System.Drawing` using `LockBits` and `Marshal.Copy` (`GetPixel` is too slow for a
whole window):

- **Unpainted gaps.** Render with `--bg "#FF00FF"` and count magenta pixels. It
  should be 0. This found the dialogs' missing shell, which the default dark
  backdrop hid.
- **Accent share.** Classify each pixel: neutral if max−min channel ≤ 24, an
  accent if the spread is > 60, then assign it to the nearest token colour.
  Exclude the logo (x < 72, y < 48). PS1 ended at 99.6% neutral and 0.37% accent.
- **"Nothing else moved" is a pixel diff, not a glance.** For a refactor or a
  change meant to touch one element, render a set of cases before the change and
  again after, and count differing pixels. Unchanged cases should be 0. Session 18
  used main, `--paused 1`, `--scanning 0.4`, `--focusrow AlbumList`,
  `--window tags`, `--window stats`, `--window lastfm`, `--lastfm failing`,
  `--artmenu 1` and `--menushot`, all on `--bg "#FF00FF"`. To render the previous
  commit rather than the working tree, build it in a scratch worktree
  (`git worktree add --detach <scratch>\base HEAD`, build its `tools/themelab`,
  then `git worktree remove --force`).

When writing these in PowerShell, see *Two PowerShell traps in pixel scripts*
under Gotchas.

**Verify what screenshots cannot show** with a small headless console app referencing
`AudioFool.Core`. Several are left in the session scratchpad. That approach caught things
no screenshot would: that DoP marker bytes were well-formed *before* any audio reached
the DAC, and that an advancing position proves the WASAPI callback is actually running.

---


---

## Apply the theme *before* building the window

`App.OnStartup` calls `ThemeService.Apply()` before
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

There is no switching at runtime since session 18, so this is the only time the theme
is applied.

## ThemeLab: reviewing a theme without touching the desktop

`tools/themelab` (session 7; it started in a session scratchpad and has been tracked in
the repo since, outside the solution) renders the real windows off-screen to a PNG: the
main window, and through `--window` the tag, Statistics, Last.fm, art search and art
viewer dialogs. It references `AudioFool.csproj`, so it renders the actual windows
with the actual theme dictionaries — no mock, and no window on the user's desktop. It
also has a `--dump` mode that prints what a list of theme resource keys resolves to,
which is how the token defaults were baked from WPF-UI's real values instead of guessed.

```bash
ThemeLab.exe --out shot.png [--w 1560 --h 900 --scale 2]
ThemeLab.exe --focus ArtistList     # keyboard focus visuals do render
ThemeLab.exe --focusrow AlbumList  # focus the selected row (the ring a click leaves)
ThemeLab.exe --window tags          # the tag dialog
ThemeLab.exe --dump keys.txt
ThemeLab.exe --dump out.txt --dumpkeys keys.txt   # resolve the keys listed in keys.txt, one per line
ThemeLab.exe --brushdump 1 --out shot.png   # also writes shot.brushes.txt: every brush and corner radius in the tree, hover/press ones included
ThemeLab.exe --menu               # does the logo open the menu?
ThemeLab.exe --menu --menutree   # sizes and clips under the menu item
ThemeLab.exe --window stats --w 900 # statistics, from the real library.json
ThemeLab.exe --window click --click Year        # click a Statistics row, render the result
ThemeLab.exe --window click --click Rush --search Buckethead
ThemeLab.exe --window click --click Year --fix 1964   # simulate fixing the selected album
ThemeLab.exe --window tags --album "Saturn Return" --w 460   # album dialog on a real album
ThemeLab.exe --window tags --track "Polygon Weather"         # track dialog on a real track
ThemeLab.exe --window tags --album "Saturn Return" --set "!Comment;Genre=Ambient"
ThemeLab.exe --window tags --album "The Inevitable End" --set "^Zeros"   # press Remove Leading Zeros
ThemeLab.exe --window artsearch --artist "Rush" --album "Moving Pictures" --use 1 --w 820 --h 640
ThemeLab.exe --artmenu 1          # the album header art's context menu
ThemeLab.exe --window tags --album "Goodbye Yellow Brick Road" --pick 1-8 --set "DiscCount=2"  # a grid selection
ThemeLab.exe --rows 1,2,3,4       # several selected grid rows
ThemeLab.exe --window edit --w 1300 --h 600   # in-place grid edits
ThemeLab.exe --window queue       # edit queued and playing tracks while the real engine plays silently
ThemeLab.exe --window queue --long "D:/Music/x/a.flac;D:/Music/y/b.dsf" [--longcomment 3000000]   # also save to real tracks (copied to scratch) while they play
ThemeLab.exe --window device --long "D:/Music/x/a.flac;D:/Music/x/b.flac"   # a change of default output device, simulated: playing, paused, stopped, next track
ThemeLab.exe --window lastfm --state connected --w 480    # setup|waiting|connected|failing|rejected
ThemeLab.exe --w 1300 --h 700 --lastfm failing   # the status-bar indicator
ThemeLab.exe --menushot menu.png --scale 2      # the logo menu's drop-down, without opening it
ThemeLab.exe --bg "#FF00FF"                     # magenta behind the window: any unpainted gap shows
ThemeLab.exe --output "Exclusive · 176.4 kHz · DSD over PCM"   # the output readout under the volume slider
ThemeLab.exe --libshot libs.png --scale 3   # the Libraries submenu, one folder ticked, one not
ThemeLab.exe --window folders               # folder list: merging, relocation, unticked status, Remove
ThemeLab.exe --window tokens --out tokens.png   # the central theme: lookups from MainWindow, XAML usage, fonts, specimen
ThemeLab.exe --dpi 1.25 --w 1720 --h 1080   # laid out and drawn as on a 125% monitor (all monitors here are 100%)
ThemeLab.exe --focus ShuffleButton --focusvisual 1   # draw the keyboard focus outline, which focus alone doesn't
ThemeLab.exe --peers 1                      # the automation tree: what a screen reader reads for each control
ThemeLab.exe --tabwalk 18                   # 18 real Tab presses from the search box; prints where focus lands
ThemeLab.exe --tabwalk 0 --keys "Tab,Tab,Tab,Tab,Down" --tabfrom SearchBox   # any key sequence
ThemeLab.exe --logohover 1                  # logo item highlighted; prints its tooltip and its template's background triggers
ThemeLab.exe --window seek --file "D:\Music\...\long.flac"   # click the seek bar's track mid-play (silent); prints the position trace, "no bounce" or "BOUNCED"
ThemeLab.exe --window waveform --file "D:\Music\...\x.flac" --at 0.4 [--noglide 1]   # seekbar waveform: read time, 2 s glide trace, window + 3x seekbar render (plays silently)
```

**Seek-bar clicks seek on mouse-down** (2026-10-01). The bar is two-way bound to the
250 ms position timer, and a click on the track (click-to-point) jumps the value without
a drag, so `IsSeeking` never parks the timer. The seek used to wait for mouse-up, and a
tick landing while the button was down put the old position back: the handle went
there, back, and there again. `SeekBar_PreviewMouseLeftButtonDown` (attached with
`handledEventsToo`, since the slider handles the press) now commits at once. A drag
still seeks on `DragCompleted`. A headless probe showed the engine itself reports the
new position within 1 ms of `Seek`. `--window seek` parks the window to the right of
every monitor so the real pointer maps to 0:00.

**Keyboard seeking works too** (same day). The arrow, Page and Home/End keys moved the
value with nothing to commit it, so the next tick undid them. `SeekBar_KeyDown` (also
`handledEventsToo`) commits each press. The steps were WPF's defaults, 0.1 s and 1 s;
they are now **5 s for the arrows and 30 s for Page Up/Down** (the user's choice),
as `SmallChange` / `LargeChange` on `SeekBar`. `--window seek` also presses Right,
Page Up and Left through `InputManager` and checks each step lands and holds; with
the handler switched off, all three bounce.

**Seeks no longer click** (same day; the user heard it with the arrow keys). A seek joins
two unrelated points of the waveform, and with `MixerChanNoRampin` (kept for gapless)
nothing smooths the step. `AudioEngine.Seek` now hands the seek to
`OutputChain.SeekBetweenBlocks`; the WASAPI callback (`OutputChain.Fill`, an instance
method now) fades the tail of the block it just pulled out over 5 ms, applies the seek,
and fades the next block's head in. DoP streams are never faded (it would break the
markers), so DSD passthrough seeks still click. Until the next pull applies it,
`Position` reports the pending target, so the seek bar can't bounce in between. A paused
device applies it on Resume. In exclusive mode the fade alters those 10 ms, which is not
bit-perfect for that moment.
- **Measured** with a scratch probe that decodes real FLACs through a mixer built like
  `OutputChain`'s and pulls 10 ms blocks, as the device does. Click size is the largest
  second difference within 1 ms of the seek, against the track's 99.9th percentile:
  quiet orchestral tracks (*2001* soundtrack) showed **7–51x** before, **0.0–0.1x** with
  the real `Fill` (called by reflection, no device). BASSmix's own ramp-in only got it
  to 2–22x. A noisy live recording hid the click (under 1x either way), so test on
  quiet material.
- Also checked: positions read the target at 0 ms and count on; a paused seek holds at
  the target and resumes from it; `--window seek` and `--window queue` (gapless) pass.

**Volume and Mute work now** (same day; the user found the slider and Mute did nothing).
They never had: `OutputChain.SetVolume` set BASS's `ChannelAttribute.Volume` on the
mixer, and BASS ignores that attribute when a decode channel is read directly, which is
all this mixer is. Measured: identical output at 1, 0.5, 0.1 and 0. Code unchanged since
the initial commit. `Fill` now applies the gain itself, gliding from the last block's
gain to the new one across a block so a drag or Mute doesn't click; exclusive mode stays
at unity. Checked against a second chain at full volume on the same audio: output is
exactly 0.500 / 0.100 / 0.000 / 0.250 / 0.800 of full, and no sample step exceeds the
full copy's by more than 0.0002. Volume changes take effect after the 0.2 s device
buffer, since that audio is already pulled.
- **So any earlier probe or ThemeLab mode that relied on volume 0 for silence** (for
  example `--window queue` and `--window clicks`) actually played at full level. From
  now on, engine volume 0 really is silent. Session 13's muted-session technique
  (`BassWasapi.SetMute`) was silent either way.

**Press on a slider's track and drag** (same day; the user couldn't drag the volume
slider). WPF's `IsMoveToPointEnabled` jumps the value to a press on the track but starts
no drag, so only a press on the handle itself (12 px on the volume slider) could drag.
`Theming/SliderDrag.cs` (`theme:SliderDrag.FromTrack`, set in `theme.slider`, so both
sliders) hands such a press to the handle after the jump, and its drag carries on from
there. On the seek bar that press is now a drag too, so `IsSeeking` parks the timer until
the button comes up and the seek lands on release; the mouse-down commit is a fallback.
ThemeLab **`--hitthumb SeekBar,VolumeSlider`** hit-tests each handle, drags it through
`DragDelta` events, and presses the track to check a drag starts. Raise that press on the
**Slider**: raised on its `Track`, the slider's own class handler never ran.

`--tabwalk` feeds keys through `InputManager`, as the keyboard does. Raising `KeyDown`
on an element (as `--window edit` does) skips WPF's Tab handling entirely, and
`MoveFocus` skips the controls' own key handlers, so neither shows the real order.

`--dpi` is the real scaling test; `--scale` only enlarges the bitmap. It draws the
window straight into the bitmap, because through the usual VisualBrush every vertical
edge softens at an emulated DPI, which looks exactly like a scaling bug in the app
(session 5 reported one, wrongly). `--focusvisual` sets WPF's internal "always show
focus visual" flag by reflection: WPF otherwise draws a FocusVisualStyle only after
real keyboard input, so `--focus` alone renders no outline at all.

`--window folders` runs against the real drives and whatever `library.json` the shell
sees, so it re-reads tags when file times differ (6 minutes off the SD card). It
backs up and restores settings and the cache.

`--menushot` renders the `Popup.Child` of the logo `MenuItem` directly. Opening
the real popup is avoided because popups are clamped onto a monitor and could
flash on the desktop.

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
  real startup path runs: it loads the user's settings, applies the theme a second
  time, and builds and shows a second `MainWindow` on the desktop. The
  harness has its own `LabApp.xaml` that merges the same dictionaries and declares the
  same converters, and nothing else. (Subclassing `App` to override `OnStartup` does
  not work — the generated `InitializeComponent` rejects a derived type.)
- **Relative pack URIs resolve against the entry assembly**, so `App.xaml` and
  `ThemeService` use the assembly-qualified form
  (`pack://application:,,,/AudioFool;component/Themes/...`). The harness also mirrors
  `Resources/logo.png` and `AudioFool.ico`, which `MainWindow.xaml` loads by relative URI.
- **The harness paints `--bg` behind the window first** (dark grey by default).
  PS1 is opaque, so none of it should show; that is what makes `--bg "#FF00FF"` a
  gap detector. `--theme` and `--switch` went with the other themes in session 18;
  `--theme` is now ignored if passed.

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
  `DisplayIndex` on every column. Since session 27 there is no separate now-playing
  column (the triangle is in the # cell), so # is DisplayIndex 0.
- **A DataGrid's `CellStyle` set from its style loses to an implicit `DataGridCell` style**
  (the old `Ps1Theme.xaml` had one, with 8 px padding). Found in session 27 by
  measuring: text sat 2 px off the header. Set `CellStyle` on each column instead, as
  the song table still does.
- **DataGrid fits star columns to the viewport minus `CellsPanelHorizontalOffset`**, and
  that offset is where the cells sit relative to the *grid*, including any margin on the
  `ScrollContentPresenter`. A symmetric list padding is therefore subtracted twice, and
  the columns come up that much short of the right edge. `theme.songTable` insets the
  list area asymmetrically to compensate (see the comment there).
- **Synthetic mouse events don't select DataGrid rows.** A raised `MouseLeftButtonDown`
  on a cell's text reaches the grid's own handlers but leaves the selection unchanged,
  on HEAD as much as now. A raised `MouseDoubleClick` (source = the cell text) does run
  the play handler.
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
  stale settings, and a write goes to the copy, not the app. To check a setting, ask
  the user or verify in the running app.
- **`library.json` is now in the container too** (found in session 22). Earlier
  sessions read it current from the shell, but once a shell process writes it (a
  ThemeLab mode "restoring it byte for byte" on 29 September did), the shell gets
  its own copy at
  `%LOCALAPPDATA%\Packages\Claude_pzs8sxrjxfjjc\LocalCache\Local\AudioFool\` and
  never sees the app's again. The same goes for `settings.json` under
  `LocalCache\Roaming`. So neither the real cache nor the real settings can be
  read from here. The running app's state has to come from UI Automation (status
  text, window title) or the user. Files on `D:\` / `E:\` are not virtualised.
- **The same applies to AudioFool launched from the shell.** It inherits the
  container and runs on the stale settings: wrong theme and folders, no Last.fm
  session. A status-bar indicator that "wasn't there" in session 15 was this, not
  a bug. It is also why a UIA check of a shell-launched app says nothing about
  the user's settings.
- **TagLib opens a file for writing unshared** (`TagLib.File.Create(path)`), so
  it fails against any other open handle, including playback's. Save through
  `TagWriter`, which uses its shared `SharedFile` abstraction. Never call
  `TagLib.File.Create(path)` directly for a write.
- **TagLib writes track numbers zero-padded** ("01", "01/12") with no option to
  turn it off. Set numbers through `TagWriter.WriteNumbers`, never `tag.Track`
  directly. See *Leading zeros* under session 16.
- **WPF-UI's implicit styles survive `OverridesDefaultStyle="True"`.** That flag
  only skips the *theme* style; an implicit app-level style (WPF-UI's
  `ControlsDictionary` has one for `RepeatButton`) still applies. It collapsed the
  PS1 slider's travelled segment to 0 px, unnoticed since session 7. Add
  `Style="{x:Null}"` as well when a template part must be bare.
- **A dialog has no shell unless it paints one.** With `WindowBackdropType.None`,
  a `FluentWindow` is transparent behind its content. Every window paints
  `color.window.bg` on its root grid (the main window on a border behind the rest). Render
  with `--bg "#FF00FF"` to catch a gap: the default dark backdrop hides it.
- **Two PowerShell traps in pixel scripts.** Variable names are case-insensitive,
  so `$B` (a blue byte) overwrites `$b` (the bitmap). And `diff` is a built-in alias
  for `Compare-Object`, which wins over a function named `Diff`. Both fail quietly,
  with plausible but wrong output.
- **WPF-UI's `ListBox` template ignores `Padding`.** The list padding (spec §6.2) is a
  `Margin` on the ListBox for that reason. Found in session 26 by measuring: rows sat
  6 px too far left.
- **A TextBox applies its `Padding` inside the text host as well.** A custom template
  that also insets its content by `{TemplateBinding Padding}` indents the text twice.
  `theme.searchBox` sets `Padding` to 0 and puts the token in the template. WPF also
  adds a 2 px caret gutter before the text, which the template cancels with a −2 margin
  so typed text and placeholder line up (measured: both at x 131).
- **Replacing a `TitleBarButton`'s template drops its `CommandParameter`.** WPF-UI sets
  it in the template's own triggers, and it is the only thing telling the shared command
  which button was pressed. `theme.windowButton` restates it per `ButtonType`.
- **A ProgressBar's (any RangeBase's) `Value` binds two-way by default.** Bound to a
  read-only property it throws at load and the app won't start (session 34: the status
  bar's `ProgressFraction`; the user hit it). Add `Mode=OneWay`.
- **"The process is alive" is not a smoke test.** An unhandled-error dialog keeps the
  process running and responding. After installing, find the main window by process id
  with UI Automation and confirm `SearchBox` is in it (the snippet is in session 34).
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

*Done in session 17:* the grey-console PS1 theme (commit `eea12aa`), revised
twice from the user's feedback.

*Done in session 18:* PS1 lost its selection bar, pane marks and row focus ring,
and became the only theme.

*Done in session 19:* queued tracks show their new tags when they come up; the
playing and next tracks can be saved (resizing saves to them are refused); a
retag of the playing track reaches the scrobbler.

0. **PS1 is open to more critique.** The user works in a screenshot loop: they
   look, then give precise notes. Lately each note removes decoration. Things to
   know going in:
   - **Suggested and not picked**: filled transport glyphs, and coloured
     shuffle/repeat when on. Don't add them unasked.
   - **Watch the green.** At the darker greys it is 1.0–1.4:1, so the seek fill
     and the ▶ read by hue alone. If the user finds them faint, a thicker keyline
     or a bolder mark is the fix.
0. ~~**Ask the user how in-place editing feels in the running app**~~ Asked
   2026-10-06: the user says it works great.
0. ~~**A new cover on the playing album skips the playing and next tracks**~~ Done in
   session 37: the engine lets go of the file for the save and reopens it at its
   position.
1. A visible, editable queue view — now the most conspicuous missing player feature.
2. ~~Library-wide tag stripping~~, ~~recovering dates lost to pre-session-12 saves
   from MusicBrainz~~, ~~resuming playback on launch~~, ~~a Last.fm Love button~~ and
   ~~TAK/DTS playback~~: **dropped by the user (2026-10-06). Don't build or suggest them.**
4. ~~**Show the full date in the album header?**~~ Done in session 33. The Albums list
   subtitle still shows only the year.
4c. ~~**The flaky test**~~ Found and fixed in session 37 (a `QualityScanner` race and a
   test that looked at all of %TEMP%).
5. MilkDrop 3 / projectM visualisation. Scoped out in session 6 (LGPL-2.1, C API,
   `GLWpfControl` for OpenGL-in-WPF, no prebuilt `libprojectM.dll` — source only).
   Proposed next step: spike build of `libprojectM.dll`. No implementation started.
6. Profile the post-scan memory.
8. Code signing would remove the SmartScreen warning on first launch, but is rarely worth
   the cost for a personal build.
