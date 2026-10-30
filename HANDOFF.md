# AudioFool — session handoff

Updated 2026-10-30, session 73. Read this alongside `README.md`: the README covers
*how the app works*, this covers *where things stand and how to work on it*. What each
past session changed, and why, is in **`HISTORY.md`** (oldest first); look there when
you need the story behind a piece of code, not at the start of every session.

---

## Where everything lives

| What | Path |
|---|---|
| Source | `C:\MusicPlayer` (solution is `AudioFool.slnx` — the new XML format, not `.sln`) |
| **Installed app** | `%LOCALAPPDATA%\Programs\AudioFool\` ← what the shortcuts launch |
| Publish output | `C:\MusicPlayer\publish\` (a build artifact, *not* what runs) |
| Settings | `%APPDATA%\AudioFool\settings.json` |
| Scan cache | `%LOCALAPPDATA%\AudioFool\library.json` (~13 MB) |
| Saved cover thumbnails | `%LOCALAPPDATA%\AudioFool\art\` (~11 MB, disposable; session 47) |
| Playback log | `%LOCALAPPDATA%\AudioFool\playback.log` (stall diagnostics, rolls at 4 MB; session 48; the first stutter reads are under sessions 48–49 in `HISTORY.md`). Readable from Claude's shell as long as the shell never writes it |
| Native BASS DLLs | `C:\MusicPlayer\lib\bass\x64\` (13 of them) |
| Music library | `D:\Music` — external 4 TB SSD "Marc SSD", ~26,000 tracks, 692 GB |
| Session history | `C:\MusicPlayer\HISTORY.md` (what each session changed, oldest first) |
| Theme spec and decisions | `C:\MusicPlayer\design\` (`theme-spec.md`, `theme-tokens.json`, `progress.md`); PS2 Dark's in `design\ps2-dark\` (spec of differences, `theme-tokens.dark.json`, `screenshots\` of both themes) |

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

Three shell notes:

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
with 0 warnings and passes the whole suite (first checked in session 5; last checked in
session 47, 624 tests). If you add a dependency that lives outside NuGet, re-run that check — it is
the only thing that catches a file you forgot to track.

### Releases

All on GitHub (private, so downloads need a signed-in account); `gh release list` has
them all. **Latest: v0.9.0** (2026-10-28, session 72, tag on `4762fbd`, 60.7 MB zip,
19 files). Every one after v0.1.0 was made by Claude.
`AudioFool.csproj` starts with a byte-order mark: keep it when bumping the version
(v0.6.0's bump dropped it and needed a follow-up commit). Handle a release end to end when asked, but
**confirm the version number and the notes with the user before `gh release create`**:
it publishes a tag. Middle number for new features, last for fixes only.

1. Commit and push anything outstanding, and run the tests.
2. Bump `<Version>` in `src/AudioFool/AudioFool.csproj` (the only place it lives),
   commit ("Version x.y.z") and push, so the tag lands on that commit.
3. Fresh `dotnet publish` (the command above), then delete `publish\*.pdb`. Check
   `(Get-Item publish\AudioFool.exe).VersionInfo.ProductVersion` shows the new version
   (About's version line reads it, so it follows by itself).
4. Compare the file list with the last release (`gh release download v<old> --dir
   <temp>`, unzip, `Compare-Object` the names): **19 files** (the exe, 13 BASS DLLs,
   5 WPF native DLLs). A missing loose DLL breaks the app on another machine.
5. `Compress-Archive publish\* $env:TEMP\AudioFool-<ver>-win-x64.zip` (never inside
   the repo). About 60 MB.
6. Notes in a file (dodges quoting): `gh release create v<ver> <zip> --target main
   --title "AudioFool <ver>" --notes-file <file>`, then `git fetch --tags`.
7. Check the tag with `git rev-list -n 1 v<ver>`. **Not `git rev-parse v<ver>^{commit}`**:
   PowerShell mangles the `^{}` and the command fails.
8. Install the same build over the installed copy (close the app first; it stops
   playback, so say so).

The notes are written for the user, grouped New / Improved / Fixed, from
`git log v<old>..HEAD` with the Docs and ThemeLab commits left out.

Write commit messages to a file and use `git commit -F <file>`: in Windows
PowerShell 5.1 a double quote inside a here-string message breaks the argument
and the commit fails.

---

## State at handoff

### Where things stand

- **Everything in `README.md` is implemented and verified.** **988 tests** pass.
- **Last release: v0.9.0** (2026-10-28), installed; nothing unreleased.
- Since session 63 an install is checked by hashing the files, not by launching
  AudioFool from Claude's shell (see *A launch from Claude's shell writes
  playback.log* under Gotchas).
- **Nothing is half-done.** Open items are under *Suggested next steps* at the end.

Recent sessions, newest first (the full notes, file names and checks are in
`HISTORY.md`):

- **73** (2026-10-30): docs clean-up only. This file slimmed (past sessions' details
  live in `HISTORY.md`, which gained sessions 50, 54 and 71); the finished PS2 Dark
  build prompts and CLAUDE.md addition deleted. No app change.
- **72** (2026-10-27): **the end of a queue goes back to its first song**: the bar
  shows it at 0:00 with its cover, stopped; Play starts the queue from it. Confirmed
  by the user. **v0.9.0** released.
- **71** (2026-10-23): cut-off covers fixed on the real library (Night Verses and 311
  re-covered by the user); **a bare × after the status text** clears a Statistics
  filter or dismisses a one-off message; **session 60's seven remaining findings**
  (dialogs on the main window's monitor, failed plays say why, Ctrl+J selects the song,
  discs' clearances apart, Analyze on tiny files, library.json and scrobbles.json saved
  safely, the Last.fm secret encrypted). **v0.8.1** released (with session 70).
- **70** (2026-10-22): **cut-off covers**: a whole cover is shown and kept over a
  cut-off one, a cut-off one is never saved, Statistics' CUT-OFF COVERS finds them.
- **69** (2026-10-22): **the tag-save fixes** (confirmed). **v0.8.0** released.
- **68** (2026-10-22): **drives noticed while AudioFool runs**; a crash on a press on
  a Songs header letter; a single click on the status line opens Statistics; the
  now-playing format line ("FLAC · 1,364 kbps · 24 bit · 44.1 kHz").
- **67** (2026-10-22): Find and Replace on a selection; the review's small UI fixes;
  playlists and clearances saved safely; the playback fixes (skip unopenable songs,
  let go of the DAC on Stop, reopen where heard, no DoP to shared).
- **66** (2026-10-22): the close X caught in the act; `CloseButtonSafetyNet` and
  hit-test lines in the click log.

Standing preferences from the user that the code doesn't show:

- **No tooltips or hint text** (2026-10-12). Keep accessible names.
- **Grey is primary; colour only in its spec role** (`design/theme-spec.md`).
- **Explain plainly.** Findings first in everyday words, with an analogy where it
  helps; the technical names second.
- **Don't explain commit and push** (the user's call, 2026-10-21): they know it now.
  Report the result in a line. Commit and push only when asked.
- **Ask before closing AudioFool to install:** it stops whatever is playing. When the
  app isn't running, install without asking.
- **Ask before posting messages to the user's running AudioFool** (a live test in
  session 66 was blocked by Claude's safety check).
- The features under *Dropped* in the next steps are dropped for good: don't build or
  suggest them.

### Deliberately not done

- **No TAK or DTS decoder.** un4seen publishes neither. Needs a third-party build or a
  libVLC fallback; `AudioEngine.CreateDecodeStream` is the single seam for that.
- No queue view (playlists came in session 38). Shuffle and repeat are done (session 5); what is still
  missing is a visible, editable queue.
- Memory was profiled in session 43 (`HISTORY.md`). Still left: FLAC tag reads load the cover
  (TagLib ignores PictureLazy for FLAC), so a big scan still peaks high before the reclaim.

### If audio pops are reported again

Sessions 55–57 traced the user's gaps to the work laptop, not AudioFool: first the
NVIDIA GPU waking (fixed by driver 597.16), then **Dell TechHub** (its snapshots of C:
and their cleanup), which the user disabled. Closed; don't raise it. If pops come back:

- **Reading `playback.log`:** a full reserve with a 40–50 ms LATE FILL (or nothing at
  all) while the user hears a pop = a laptop-wide hiccup downstream of the app. A
  ~100–130 ms APP STARVED + LATE FILL with 0 ms GC = the old GPU wake. GC PAUSE = a
  full GC froze the app. SLOW FILL with little "busy" = waiting on the file. Read the
  Windows event logs for that minute too (VSS 8231 in Application, Volsnap 33 in
  System): LatencyMon has no timestamps and misses firmware stalls.
- Check TechHub is still off: `Get-CimInstance Win32_Service -Filter
  "Name='DellTechHub'"` (StartMode Disabled), `Get-Process Dell.TechHub*` (empty).
- Output is a **Topping DX5 on Topping's own driver** (Thesycon-based); its control
  panel's buffer size is ASIO only, so nothing there applies to WASAPI.

---

## Architecture notes

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

### The themes (two since session 52, switched live)
**PS1 Light** (`design/theme-tokens.json`, spec `design/theme-spec.md`) and **PS2 Dark**
(`design/ps2-dark/theme-tokens.dark.json`; `design/ps2-dark/theme-spec.md` holds only
what differs). Same keys and sizes in both files; only `color.*` and `shadow.*` differ,
and the parity tests in `ThemeTokensTests` hold them to that. **`design/progress.md` is
the reference** for how screens use the tokens (the token-usage table), for every
decision, and for where the PS2 Dark build sessions stand (one of three left).
(Session 18 removed an earlier Dark and Vista; nothing of them was brought back.)

- **The setting:** `AppSettings.Theme`, `Light` or `Dark` by name, **Dark when
  missing**. Read by `ThemeSettingConverter`, which never throws: anything else (the
  pre-session-18 "Vista" or "PS1", a number, null) reads as Dark and the rest of the
  file still loads. A strict reader would make `Load` fall back to defaults.
- **Startup:** `ThemeService.Apply(settings.Theme)` in `App.OnStartup`, before the
  window is built (see *Apply the theme before building the window*). It swaps WPF-UI's
  base dictionary to Dark if needed (`App.xaml` merges Light), merges the token
  resources (`Theming/TokenResources.cs`, the embedded JSON for that theme) and then
  `Theming/Chrome.xaml` (the `theme.*` components, plus implicit `ToolTip`,
  `ContextMenu` and `MenuItem` styles), sets WPF-UI's accent and the window borders.
  `App.xaml` holds nothing themeable, and every window declares
  `WindowBackdropType="None"` in its XAML.
- **A switch:** the status bar's Theme button and Ctrl+T run
  `MainViewModel.ToggleThemeCommand` → `ThemeService.Switch(theme)`, then save the
  setting. `Switch` builds the other token set and puts it where the first one was in
  `Application.Resources`, swaps WPF-UI's base, re-applies the accent, refreshes
  `{theme:LiveColor}`, tells the window borders, and raises `ThemeService.Switched`
  (the view model relabels the button from it). Only resources change, so playback,
  selections, scroll positions and open dialogs stay put. About 80–110 ms in ThemeLab,
  layout and redraw included.
- **What makes every colour follow:** `DynamicResource` everywhere (178 `color.*` and
  13 `shadow.*` references); the `type.*` styles' text colour is a `DynamicResource`
  too, so the 17 styles `BasedOn` them through `StaticResource` follow;
  `{theme:LiveColor color.x}` for the 11 brushes inside styles' own resources (WPF-UI
  keys for menus and title bars), which `DynamicResource` can't reach; code looks
  colours up as it draws (MoldedFace also holds a reference to its first inset brush, so
  a switch always redraws it). `{theme:Token}` reads once, so it refuses colours,
  effects and styles.
- **Popups:** a context menu or tooltip isn't in its window's tree, so a switch skips it
  while closed, and WPF refreshes it as it opens (`Popup` re-registers with its placement
  target and invalidates its subtree on every open; checked in WPF's source).
- **The window border:** `Theming/WindowFrames.cs` sets `DWMWA_USE_IMMERSIVE_DARK_MODE`
  on each window in Dark (cleared in Light): open ones at a switch, new ones at their
  first `SizeChanged`.

**The accent** is `color.control.iconNeutral`, the theme's control grey. Nothing visible
reads it (every button, check box and text box has a template of its own); it is set
so a stock state can't pick up the Windows accent.
- **`ApplicationAccentColorManager.ApplySystemAccent()` silently does nothing here.**
  It resolves the theme through `ApplicationThemeManager`, which this app never
  drives. Use the explicit `Apply(color, ApplicationTheme.Light / .Dark)` overload.
- It writes its brushes straight into `Application.Resources`, which outranks every
  merged dictionary, so a switch writes them again.

**WPF-UI's own keys are not overridden.** The old `Ps1Theme.xaml` restated about 160 of
them; since session 31 they are WPF-UI's own values (Light, or Dark in Dark), and a
ThemeLab brush dump (`--brushdump 1`) showed none reaching anything visible. Where a
stock control still shows (the filter chip is a `ui:Button`; menu items use WPF-UI's
templates), its colours are set from tokens on the control or in its style's own
resources, as the `MenuItem` styles in `Chrome.xaml` do. A new stock control needs the
same treatment.

Still load-bearing from the old design system:

- **Use `DynamicResource` for styles, templates and brushes in windows**, and never
  `StaticResource` for a colour or shadow: a theme switch replaces them.
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
on the picked image's MIME type. Since session 62 the bytes go to a `.audiofool-cover-*.part`
working file, flushed, then `File.Move(..., overwrite: true)` over the target, so a cut-off
write leaves the old cover whole. An existing cover of a different extension goes through
the `recycle` callback (`RecycleBin.Send`) **after** the new one is in place; if the bin
refuses it (no bin on the drive) it is kept, `FolderArtWriteResult.Warning` says so, and
the path returned is whichever file `TagReader.FindFolderArt` would pick.

An album can span multiple physical directories (multi-disc sets, stray compilations).
`AlbumDirectories(Album)` collects distinct `Path.GetDirectoryName` values across all
`album.Tracks`. Folder art is written to each of them, not just `Album.FolderArtPath`,
**except** one holding another album's song (`FolderArtTargets.HoldsOtherAlbums`, session
62): the same album name counts as this album, an empty one never does. A skipped folder
is named in the status bar; its songs still get the embedded cover. *Save Embedded Art*
and Keep Best Cover (`EmbeddedArtExtractor`) follow the same rule through their
`sharedFolder` argument, and both writers go through `FileSwap.WriteWhole`.

After any art write `AlbumArtService.InvalidateAlbum(album)` busts all four cache key
shapes for the album (thumbnail, full-viewer, per-track). Called with the *pre-edit* album
— its current identity and track paths are what is actually in the cache. If the album is
renamed, the new identity has no cache entry and decodes fresh automatically.

**Session 11 added "keep" semantics to part of the edit.** The rule to remember is
this: an album edit must never write a value the user did not choose to every track.
Since session 60 every field of the album window follows it, Artist, Album Artist,
Album and Year included (until then those four were written album-wide from the first
track: a compilation lost its artists, an untagged album got "Unknown Album"). The
three rules:

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

**Since session 40 the album and one-song dialogs are not modal**: they follow the
Albums list and Songs table (`MainViewModel.AlbumEditor.cs` / `TrackEditor.cs`), and
every save looks the tracks up again by path before writing, because the library can
change while the window is open. Only the several-tracks dialog is still modal.

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
entries for Add Folder bind their commands via
`{Binding Source={StaticResource Proxy}, Path=Data.AddFolderCommand}`.

### One copy, and songs opened from Explorer (session 59)
- **`Services/SingleInstance.Start(e.Args)`** runs first in `App.OnStartup`. The first
  copy owns the mutex `Local\AudioFool-<user SID>-<session id>.instance` and listens on
  the pipe `AudioFool-<SID>-<session>.open` (`PipeOptions.CurrentUserOnly`, on a
  background thread). A later copy calls `AllowSetForegroundWindow(ASFW_ANY)` (only the
  process the user just started may take the foreground, so it passes that on), sends
  full paths one per line ending in a zero byte, and quits once the first copy answers
  that its UI thread took them (`Deliver`: up to 3 s, then withdrawn, so nothing
  arrives twice); an empty message (the shortcut again) only brings the window
  forward.
- **Closing** (session 61): the main window's `Closed` calls `StopListening`, and
  `OnExit` lets go of the mutex last, after the saves. A later copy that gets no answer
  keeps trying for 10 s, alternating 500 ms connects with `WaitOne(0)` on the mutex,
  and becomes the one copy as soon as it is free (an abandoned mutex counts). Only a
  copy that neither answers nor lets go in 10 s (hung) gets a second copy started
  beside it. The listener survives any exception (it's on a thread of its own) and
  retries after 200 ms, so a pipe still held by a closing copy doesn't spin it.
- **A fault during startup** (session 61) shows the error and ends the app
  (`Shutdown(1)`) instead of leaving a windowless copy holding the mutex; the
  last-resort handler also ends the app when no window is open.
- **Explorer starts one copy per file**, in no order, so files are batched until 500 ms
  pass with none (a `DispatcherTimer`), the first copy's own files included, and
  `OpenedFiles.InPlayOrder` sorts them (folder, then natural name).
- **`MainViewModel.OpenFiles`** waits (`_pendingOpen`) for the engine (`_engineReady`,
  set in `InitialiseAsync`) and, for a song under a music folder, for the first library
  (`ApplyLibrary`), so it can be shown in its album. Something opened skips
  `RestoreLastPlayed`. A song outside the library plays as a queue of its own;
  `RememberLastPlayed` already ignores tracks the library doesn't hold.
- **Testing:** `Start` takes a name, so ThemeLab's `--singleinstance` runs under a test
  name and never touches the user's running copy. ThemeLab doesn't run `App.OnStartup`
  at all, so no other mode is affected. Don't launch the real app from Claude's shell
  to test it: it would show on the user's screen and read stale AppData (see below).

### Clicks don't scroll the lists (session 59)
- A press on a row that's only half in view used to scroll it in, so the second click
  of a double-click hit the row that moved under the pointer. **`_pressedList`** (in
  `MainWindow.xaml.cs`) is the list a left press went into, from the press until just
  after the release (cleared by any key). While it's set, **`Row_RequestBringIntoView`**
  (an `EventSetter` on `BrowserRow`, which every list row style is based on, and on
  the grid's row style) refuses the row's bring-into-view, and
  `BrowserList_SelectionChanged` skips its own scroll to the selection.
- The handler has to sit on the **row**: `RequestBringIntoView` bubbles, and the
  list's `ScrollViewer` handles it before the event reaches the list itself.
- Keys, type-ahead and the jump to the playing song still scroll. A new list with
  double-click needs the same: a row style based on `BrowserRow` and an entry in
  `HookClickWithoutScroll`.

### The close X: safety net and click log (sessions 59 and 66)
- **`Services/CloseButtonSafetyNet`** (session 66, installed in `App.Launch`) stays.
  WPF-UI 4.3.0 clicks title-bar buttons from WM_NC* messages and loses a click when
  anything clears the X's mark between press and release. The net reads presses and
  releases off the thread's queue (`ComponentDispatcher.ThreadPreprocessMessage`),
  never handles one, and after a release on the X that followed a press on it waits
  for Background priority (WPF-UI's own click is queued at Input) and calls the X's
  `TitleBarButton.InvokeClick()` unless WPF-UI raised `CloseClicked`. A double-click's
  second press and a client click while captured count as presses. No reflection.
- **`Services/TitleBarClickLog`** (diagnostic, sessions 59 and 66) reads the same
  queue for any window with a WPF-UI `TitleBar`, and through a `WH_CALLWNDPROC` hook on
  the UI thread every WM_NCHITTEST between a press on the X and its release; it writes
  `titlebar:` lines through `PlaybackDiagnostics.Note`. It reads the X's private
  `_isClickedDown` by reflection (a newer WPF-UI may rename it, and the log then says
  False). It never handles a message. Once the hit-test lines have named what wipes
  the mark, it can go; the net stays.

---

## Claude's shells and AppData (found in session 58)

Claude's tools run inside the **Claude desktop app's MSIX package**: PowerShell, Bash,
`dangerouslyDisableSandbox` or not, and anything they launch (ThemeLab, probes).
- **New AppData writes are redirected.** New files and folders they create under
  `%LOCALAPPDATA%` or `%APPDATA%` go to the package's private store,
  `C:\Users\MarcusRenlund\AppData\Local\Packages\Claude_pzs8sxrjxfjjc\LocalCache\Local\…`
  (or `…\Roaming\…`).
- **Reads go to those private copies.** Once a private copy exists, Claude's reads see
  it. **AudioFool and the user never see any of it.** `Get-Item -Force <path>` shows
  the redirect in `.Target`.
- **Not redirected:** `%LOCALAPPDATA%\Programs\AudioFool` (which is why installs work),
  `C:\MusicPlayer`, the music drives and temp folders.
- **Stale private copies exist** of `settings.json`, `library.json`,
  `library.json.beforemigration` and `playlists.json`, left by earlier sessions'
  ThemeLab and probe runs. **So Claude's reads of the user's settings or library can be
  stale.** Earlier "backups" of library.json made from Claude's shell were private
  copies, not real backups. `playback.log` has no private copy, so its reads are real.
- **The rule:** never place data AudioFool must read in AppData from Claude's shell;
  ship it beside the exe. Clearing the stale private copies was offered to the user,
  not done.

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

Named elements available to query: `SearchBox`, `ArtistList`, `AlbumList` (both multi-select since session 40), `TrackGrid`, `PlaylistsToggle`, `PlaylistList`,
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

**Hardware rendering, off the desktop.** ThemeLab draws in WPF's software renderer;
the app draws in hardware, and the two can differ (effects especially). A WPF window
cloaked before it is first shown (`EnsureHandle`, then `DwmSetWindowAttribute`
`DWMWA_CLOAK` = 13, checked with `DWMWA_CLOAKED` = 14; `ShowActivated = false`) is
composed by DWM but never on screen and never active, and `PrintWindow` with flag 2
captures it. A window parked off-screen at `Left = -20000` captures as solid black.
Session 53 used this to check `DropShadowEffect` colours in hardware; the test app is
not kept, so rebuild it (a dozen lines around those calls).

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

A switch at runtime (session 52) is the other case: every window it has to reach is
open, so `Application.Resources` notifies them all, and a window opened later is built
in the new theme as at startup.

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
ThemeLab.exe --window stats --w 900 # statistics, from the real library.json (add --qualitycache <scratch> --fakeflags 1 --clearances <scratch> --clearall 1 for Clear All, session 59)
ThemeLab.exe --window stats --w 900 --cutoffdemo 1 [--cutofffilter "Cut off"]   # five scratch albums with cut-off covers, the cover check, the art shown: ALL OK (session 70; no music drive needed)
ThemeLab.exe --window click --click Year        # click a Statistics row, render the result
ThemeLab.exe --window click --click Rush --search Buckethead
ThemeLab.exe --window click --click Year --fix 1964   # simulate fixing the selected album
ThemeLab.exe --window tags --album "Saturn Return" --w 460   # album dialog on a real album
ThemeLab.exe --window tags --track "Polygon Weather"         # track dialog on a real track
ThemeLab.exe --window tags --album "Saturn Return" --set "!Comment;Genre=Ambient"
ThemeLab.exe --window tags --album "The Inevitable End" --set "^Zeros"   # press Remove Leading Zeros
ThemeLab.exe --window tags --album "Saturn Return" --pickimage <file> [--theme dark]   # Choose Image… with that file picked: prints the size, format and message line, then moves the file away and prints what Save would embed (session 69)
ThemeLab.exe --window artsearch --artist "Rush" --album "Moving Pictures" --use 1 --w 820 --h 640
ThemeLab.exe --artmenu 1          # the playlist header picture's context menu. Opens a REAL popup, which Windows keeps on a monitor: it can flash on the desktop
ThemeLab.exe --window tags --album "Goodbye Yellow Brick Road" --pick 1-8 --set "DiscCount=2"  # a grid selection
ThemeLab.exe --rows 1,2,3,4       # several selected grid rows
ThemeLab.exe --window edit --w 1300 --h 600   # in-place grid edits
ThemeLab.exe --window queue       # edit queued and playing tracks while the real engine plays silently
ThemeLab.exe --window queue --long "D:/Music/x/a.flac;D:/Music/y/b.dsf" [--longcomment 3000000]   # also save to real tracks (copied to scratch) while they play
ThemeLab.exe --window closex [--theme dark]   # Statistics' close button, posted messages: a normal click (WPF-UI clicks); clicks WPF-UI loses (a mouse leave, another thread's hit test, a double-click's second press, a client click while captured: the safety net clicks, once); cancelled closes stay cancelled; press-and-slide-off does nothing. "ALL OK"; prints the click log with its hit-test lines (session 66)
ThemeLab.exe --window intro --w 1300 --h 800 [--theme dark] [--dpi 1.25]   # the About intro at nine moments, never shown (no sound): renders <out>-<ms>.png, prints opacities and centre/corner colours, opens the sound without playing it (session 64)
ThemeLab.exe --window lend   # About's loan of the device in Bit-Perfect, real engine and default device, digital silence: while lent another exclusive open succeeds, returned paused in place, Resume plays ("ALL OK"). Ask the user first: it takes their DAC ~5 s
ThemeLab.exe --window playbackfixes   # session 67's playback batch through the real engine and default device, silence only (WAVs, a DSF of 0x69): songs that won't open skipped (end of a track, Next, Previous, a queue ending on them); in Bit-Perfect the DAC free after Stop and after a queue ends, held while playing and paused; reopens at the heard place, playing and paused; DoP never on a shared output (refusals set by reflection). "ALL OK". Ask the user first: it takes their DAC ~30 s
ThemeLab.exe --window ratechange [--stopinwait 1]   # a Bit-Perfect rate change (44.1 → 96 kHz) through the real engine and default device, exclusively for ~5 s, on two WAVs of digital silence (Bit-Perfect is full volume: never real music); prints "rate change: waited N ms; ... dropped by the reopen: M ms" (0 since session 61). --stopinwait presses Stop during the wait. Ask the user first: it takes their DAC
ThemeLab.exe --window splice --long "D:/Music/x/a.flac;D:/Music/x/b.flac"   # a gapless join through the real engine, silent; prints the log's "gapless:" line (needs the device free; since session 60 any rates join in shared mode, "(resampled)" when they differ)
ThemeLab.exe --window folderart   # cover.jpg on scratch folders through the album save, Save Embedded Art and Keep Best Cover: old cover.png to the real Recycle Bin, a folder shared with another album left alone, no leftovers ("ALL OK"; leaves one tiny scratch cover.png in the bin)
ThemeLab.exe --window albumkeep   # the album tag window on scratch albums (a compilation, an untagged album) through its real save; checks nothing unchanged is written ("ALL OK")
ThemeLab.exe --window findreplace [--theme dark] [--dpi 1.25]   # Find and Replace through its window and the real saves on scratch songs: options, ticks, a song retagged after Find (untouched), two artists, Replace, Undo, Ctrl+H, then two songs selected and one (session 67); renders <out>-empty/-found/-prompt/-done/-undone/-selection ("ALL OK"; session 63)
ThemeLab.exe --window findreplaceperf [--find <text>]   # the same window over the real library.json, read-only (Replace never pressed): times Find, typing a replacement, the header box and a sort; renders <out>-real.png
ThemeLab.exe --window safesave --long "D:/Music/x/a.flac;D:/Music/x/b.flac"   # copies of one album's songs (never the songs) given a new cover through the album save, the window closed mid-save; checks the window waited, the audio is identical, one cover, creation date kept, no leftovers ("ALL OK")
ThemeLab.exe --window device --long "D:/Music/x/a.flac;D:/Music/x/b.flac"   # a change of default output device, simulated: playing, paused, stopped, next track
ThemeLab.exe --window lastfm --state connected --w 480    # setup|waiting|connected|failing|rejected
ThemeLab.exe --w 1300 --h 700 --lastfm failing   # the status-bar indicator
ThemeLab.exe --menushot menu.png --scale 2      # the logo menu's drop-down, without opening it
ThemeLab.exe --bg "#FF00FF"                     # magenta behind the window: any unpainted gap shows
ThemeLab.exe --output "Exclusive · 176.4 kHz · DSD over PCM"   # the output readout under the volume slider
ThemeLab.exe --libshot libs.png --scale 3   # the Libraries submenu, one folder ticked, one not
ThemeLab.exe --window folders               # folder list: merging, relocation, unticked status, Remove
ThemeLab.exe --window nowplayinglines --w 1400 --h 700 [--theme dark] [--dpi 1.25]   # the now-playing bar's four lines for a hi-res FLAC, a CD FLAC, an MP3 and a DSF: prints both lower lines, text height against the art, bar height against nothing playing; renders <out>-1..4.png; "ALL OK" (session 68)
ThemeLab.exe --window statusclick --w 1300 --h 700 [--theme dark]   # the status line and dot: a single click's gesture opens Statistics (not run: it is modal), hand pointer, underline on hover; renders <out>-plain/-hover.png; "ALL OK" (session 68)
ThemeLab.exe --window dialogplacement   # a hidden main window on each monitor, maximised and normal, and a hidden dialog placed by DialogPlacement: centred on its monitor, kept on it; never shown (session 71)
ThemeLab.exe --window playfailure   # the real engine and BASS on a missing file, an unused drive letter, text named .flac and a file this process holds: each PlayFailure and its line; nothing reaches the device (session 71)
ThemeLab.exe --window nowplayingjump   # Ctrl+J over the real library.json (read only): its album, the song selected, a search/filter cleared only when it hides the song (session 71)
ThemeLab.exe --window statusfilter [--theme dark] [--dpi 1.25]   # the status bar's ×, over the real library.json (read only): a filter (hidden/shown, right after the text, trimmed long line, hover, a click clears and restores the totals), then one-off messages (scan counts, a save; none while scanning or saving, none on a search's line); renders <out>-off/-on/-saved/-long/-hover/-scan.png; "ALL OK" (session 71)
ThemeLab.exe --window queueend [--theme dark]   # the end of a queue through the real engine (shared, 5%, silent WAVs, a blue cover.jpg) and the view model: the bar back on the first song at 0:00, its cover, stopped, device let go; Play starts from it; shuffle; a one-song queue; renders <out>-end.png; "ALL OK" (session 72). Run with Start-Process -Wait -RedirectStandardOutput to see its lines
ThemeLab.exe --window headerpress           # a press on a letter of each Songs header (a LetterSpacing Run), then the header's click: no exception, each sorts; "ALL OK" (session 68)
ThemeLab.exe --window drives                # drives plugged in and pulled out while running, on subst letters (Q:-Y:): an unrelated drive, the music drive pulled, back under another letter (lists, queue, now playing, Next), back under the same letter, F5 alone; silent; "ALL OK" (session 68)
ThemeLab.exe --window tokens --out tokens.png   # the central theme: lookups from MainWindow, XAML usage, fonts, specimen
ThemeLab.exe --dpi 1.25 --w 1720 --h 1080   # laid out and drawn as on a 125% monitor (all monitors here are 100%); with --window, the dialog too (session 53)
ThemeLab.exe --focus ShuffleButton --focusvisual 1   # draw the keyboard focus outline, which focus alone doesn't
ThemeLab.exe --peers 1                      # the automation tree: what a screen reader reads for each control
ThemeLab.exe --tabwalk 18                   # 18 real Tab presses from the search box; prints where focus lands
ThemeLab.exe --tabwalk 0 --keys "Tab,Tab,Tab,Tab,Down" --tabfrom SearchBox   # any key sequence
ThemeLab.exe --logohover 1                  # logo item highlighted; prints its tooltip and its template's background triggers
ThemeLab.exe --window seek --file "D:\Music\...\long.flac"   # click the seek bar's track mid-play (silent); prints the position trace, "no bounce" or "BOUNCED"
ThemeLab.exe --window waveform --file "D:\Music\...\x.flac" --at 0.4 [--noglide 1]   # seekbar waveform: read time, 2 s glide trace, window + 3x seekbar render (plays silently)
ThemeLab.exe --window playlists --w 1720 --h 1080   # hearts, a playlist, Liked before/after an unlike, an empty playlist, the name prompt (scratch playlists file)
ThemeLab.exe --window reorder --w 1720 --h 1080     # drag to reorder a playlist: moves, a block, Esc, no-ops, sorted; renders the drop line (scratch playlists file)
ThemeLab.exe --window multiedit --w 1720 --h 1080   # several artists, then albums, selected and retagged on scratch files; renders <out>-artists.png / -albums.png
ThemeLab.exe --window albumeditor --w 1720 --h 1080 # the album tag window following the list: move, Save / Don't Save / Cancel, rename, Close (scratch files); renders <out>-window.png / -prompt.png
ThemeLab.exe --window clear --w 1720 --h 1080 --qualitycache scratch\quality.json [--clearances scratch\cleared.json]   # clear an album and a song from the first Quality Check row with songs, the menus, Put Back, the Analyze buttons; renders the Analyze window (make the cache first with --window stats --qualitycache ... --qualitycheck 1 --qualitystop 75)
ThemeLab.exe --window trackeditor --w 1720 --h 1080 # the song tag window following the table, the same cases (scratch files)
ThemeLab.exe --theme dark                       # every window built in PS2 Dark (light by default)
ThemeLab.exe --theme light --switch dark        # built in Light, switched live before the first render: must match --theme dark pixel for pixel
ThemeLab.exe --window themeswitch --file <audio> --artfile <image> [--theme dark]   # switch by the Theme button and back by Ctrl+T with the real library, the file playing silently, the art viewer and Analyze open; checks playback, selection, scroll, dialogs, settings.json (restored)
ThemeLab.exe --window rowclick --w 1180 --h 1240 [--artist Nhato]   # double-click the row cut off at the bottom of each list: must not scroll, must land on that row; then Down must scroll (session 59)
ThemeLab.exe --singleinstance 1                # the one-copy handover under a test name: three children at once with a song each, one with none; one batch, four activations (session 59)
ThemeLab.exe --singleinstance exit             # copies started while the one copy closes: after it lets go (takes over, and a later one reaches the new copy), after its window closed but before it lets go (waits, then takes over; nothing reaches the closing copy), and while its UI thread is busy 1 s (handed over once); "ALL OK" (session 61)
ThemeLab.exe --window openfiles               # songs opened from Explorer, real library, silent: before the engine is ready, a library song before the library shows, from a search in playlist mode, three at once, a text file (session 59)
```


`--tabwalk` feeds keys through `InputManager`, as the keyboard does. Raising `KeyDown`
on an element (as `--window edit` does) skips WPF's Tab handling entirely, and
`MoveFocus` skips the controls' own key handlers, so neither shows the real order.

`--dpi` is the real scaling test; `--scale` only enlarges the bitmap. It draws the
window straight into the bitmap, because through the usual VisualBrush every vertical
edge softens at an emulated DPI, which looks exactly like a scaling bug in the app
(session 5 reported one, wrongly). Since session 53 it reaches everything a case draws:
`Save` lays a dialog, prompt, drop-down or tooltip out again at the scale before
drawing it (`LayOutAt`), and sizes the bitmap from that layout when the case asked for
the element's own size. Before, only the main window was laid out at the scale and the
rest was drawn enlarged from its 100% layout. `--focusvisual` sets WPF's internal "always show
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

`--window tags` waits for an album or selection window's background read
(`WaitForDetails`, session 47) and prints `details: greyed at open=... read in ... ms`
before the fields; without the wait every detail field would print empty.

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
  Both themes are opaque, so none of it should show; that is what makes
  `--bg "#FF00FF"` a gap detector. Every window render has 0 magenta pixels, at 100%,
  125% and 150% (counted in session 53, which also found an older note here about a
  6 px strip no longer true). A menu's drop-down or a tooltip drawn on its own shows the
  backdrop in its shadow margin and round corners, the same in both themes.
- **Windows are placed by their layout box** (session 53): the VisualBrush that draws
  a window, or anything in one, has its `Viewbox` set to the window's own rectangle.
  Placed by what it draws, as before, a shadow reaching past the left edge (Dark's
  cover shadow in the art viewer) moved the whole render right. Drop-downs and
  free-standing tooltips keep the old placement: one drawn without being opened still
  carries its opening animation's offset, which that placement undoes.
- **`--theme light|dark`** picks the theme every window is built in (light when not
  given, so old recipes render as before). **`--switch light|dark`** switches live,
  through `ThemeService.Switch`, just before the first render, once the case has built
  its windows; a switched render must match a fresh start in that theme pixel for pixel.
  It doesn't reach the free-standing context menu and tooltip `--popupshot` builds
  (outside any window, never opened; WPF refreshes real ones as they open), nor
  `--window tokens`' specimen, which draws token values as data.

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
- **`git push` from Windows PowerShell reports exit code 255 when it worked.** Git
  writes its progress to stderr, which PowerShell 5.1 counts as an error. Read the
  output: a `abc..def  main -> main` line means the push went through.
- **The sandbox guard also blocks `Remove-Item` on a path held in a variable**
  (`Remove-Item "$d\files\*"`), not only in combination with the PATH assignment.
  Overwrite with `Copy-Item -Force` instead, or spell the path out.
- **PowerShell does not wait for a GUI-subsystem exe redirected with `>`.** ThemeLab
  returned at once with an empty log and no PNG. Pipe it (`| Out-File`) instead, but
  not into `Select-Object -First`: that ends the pipe early and kills the process
  before it saves.
- **TagLib's ID3v2.3 date handling is wrong**: it writes `TDAT` month-first and reads
  it the same way, so a TagLib round trip passes while other players see day and
  month swapped. Check the bytes on disk. See *Release dates* under session 12 in `HISTORY.md`.
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
  directly. See *Leading zeros* in `HISTORY.md` (session 16).
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
  with UI Automation and confirm `SearchBox` is in it (the snippet is under session 34 in `HISTORY.md`).
- **A launch from Claude's shell writes playback.log** (found in session 63): after its
  startup scan the app logs "memory clean-up ran" (`MainViewModel.ReclaimScanMemory`).
  From the shell that write would give Claude's shells a private copy of the log (see
  *Claude's shells and AppData*), and every later read of it, the close-X `titlebar:`
  lines included, would see that stale copy. So since session 63 an install is checked
  by hashing the installed files against `publish\`, with ThemeLab having built the
  real MainWindow, and the user's own launch is the start-up test; the UI Automation
  check above is for an AudioFool the user started.
- **`library.json` can list files that no longer exist.** It still held 2 m4a and
  10 wav files deleted since the last full scan. Before picking a sample file from
  the cache, check that it exists (`Test-Path -LiteralPath`).
- **TagLib never makes a file smaller.** Removing or shrinking a cover leaves its room
  as padding. Only `TagPadding.TryShrink` gives space back.
- **TagLib mishandles pictures on some real files** (session 44, all found on
  D:\Music; don't use `Tag.Pictures =` to remove covers, use `TagPadding`):
  - An MP3 with **two ID3v2 tags back to back** (Buckethead, Van Halen): TagLib reads
    and writes only the first, so a cover in the second survives, unseen by TagLib.
  - An MP3 with an **APE tag**: setting `Tag.Pictures` writes the cover into the APE
    tag too, a second copy.
  - A FLAC whose **padding would pass 16 MB** (FLAC's 24-bit block length; Shpongle,
    Tesseract): TagLib writes the padding block's length wrapped, and the file has
    zeros before its audio.
  - Count pictures with `TagLayout.Pictures` (the bytes, every tag), not TagLib's
    combined tag, which shows only the first tag holding any.
- **`File.Replace` keeps the replaced file's creation date** (checked on D:'s NTFS).
  Recently Added and the artist sort read it, so any whole-file rewrite must swap in
  through `File.Replace`, never delete-and-move. `[IO.File]::Replace` from PowerShell
  5.1 needs `[NullString]::Value` for the backup path, not `$null`.
- **The sandbox blocks `rm` of a relative glob after `cd`** (`cd $D; rm -f files/*`).
  Use a fresh scratch folder instead of clearing one.
- **A file's audio region can end in tags.** An MP3 may end with an APEv2 tag and an
  ID3v1 tag, which TagLib re-renders on save; compare them as tags, not as audio
  (`CoverCleaner.AudioHash`).
- **`DropShadowEffect` draws its colour's linear-light (scRGB) values as if they were
  sRGB**, in software and hardware alike. Black can't show it, so no shadow did until
  PS2 Dark's blue glow: (76,141,255) came out as about (16,66,255). `TokenResources`
  hands every shadow colour over with `Color.FromScRgb` built from the token's sRGB
  values, which draws the token's colour (session 53). A coloured effect made anywhere
  else needs the same.
- **Even a transparent effect can move pixels next to it.** A `DropShadowEffect` at
  opacity 0 inside the Play/Pause button changed 76 pixels of the face's own shadow by
  one colour level, at 125% only. Where a theme must render exactly as before, collapse
  an element whose effect can't be seen (the glow's disc does, by a trigger on its
  effect's opacity).
- **Background shells end with the session** (session 58). A job Claude starts with
  `run_in_background` is stopped, children included, when the conversation closes:
  the beat screen died at 21:40 with three quarters to go. Start anything that runs
  for hours in a Terminal panel tab (`run_in_terminal`), which outlives the session
  and shows the user its progress, and make it resumable. A Terminal tab isn't safe
  either: on 2026-10-20 the beat screen's tab was gone after about two hours (most
  likely sleep), and a restart in a new tab picked it up. Pass full paths to
  `run_in_terminal`: it refuses variables such as `$env:TEMP`.

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

Open items only; finished ones are in `HISTORY.md`. Open a session by asking the user
about step 1 (ask; don't re-explain), then step 3, then which of steps 2 and 4 to take.

1. **Ask the user to try what was checked off-screen only:**
   - **Session 71:** the × after the status text (clears a Statistics filter,
     dismisses a one-off message); a dialog with the main window maximised on the
     second monitor; Ctrl+J under a search; Last.fm still connected after the update
     (its secret is now encrypted).
   - **Session 68, drives:** with AudioFool open, pull the SSD: the status bar says
     D:\Music is not available within a couple of seconds. Plug it back in: the
     warning clears; under another letter the status bar says "Music folder moved
     to …" and songs play (Next carries on in the queue). F5 does the same if the
     drive came back while AudioFool wasn't looking.
   - **Session 67, playback:** a song that won't open skipped by Next or the queue
     (the status bar names it).
   - **Session 61:** a Bit-Perfect rate change keeping the old track's last ~0.2 s,
     heard on music.
   - **Session 60:** closing AudioFool during an album save with a new cover keeps the
     window open with "Finishing saving" until it's done; the album window on a
     compilation (boxes start empty with "Varies by track - kept unless changed", and
     saving another field leaves each track's own).
   - **Session 59:** double-click a song in Explorer, one from `Downloads` (plays on
     its own, the view stays) and one from `D:\Music` (goes to its album and plays with
     the album after it), with AudioFool open and closed, and several at once; **Clear
     All** in Statistics' Quality Check (the prompt, then every row None).
   - **Session 39:** edge scrolling while dragging in a playlist (real drags are
     confirmed; asked 2026-10-20: not tried yet).
2. **A visible, editable queue view**: the most conspicuous missing player feature.
3. **The close X that sometimes ignores clicks** (sessions 51, 59 and 66). In tag
   windows and Statistics the X sometimes ignored **several clicks in a row**. Caught
   in session 66: 19 lost clicks on Statistics' X, each a clean non-client press and
   release with no capture and no mouse leave, WPF-UI's mark gone by the release. Since
   then `Services/CloseButtonSafetyNet` clicks the X when WPF-UI loses a click, and the
   click log records every hit test between a press on the X and its release (*The
   close X: safety net and click log*). The user: "It's working well so far."
   - Ask whether it has ignored a click since, and whether the window buttons still
     highlight on hover (session 51's fallback was removed over that, though it was
     never shown to be the cause).
   - Read the `titlebar:` lines in `playback.log`. "WPF-UI lost the click on the X; the
     safety net clicked it" marks each catch. The "hit test at x,y from another thread,
     pointer at ..., over nothing; X pressed True" line before it is the question that
     wiped the mark: a spot far from the pointer, sent by another thread, means another
     program (PowerToys, Logi Options+, ShareX and Teams run here; quitting them one at
     a time during a spell would name it). If no hit test shows and the mark is already
     False at the first one, the press never registered: look at WPF-UI's handling of
     the press instead.
4. **Smaller open items** (session 60's review is otherwise all fixed):
   - Harmless: a new scrobble skips the Last.fm backoff (one request each, the backoff
     isn't reset); an exception other than `LastFmException` escaping a send skips it
     altogether (unlikely).
   - `AudioEngine` itself has no tests (1,500 lines; `OutputChain` and `BassRuntime`
     neither). Session 67 pulled the playback decisions out into tested pieces
     (`TrackEnd`, `ReopenPoint`, `DopRule`, `PlayOrder.Candidates`); the rest is still in it.
   - `LiveSpectrumTests.An_update_allocates_nothing` used to fail now and then; made
     sturdier in session 67. Watch that it stays quiet.
   - A folder added during a scan is skipped by that scan (F5 or the next start reads
     it); small, left out on purpose.
   - **Ideas offered in session 66, not picked yet:** Windows' own media controls (SMTC:
     the media keys from any app, the song and cover in Windows 11's media pop-up,
     headphone buttons; F9–F11 could then go) and a Duplicates row in Statistics (albums
     held twice, and the space they take).
5. **The visualizer: removed in session 65** (the user's call; they may build one of
   their own later). The notes are in `HISTORY.md` (*The visualizer and the projectM
   trial*), and the code can be read in git at `b30812c`. **Kept as groundwork** (the
   user confirmed keeping it, session 73): the hearing clock (`AudioEngine.Heard`, also
   the seek bar's timing), the tap (`SampleTap`; `AudioEngine.TapEnabled`, off by
   default, `TapFormat`, `TryReadHeard(..., framesAhead)`), `Core/Analysis/TapConverter`,
   `Core/Analysis/LiveSpectrum` and `Core/Analysis/VisualizerFeed`, each with its tests.
   A visualizer of our own would start from `VisualizerFeed`: give it a window and a
   renderer, and new tokens in both files. Don't suggest projectM again.

**Design notes for the next round of critique.** The user works in a screenshot loop:
they look, then give precise notes, and lately each note removes decoration.
Suggested and not picked: filled transport glyphs, and coloured shuffle/repeat when
on. Don't add them unasked.

**Dropped by the user (2026-10-06): don't build or suggest them.** Library-wide tag
stripping, recovering dates lost to pre-session-12 saves from MusicBrainz, resuming
playback on launch, a Last.fm Love button, TAK/DTS playback. Also declined
(2026-10-07): converting hi-res files to 16-bit to save space. Dropped 2026-10-14:
noticing a change of the Windows shared format without a restart, lowering memory
during a scan (FLAC reader skipping PICTURE blocks, `GCConserveMemory`), and code
signing.
