# Architecture notes

How the trickier parts of AudioFool work (library filtering, themes, shuffle and repeat, tag editing, title bar, one copy, the close X, the Genius window). Moved out of HANDOFF.md on 2026-11-11, word for word.

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
  window is built (see *Apply the theme before building the window* in `docs/verifying.md`). It swaps WPF-UI's
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
  on its root (see *A dialog has no shell* under Gotchas in `docs/traps.md`).
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
  to test it: it would show on the user's screen and read stale AppData (see *Claude's shells and AppData* in `docs/traps.md`).

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

### The Genius window (session 75)
- **Where:** `Core/Genius/` (`GeniusClient`, `GeniusJson`, `GeniusMatch`,
  `GeniusLookup`, records in `GeniusModels`), `GeniusWindow` + `GeniusViewModel`,
  `Formatting/LinkedText` (web addresses as links), `MainViewModel.ShowGenius` (one
  window, reused). Tests in `GeniusTests`; ThemeLab `--window genius`.
- **It reads genius.com's own web API** (`https://genius.com/api/`, the one its pages
  are built from): no key, and it has comments and Q&A. It is **undocumented**, so if
  the window starts saying "Couldn't load the Genius page", look there first: compare a
  live answer with what `GeniusJson` expects. One quirk already found live: a
  question's text comes as a bare string while everything else is `{"plain": …}`.
- **The fallback** if Genius closes it: the documented API (`api.genius.com`), which
  needs a client access token and has **no comments and no Q&A**. The user has made
  Genius API credentials and kept them (2026-11-04); ask for the client access token
  only then, and store it like the Last.fm secret (DPAPI in settings), never in the repo.
- **Matching** is the part to tune if the user reports a wrong or missed page:
  `GeniusMatch.ExtraWords` (what counts as an extra in a trailing bracket or " - "
  tail), artist keys (any artist of a joint credit, "The" optional). A live check is a
  small console app on `AudioFool.Core` that samples library.json and runs
  `GeniusLookup.FindAsync` gently (400 ms apart); session 75's run: 32 of 50 matched,
  all right, the rest soundtracks and instrumentals Genius doesn't have. The probe
  wasn't kept.
