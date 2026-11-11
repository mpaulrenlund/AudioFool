# Verifying changes

How to check UI and drive changes without touching the user's desktop: UI Automation and PrintWindow, the theme before the window, ThemeLab, portable-drive tests. Moved out of HANDOFF.md on 2026-11-11, word for word.

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
geometry in `docs/architecture.md` was established, and it needs no screenshot at all.

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
under Gotchas in `docs/traps.md`.

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

---

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
ThemeLab.exe --window coveraccent --w 1720 --h 1080 [--theme dark]   # the playback bar in the cover's colour: made-up covers (deep red, yellow, a landscape, a wide and a tall one, black and white) and none; every element's colour read back against the fitted one, the untouched ones against the theme's, a switch to the other theme and back; paused and nothing loaded, no glow (only while playing; the arcs round the button change in Dark, not in Light); renders <out>-<cover>.png, -switched.png and -paused.png; "ALL OK" (session 77)
ThemeLab.exe --window headeraccent --w 1720 --h 1080 [--theme dark] [--realalbums 60] [--realalbum "Shirobon/PSY2K"]   # the album header's artist name in its own cover's colour: the same made-up covers on the sample header, the other lines and the bar untouched, same cover = same colour as the bar, a switch and back; --realalbums: real albums (read only) selected as a click would, header colour vs the bar's for the first song; --realalbum renders one, <out>-real.png; "ALL OK" (session 77)
ThemeLab.exe --window statusclick --w 1300 --h 700 [--theme dark]   # the status line and dot: a single click's gesture opens Statistics (not run: it is modal), hand pointer, underline on hover; renders <out>-plain/-hover.png; "ALL OK" (session 68)
ThemeLab.exe --window dialogplacement   # a hidden main window on each monitor, maximised and normal, and a hidden dialog placed by DialogPlacement: centred on its monitor, kept on it; never shown (session 71)
ThemeLab.exe --window playfailure   # the real engine and BASS on a missing file, an unused drive letter, text named .flac and a file this process holds: each PlayFailure and its line; nothing reaches the device (session 71)
ThemeLab.exe --window nowplayingjump   # Ctrl+J over the real library.json (read only): its album, the song selected, a search/filter cleared only when it hides the song (session 71)
ThemeLab.exe --window statusfilter [--theme dark] [--dpi 1.25]   # the status bar's ×, over the real library.json (read only): a filter (hidden/shown, right after the text, trimmed long line, hover, a click clears and restores the totals), then one-off messages (scan counts, a save; none while scanning or saving, none on a search's line); renders <out>-off/-on/-saved/-long/-hover/-scan.png; "ALL OK" (session 71)
ThemeLab.exe --window queueend [--theme dark]   # the end of a queue through the real engine (shared, 5%, silent WAVs, a blue cover.jpg) and the view model: the bar back on the first song at 0:00, its cover, stopped, device let go; Play starts from it; shuffle; a one-song queue; renders <out>-end.png; "ALL OK" (session 72). Run with Start-Process -Wait -RedirectStandardOutput to see its lines
ThemeLab.exe --window listening --w 900 [--state loaded|loading|offline|disconnected] [--theme dark] [--dpi 1.25]   # Listening History over the real library.json (read only), Last.fm answers made up from it (nothing sent): tiles, rows, which can be clicked, a period switch and back (no refetch), a failed period, clicks applied to the main window; renders <out>-week/-periodfail/-again.png; "ALL OK" (session 74)
ThemeLab.exe --window genius --w 760 [--theme dark] [--dpi 1.25]   # the Genius window over a made-up Genius (nothing sent): every state, Show N more, a cached reopen, the nearest results and a choice, a part missing, offline, waiting, a new song dropping the old answer; Ctrl+G and the song menu; renders <out>-full/-nomatch/-chosen/-sparse/-empty/-missing/-notitle/-offline/-loading.png; "ALL OK" (session 75)
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
