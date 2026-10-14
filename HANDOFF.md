# AudioFool — session handoff

Updated 2026-10-14, session 51. Read this alongside `README.md`: the README covers
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
| Playback log | `%LOCALAPPDATA%\AudioFool\playback.log` (stall diagnostics, rolls at 4 MB; session 48; first stutter read: see session 48 below). Readable from Claude's shell as long as the shell never writes it |
| Native BASS DLLs | `C:\MusicPlayer\lib\bass\x64\` (13 of them) |
| Music library | `D:\Music` — external 4 TB SSD "Marc SSD", ~26,000 tracks, 692 GB |
| Session history | `C:\MusicPlayer\HISTORY.md` (what each session changed, oldest first) |
| Theme spec and decisions | `C:\MusicPlayer\design\` (`theme-spec.md`, `theme-tokens.json`, `progress.md`) |

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
with 0 warnings and passes the whole suite (first checked in session 5; last checked in
session 47, 624 tests). If you add a dependency that lives outside NuGet, re-run that check — it is
the only thing that catches a file you forgot to track.

### Releases

Five so far, all on GitHub (private, so downloads need a signed-in account):
**v0.1.0** (2026-09-30, the user by hand), **v0.2.0** (2026-10-02), **v0.3.0**
(2026-10-09, session 45), **v0.4.0** (2026-10-12, session 47) and **v0.5.0** (2026-10-13, session 48), all four by Claude. Handle a release end to end when asked, but
**confirm the version number and the notes with the user before `gh release create`**:
it publishes a tag. Middle number for new features, last for fixes only.

1. Commit and push anything outstanding, and run the tests.
2. Bump `<Version>` in `src/AudioFool/AudioFool.csproj` (the only place it lives),
   commit ("Version x.y.z") and push, so the tag lands on that commit.
3. Fresh `dotnet publish` (the command above), then delete `publish\*.pdb`. Check
   `(Get-Item publish\AudioFool.exe).VersionInfo.ProductVersion` shows the new version.
4. Compare the file list with the last release (`gh release download v<old> --dir
   <temp>`, unzip, `Compare-Object` the names): **19 files** (the exe, 13 BASS DLLs,
   5 WPF native DLLs). A missing loose DLL breaks the app on another machine.
5. `Compress-Archive publish\* $env:TEMP\AudioFool-<ver>-win-x64.zip` (never inside
   the repo). 0.3.0 was 60 MB.
6. Notes in a file (dodges quoting): `gh release create v<ver> <zip> --target main
   --title "AudioFool <ver>" --notes-file <file>`, then `git fetch --tags`.
7. Check the tag with `git rev-list -n 1 v<ver>`. **Not `git rev-parse v<ver>^{commit}`**:
   PowerShell mangles the `^{}` and the command fails.
8. Install the same build over the installed copy (close the app first; it stops
   playback, so say so).

The notes are written for the user, grouped New / Improved / Fixed, from
`git log v<old>..HEAD` with the Docs and ThemeLab commits left out.

---

## State at handoff

- **Everything in `README.md` is implemented and verified**, and the installed app is
  a build of `main` as pushed. **670 tests** pass.
- **Last release: v0.5.0** (2026-10-13, session 48, tag on `a8bffb1`, 60.2 MB zip,
  installed). It holds everything from session 48. Session 49's fix is unreleased.
- **PS1 is the only theme**, built from `design/theme-tokens.json`. `design/progress.md`
  holds every design decision; read it before UI work.
- **Nothing is half-done.** Open items are under *Suggested next steps* at the end.

Recent sessions, newest first (full notes in `HISTORY.md`):

- **51** (2026-10-14): **DSD passthrough folded into Bit-Perfect** (the user's call).
  The logo-menu item started audio when clicked (its setter jumped to the loaded track,
  paused included). Rather than fix it, it's gone: with Bit-Perfect on, DSD goes out as
  DoP whenever the device can clock the rate, else PCM. `DsdMode` removed everywhere.
  The user believes Keep Best Cover has been run on the 9 DSFs (not checked).

- **50** (2026-10-14): **Keep Best Cover handles DSF.** `TagPadding` reads a DSF's
  layout (header "DSD ", chunk size 28, file size, tag pointer; one ID3v2 tag after the
  music) and rewrites it: everything before the tag copied as is, the tag less its
  extra APIC frames with padding cut to 5 KB, nothing after it, the file size set
  right. `TagLayout.AudioEnd` (the pointer) bounds the music for `Verify`'s audio hash.
  Two things found on the real files: **TagLib writes a DSF's file size one byte too
  big** (accepted: size = tag end or tag end + 1), and the 9 *Dark Side of the Moon (R2R
  Stereo Master)* DSFs each carry **~1.38 MB after the tag, past the header's file
  size**, leftovers of a shrunk tag (counted as padding and dropped). Each has three
  covers: FrontCover 1417² 38 KB (kept, by the front-cover rule), BackCover 1525² 1.19
  MB, Artist 800×444 485 KB. Probe on copies of tracks 7 and 9 (originals untouched):
  Cleaned, ~3.05 MB freed each, tags and duration the same, header size = file length,
  pointer unchanged, creation date kept, a second run NothingToDo. Fixture
  `TestData/sample.dsf` is synthetic (8 KB of random "music", no tag). 669 tests.
  Installed, not yet run by the user on the real files.
  **Then Quality Check:** the user had cleared 161 tracks and asked for fewer flags.
  Measured all 161 from `E:` with the real analyzer (BASS on the no-sound device).
  Most flags are measured facts (16-bit in 24-bit files; silence above 24 kHz), so
  only the weakest rule changed, the user's pick: **"possibly fake hi-res" now needs
  the 25–32 kHz band 45 dB under the treble, not 35**
  (`QualityOpinion.FaintUltrasonicDb`); the user's 38 such tracks measured 35–44.
  Re-measured: those 38 now clean, every other flag unchanged. `QualityCache` version
  2, so the next check reads every track again (~11 min). Clearances are kept
  (`quality-cleared.json`, matched by path or artist/album/file name). 670 tests.
  Installed. Offered and declined: turning off Fake 24-bit and the "possibly" lossy
  rows. Same session: the table at the
  top of this file repaired; three next steps dropped (see *Dropped*); the big-artist
  tag window confirmed.

- **49** (2026-10-14): **a cause of the stutters found and fixed.** The user heard one
  at 7:40, playing from the SD card (`E:\Music`; the SSD wasn't connected). The log
  showed the whole app frozen 300 ms at 7:31, a minute after launch, feed thread
  included, the buffer down to 10 ms of 200. The log couldn't name it (it counted GC
  only inside a fill, and a GC between two pulls holds the next one back at the
  managed callback), so it now counts GC across the gap and labels a watcher freeze
  **GC PAUSE** or **APP STARVED**. A probe confirmed: `ReclaimScanMemory`'s full
  compacting collection on a 458 MB heap froze the feed 352 ms, 20 ms left. It runs
  after the startup scan and the art prefill, both soon after launch. Fix:
  `ReclaimScanMemory` waits while playing (`_reclaimPending`) and runs on pause or
  stop; `GCSettings.LatencyMode` is `SustainedLowLatency` while playing. ThemeLab
  `--window reclaim --file <audio>` (real library, silent): postponed with 0 ms paused;
  a scan's worth of garbage while playing gave 3 background full collections and no
  LATE FILL or BUFFER LOW; on pause it ran (109 ms, 42 MB committed). The log notes
  "memory clean-up postponed" / "ran". 7:40 itself: a switch into Bit-Perfect at
  7:39:52 (a gap by design) and a 73 ms whole-app freeze at 7:40:25 with the feed on
  time. Installed; unreleased.
  **Then a second cause, the one heard:** at 7:57:56 (Bit-Perfect, 44.1 kHz) the user
  heard a gap exactly where the log had APP STARVED 64 ms with 0 ms of GC (the PC, not
  us) and a pull 39 ms late; yesterday's 8:24 was the same, 33 ms late. Shared mode
  rode out 55–60 ms freezes unheard. Exclusive mode was **event-driven**: one 200 ms
  block per device request, and on the DX5 ~35 ms late was audible. Now exclusive
  opens **timer-driven** like shared (`OutputChain`: buffer 0.2 s, period
  `ExclusivePeriodSeconds` 0.01; BASSWASAPI wants buffer ≥ 4× period in exclusive),
  falling back to event-driven if refused; the log's "device opened" line names which
  ("Exclusive (timer)"). Silent probe on the DX5 (80 s of digital silence, exclusive
  has no volume): opened timer-driven, refills every 20 ms (max gap 22 ms) all minute,
  reserve never below 80 ms, and an in-process CPU flood that starved the rest of the
  probe 248 ms didn't delay a single fill. Same samples, still bit-perfect. Also fixed
  a flaky test from session 48 (`A_failed_conversion_leaves_the_file_as_it_was`
  looked for working files across all of %TEMP%). Installed; **the user has to listen**.

- **48** (2026-10-13), four things, released as **v0.5.0**:
  - **Stutters (open).** The user hears occasional stutters while working in other
    apps, Bit-Perfect on or off. `Core/Playback/PlaybackDiagnostics` logs to
    `playback.log`: every device pull timed (wall, CPU cycles, GC pause, gap; LATE FILL
    when a gap passes the learned rhythm + 20 ms), a normal-priority watcher sampling the
    queued audio every 5 ms (BUFFER LOW, APP STARVED), and a summary each minute. Why
    stutters are possible: decoding, file read from D: included, runs inside the WASAPI
    callback with only the 200 ms buffer as slack. A silent headless probe found no
    stall under busy normal-priority threads, 5.7 GB of competing reads on D: or GC
    storms. The one real stutter (~8:24, Bit-Perfect 44.1 kHz) lined up with a single
    WASAPI pull 33 ms late while our fill took < 1 ms: below our code, pointing at the
    driver, but one event isn't proof. Then ~30 min clean, and none reported since.
    Output is a **Topping DX5 on Topping's own driver** (5.74.0.18449, Thesycon-based);
    its control panel was checked with the user: the buffer size there is **ASIO
    only** (512 samples, Safe Mode on) and Streaming is Always On, so nothing there
    applies to WASAPI. Windows shared format: 24-bit 96 kHz.
  - **Open in Windows Explorer** on the song menu (`Core/Library/ExplorerTarget`,
    `OpenInExplorerCommand`); confirmed by the user.
  - **Statistics → PNG COVERS**: embedded PNGs from the cover check
    (`CoverFinding.PngBytes`, covers.json v2) and `.png` files in song folders, counted
    when the window opens (`PngCoverStatistics`, `PngSectionViewModel`); confirmed.
  - **Convert PNG Covers to JPEG** from the artist and album menus under a PNG COVERS
    filter (`PngCoverConverter`, `CoverCleaner.ReplaceCover`, `Services/RecycleBin`,
    `MainViewModel.PngConversion`). The user's calls: quality 95 at 4:4:4 (measured on
    60 real covers: q100 kept 81% of the size and made 1 in 4 bigger, q95 36%), convert
    even when bigger, replaced files to the Recycle Bin. **Run on the whole library by
    the user** after a backup of the 140 affected folders to `C:\AudioFool PNG backup\`
    (42 GB, `folders.txt` lists them; delete once the user is happy). Audited against
    that backup: 1,137 of 1,144 songs byte-identical audio, same tags, one same-size JPEG
    that decodes, creation dates kept; the other 7 are *Bucketheadland* MP3s the user
    retagged afterwards (cover from Cover.jpg, "04/7" to "4/7"), audio identical. All
    131 cover files converted; the 7 scans in song-less subfolders converted too; no PNG
    left in D:\Music. Saved 1.66 GB (1.37 GB in songs, 287 MB of image files, the latter
    still on D: in the Recycle Bin until it's emptied).

- **47** (2026-10-12): artist rows read "12 albums · 14.2 GB" (one decimal, "<0.1 GB"
  for tiny artists); a third click on the ARTISTS header sorts largest first
  (`ArtistOrder`: Name → Recent → Size; confirmed by the user); the header's tooltip
  removed. Docs tidied: this file split into `HANDOFF.md` and `HISTORY.md`. Then,
  after the user found art slow on another machine: **saved cover thumbnails**
  (`Core/Art/ArtDiskCache`, `%LOCALAPPDATA%\AudioFool\art`, list and header sizes,
  prefilled in the background by `MainViewModel.ArtPrefill.cs`; thumbnail 19 → 3 ms,
  worst 185 → 6 ms) and **tag windows that read their files in the background**
  (`TagEditViewModel.ReadDetailsAsync`, fields greyed until read; was a 1–2 s freeze
  on big artists). Released as **v0.4.0**. **Art confirmed by the user** (2026-10-12):
  faster here, and on the other machine "I didn't see any slow loading this time". The
  big-artist tag window not yet reported.
- **46** (2026-10-12): Bit-Perfect never starts playback; Space and fifteen Ctrl
  shortcuts (`MainWindow.Shortcuts.cs`, listed in `ViewModels/ShortcutGuide.cs` and the
  README); Escape or a click leaves the search box; Statistics lost Top Artists and its
  tooltips and gained a shortcut guide; `Services/OwnerActivation` stops another app
  jumping in front after a tag window closes. All confirmed by the user.
- **45** (2026-10-09): v0.3.0 released. The seek bar's ~200 ms lead discussed and
  left alone.
- **44** (2026-10-08): the best of several embedded covers is shown; Keep Best Cover
  run over the whole library (~9 GB freed); a new cover is saved as the only one.

Standing preferences from the user that the code doesn't show:

- **No tooltips or hint text** (2026-10-12). Keep accessible names.
- **Grey is primary; colour only in its spec role** (`design/theme-spec.md`).
- The features under *Dropped* in the next steps are dropped for good: don't build or
  suggest them.

### Deliberately not done

- **No TAK or DTS decoder.** un4seen publishes neither. Needs a third-party build or a
  libVLC fallback; `AudioEngine.CreateDecodeStream` is the single seam for that.
- No queue view (playlists came in session 38). Shuffle and repeat are done (session 5); what is still
  missing is a visible, editable queue.
- Memory was profiled in session 43 (`HISTORY.md`). Still left: FLAC tag reads load the cover
  (TagLib ignores PictureLazy for FLAC), so a big scan still peaks high before the reclaim.
- The seek bar reads ~200 ms ahead of what you hear (decode position versus device
  buffer). Invisible in practice. Discussed with the user in session 45 and left alone;
  see session 45 in `HISTORY.md` for the fix and why it waits for a feature that needs exact timing.

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
ThemeLab.exe --window playlists --w 1720 --h 1080   # hearts, a playlist, Liked before/after an unlike, an empty playlist, the name prompt (scratch playlists file)
ThemeLab.exe --window reorder --w 1720 --h 1080     # drag to reorder a playlist: moves, a block, Esc, no-ops, sorted; renders the drop line (scratch playlists file)
ThemeLab.exe --window multiedit --w 1720 --h 1080   # several artists, then albums, selected and retagged on scratch files; renders <out>-artists.png / -albums.png
ThemeLab.exe --window albumeditor --w 1720 --h 1080 # the album tag window following the list: move, Save / Don't Save / Cancel, rename, Close (scratch files); renders <out>-window.png / -prompt.png
ThemeLab.exe --window clear --w 1720 --h 1080 --qualitycache scratch\quality.json [--clearances scratch\cleared.json]   # clear an album and a song from the first Quality Check row with songs, the menus, Put Back, the Analyze buttons; renders the Analyze window (make the cache first with --window stats --qualitycache ... --qualitycheck 1 --qualitystop 75)
ThemeLab.exe --window trackeditor --w 1720 --h 1080 # the song tag window following the table, the same cases (scratch files)
```


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

Open items only; finished ones are in `HISTORY.md`.

0. **Stutters (sessions 48–49)**: two causes fixed in session 49, the post-scan memory
   clean-up and event-driven exclusive mode. Ask the user whether stutters still
   happen, in Bit-Perfect especially. If the PC's own freezes (APP STARVED, no GC) grow
   past ~80 ms, LatencyMon is the next step (the user runs it). When they give a time, read `playback.log` around it: GC PAUSE = another
   full GC froze the app (find what forced it); APP STARVED with a late fill = the PC;
   LATE FILL alone with a fast fill = the driver's wake-up was late (the 8:24 case);
   SLOW FILL with little "busy" = waiting on the file (the SD card is slower than the
   SSD); nothing logged = not ours (LatencyMon). Still available: decode ahead
   (`BassFlags.AsyncFile` read-ahead, a decode-ahead buffer), MMCSS for the feed
   thread; a bigger device buffer needs the seek-bar latency fix from session 45.
1. **Release** when the user asks: session 49's two fixes and session 50's DSF covers.
   DSF support is a feature, so v0.6.0 rather than v0.5.1. Session 51 removed the DSD passthrough item.
2. **`C:\AudioFool PNG backup\`** (42 GB): remind the user it can go once they're
   happy with the converted covers.
3. **A visible, editable queue view**: the most conspicuous missing player feature.
4. **Ask once**: edge scrolling while dragging in a playlist (session 39). Real drags
   are confirmed; edge scrolling hasn't been tried.
5. **MilkDrop 3 / projectM visualisation.** Scoped out in session 6 (LGPL-2.1, C API,
   `GLWpfControl` for OpenGL-in-WPF, no prebuilt `libprojectM.dll`, source only).
   Next step would be a spike build of `libprojectM.dll`. Nothing started.

Confirmed by the user (2026-10-14, session 50): Edit Artist Tags on a big artist opens
at once (session 47's background read).

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
