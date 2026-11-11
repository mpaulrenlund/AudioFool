# Traps

Claude's shells and AppData, and the gotchas that cost real time. Moved out of HANDOFF.md on 2026-11-11, word for word.

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

---

## Gotchas that cost real time

- **WPF-UI clips a top-level menu header to 20 px tall.** Putting a 30 px image in a
  `MenuItem.Header` gives you a silently cropped image: the element reports
  `ActualHeight` 30 and WPF hangs a layout clip of `0,5,39,20` on it, so 5 px vanish off
  the top and the bottom. `Padding` on the `MenuItem` is ignored as well. Nothing errors,
  and at title-bar size the crop reads as "the logo looks a bit squat" rather than as a
  bug — the user spotted it before I did. Two lessons: draw over the item instead of
  filling its header (see *Title bar layout* in `docs/architecture.md`), and when checking whether an image renders
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
