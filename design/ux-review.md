# AudioFool — UI/UX review, first pass (2026-11-22)

A review of the whole app as it stands at v0.12.0: the main window, the dialogs, the menus,
the keyboard and contrast, in both themes. Nothing has been changed; this is a list to pick
from. Each item says what's wrong in plain words, when it bites, what to do about it, and
how big the change is. Items marked **(touches a decision)** run against a call you've already
made; they're raised once, with the reason, and are yours to keep as they are.

How it was checked: ThemeLab renders of every window in Light and Dark (about 240 renders),
at the designed 1720 × 1080, the laptop screen alone (1536 × 960 at 125%) and the minimum
size; the screen-reader tree and a 30-key Tab walk; contrast ratios computed from both token
files; and the code for anything a render can't show. The music drive wasn't connected and the
library cache was October's, so content is stale; layout isn't.

Not yet covered: the real mouse (hover, drag, the press nudge), Art Search (it goes online),
the Analyze window (it opens the audio device; judged from the October screenshot), and
anything on the real screen.

---

## Status

- **Done 2026-11-24 (the fix round):** A1, A2, A3, A5, B6 (the ellipsis only), D1, D2, E2, E3,
  E5, E6, E7. What each became is in `design/progress.md` (*The UI/UX review's fix round*).
- **Left as they are, from the code:** E1 (the full height is deliberate: the answer arrives
  after the window opens, so sizing to it would make the window jump) and B6's bar (placed by an
  earlier call; beside the text it would jitter as the count grows). D8's ellipses are the
  user's call of 2026-11-20 ("Library Statistics", "Listening History", no ellipsis).
- **Open, each a call or a design round:** A4, B1, B2, B3, B4, B5, B7, C1–C4, D3–D9 (D8: only
  where Find and Replace sits), E4.

---

## The short version

The app is in good shape. The four regions read clearly, the now-playing row is unmistakable,
every icon-only control has a proper name for screen readers, Tab moves round the screen in
order with no traps, and the dialogs feel like one family. The problems are mostly of three
kinds:

1. **Places where the app says something untrue or out of date**, like a shop sign pointing
   at a door that's been moved. These are small fixes and worth doing first (A1–A5).
2. **The song title gets the least room in the table, and search scatters its results**
   (B1, B2). These are the biggest everyday improvements, and each needs a design call.
3. **Small grey things in Light are hard to read**, the artist name in the playback bar
   most of all (C1). Dark passes almost everywhere.

My recommendation: do group A, plus D1 (Enter saves) and E2 (no Minimize on blocking dialogs),
as one small round of fixes. Then take B1, B2 and C1 one at a time as design rounds.

---

## A. Where the app tells you something wrong (do first)

**A1. With the drive unplugged, the tag window shows blank tags as if they were empty.**
High. You open Edit Tags on an album while the SSD is away (a normal state): Genre, Comments,
Composer and Publisher come up blank, Save stays enabled, and Remove Leading Zeros says "None
found" though nothing was checked. It looks as if the tags are missing, and a fix you type only
fails when you press Save. Cause: files that won't open are silently dropped
(`TagEditViewModel.cs:316`, `TagReader.cs:140-156`).
*Fix:* when no file could be read, say so on the window's existing error line ("D:\Music isn't
connected: these songs can't be read or saved") and keep the detail fields and Save greyed,
as they already are while the read runs. Small.

**A2. The first-run help points to a menu that has moved, and names a button that doesn't
exist.** Medium. A new user is told three things in three wordings:
- the centre of the window: "Add a folder from the logo menu, under Libraries." Libraries has
  been under **Settings** since v0.12.0 (`EmptyStateText.cs:21`);
- the status bar: 'Use "Add Folder" to point at your music.' There is no Add Folder button
  (`MainViewModel.cs:737`, `:1388`);
- the unticked-folders line, which is already right: "Settings › Libraries in the logo menu"
  (`MainViewModel.cs:1385`).
*Fix:* one wording everywhere, naming the real path: "Logo menu › Settings › Libraries › Add
Folder…". The empty 31 px square above the centre text reads as a broken picture; consider
dropping it. Tiny.

**A3. The library dot stays green while the status line reports a problem.** Medium. The dot
before the status text is always `status.libraryDot` (`MainWindow.xaml:1690`). Double-click a
song with the drive away and the reason, "Can't open …: the drive isn't connected", sits in
plain status text beside a green light. It's like a car showing a green "all fine" lamp next
to the engine warning.
*Fix:* give the dot the theme's "off" colour (`status.lastFmOff`: yellow in Light, red in Dark)
while the line is a warning or an error, or remove the dot. Small.

**A4. In playlist mode, the only Shuffle icon shuffles the whole library.** Medium
(touches a decision). On a playlist, Shuffle artist is hidden and Shuffle library stays in the
PLAYLISTS header (spec §6.3, on purpose). Someone looking at "Road trip" who clicks the one
shuffle icon on screen expects that playlist shuffled and gets 26,000 songs
(`MainViewModel.cs:2464`).
*Fix:* in playlist mode, make it shuffle the playlist on screen, or hide it there. Small.

**A5. Network errors show raw system text and contradict themselves.** Low-medium.
Listening History: "Couldn't load your listening history. Couldn't reach Last.fm: No such host
is known." Last.fm failing: "…temporarily unavailable, please try again. It will be retried."
Neither Genius nor Listening History can retry: you close the window and press the shortcut
again (`ListeningHistoryViewModel.cs:119`).
*Fix:* one plain sentence without the system's message: "Couldn't reach Last.fm. Check the
connection and open this again." Drop the server's "please try again" where the app retries by
itself. Small.

---

## B. Space and reading in the main window (design rounds)

**B1. In album view the song title gets the least room.** High. On the laptop screen
(1536 × 960 at 125%), a Between the Buried and Me album shows the Song column 187 px wide,
"Lost Perfection: b) Anablepho…", while Album says "The Silent Circus" on all ten rows (177 px)
and Artist says "Between The…" ten times (88 px). That's two columns repeating the header
above them, while the one thing that differs per row is cut. At the minimum width Song is 86 px
("Polygon We…") and the six technical columns take 440. Song text is about a fifth of the row.
*Direction:* in album view, hide Album (it's the header's title) and give its room to Song;
show Artist only when a row's artist differs from the album's (compilations, guests). Playlists,
Recently Added and search, where those columns differ row to row, keep them. Medium; it changes
the spec's "same columns everywhere".

**B2. Search scatters its matches and shows one album's worth at a time.** Medium-high.
Searching "love" in the full library: "572 of 26,861 tracks match", across 173 artists. The
first artist is selected and the table shows one song ("1 of 11 tracks"); the other 571 are
reached by clicking artist after artist, and nothing says why an artist matched. It's like a
search engine that shows one result and makes you click through 173 folders for the rest.
*Direction:* while a search is on and no album is picked, list every matching song in the song
table, album by album, exactly as Recently Added already does (newest album first there;
library order here). Picking an artist or album narrows it, as today. Medium; reuses the
Recently Added view.

**B3. The minimum window width is 1,333 px, so AudioFool can't share the laptop screen.**
Medium; a question first. 1,333 px is 87% of the laptop's 1,536. Snapping it to half the screen
(768, or 960 on a 1920 monitor at 100%) can't work. Two limits set it, each 793 px: the table's
fixed columns, and the transport and volume under the Songs panel. At minimum the seekbar is
120 px, against the volume slider's fixed 195 (about 21 s per pixel on a 42-minute track). The
minimum is also worked out from the panels' starting widths (212/292), not their drag
minimums (140/180).
*Question:* do you want AudioFool usable at half a screen? If yes: below some width, drop the
technical columns (Kind, Bitrate, Bit Depth, Sample Rate are also in the playback bar's details
line for the playing song) and shorten the volume slider. If no, leave it.

**B4. On short windows the album header takes most of the Songs panel.** Low-medium. The
header is a fixed 189 px. At the minimum height it's 189 of 358 px, leaving four song rows;
on the laptop, maximised, 16 rows show and the last is cut through its text.
*Direction:* below some height, shrink the header art (144 → 88, the playback bar's size) or
put year, tracks and duration on one line. Small to medium.

**B5. In playlist mode the Albums panel is an empty box with a header.** Low. 292 px of
nothing under ALBUMS (spec §6.9) reads like a list that failed to load.
*Direction:* hide the ALBUMS label in that mode, so it's plainly unused. Tiny.

**B6. The scan's progress bar sits 800 px from its text.** Low. The bar is beside the Last.fm
chip; "Scanning… N of M files" is at the far left. The text also alternates "…" and "..."
(`MainViewModel.cs:1000`, `:1015`).
*Direction:* put the bar right after the text; one kind of ellipsis. Tiny.

**B7. Paused and playing look the same in the song table.** Low. The outlined triangle
differs from the solid one by a 2–3 px hole in an 8 × 8 box (38 against 46 ink pixels). The
playback bar carries the state, so this may be fine as it is.

---

## C. Contrast and legibility

Dark passes for all text. Light has the failures below (WCAG: 4.5:1 for text under 18.7 px
bold, 3:1 for icons and focus rings). Ratios are computed from the token files.

**C1. Light: the playback bar's and album header's artist name is 3.17:1.** High
(touches a decision). It's 14 px semibold in `accent.playback` (#15786C) on the panel grey,
which needs 4.5:1; the old blue was 4.72. Since covers are fitted "that or darker", 3.17 is the
worst case and most covers are darker. HANDOFF already notes it reads "a little harder" since
2026-11-12. In Dark it's 5.32.
*Direction:* in Light only, draw the name (text) in `accent.playbackText` (#0C5A51, 4.80:1),
the same hue a step darker, and keep the exact seekbar colour for the graphics. The name still
reads as "the seekbar's colour". With a cover, fit the name to `accent.playbackText` instead of
`accent.playback`. Small.

**C2. Light: grey icons with no face are under 3:1.** Low-medium (partly touches a
decision). The speaker (Mute) and the unliked heart use `control.iconOff`: 2.84:1 on a panel,
2.39:1 on a selected row. The Maximize and Close glyphs are 2.53 and 2.40:1; the spec's stated
exception covers only the yellow Minimize.
*Direction:* `text.muted` (5.14:1) for the speaker and the heart outline, as the shuffle icons
in the panel headers already use. Leave the window buttons as a stated exception, or note them
in §8.

**C3. Dark: a hovered row looks almost like a selected one.** Low-medium. White at 4% over the
panel gives #23252B; the selection is #252A34, so 1.06:1 apart. Moving the mouse down Artists in
Dark, two rows look selected. In Light, hover and selection are 1.10:1 apart; the selection
itself is only 1.19:1 off the panel in either theme (with no focus outline on rows, by
decision, that tint is all a keyboard user has).
*Direction:* lower Dark's `state.hoverOverlay` to about 2–2.5%. Tiny (one token).

**C4. Light dialogs: muted text on the window grey is 4.34:1.** Low. The spec's ratios were
worked out on the panel grey; dialog text sits on the darker window grey. It affects the tag
window's cover note, Last.fm's scrobble rules, "None found" and Listening History's unselected
periods. Empty text fields are faint too (border 1.27:1).
*Direction:* `text.secondary` (5.57:1) for body text directly on the window grey. Small.

Other Light figures, for the record: the search placeholder 4.26, the repeat-one "1" badge
4.17, the now-playing row under hover 4.22, the now-playing triangle 2.99, the library dot
2.67, the yellow "off" dots 1.28 on their chips (the ring brings it to 1.40), disabled menu text
2.34 (exempt, but Liked's greyed Rename…/Delete… are hard to read).

---

## D. Keyboard and discoverability

**D1. Enter doesn't save in the tag window or connect in Last.fm.** Low-medium. Save
(`TagEditWindow.xaml:301`) and Connect have no default-button setting; the prompts do. You type
a genre, press Enter, and nothing happens. There's no Ctrl+S in the tag window either, though
Mp3tag has trained exactly that.
*Fix:* make Save and Connect the default buttons (the Comments box keeps Enter for new lines);
Ctrl+S saves in the tag window. Tiny.

**D2. Shortcuts aren't shown on the menu items they run.** Medium. No menu item in the app
shows its shortcut: Library Statistics (Ctrl+W), Listening History (Ctrl+Y), Find and Replace…
(Ctrl+H), Analyze… (Ctrl+Q), Genius… (Ctrl+G). The only list is the guide at the bottom right
of Statistics.
*Fix:* the standard right-aligned shortcut text on those items (menu text, not a tooltip).
Small.

**D3. Ctrl+B switches Bit-Perfect from anywhere, even the search box, with no confirmation.**
Medium; a question. Bit-Perfect plays at full scale and ignores the volume slider. If you
listen at 30% in AudioFool and brush Ctrl+B (B sits between V and N), the next sound is full
scale through your amp. Not tried on a device.
*Question:* keep Ctrl+B? The chip is one click away, and nothing else in the app has a
full-volume consequence on one key.

**D4. A few shortcuts clash with Windows habits.** Low-medium. Ctrl+S (Save everywhere else)
toggles Shuffle; Ctrl+W opens Statistics but doesn't close it (while Ctrl+D both opens and
closes the art viewer); Ctrl+E (search in Explorer and browsers) cycles Repeat; Ctrl+R repeats
F5. Space on a focused button plays or pauses rather than pressing it, so keyboard users must
know to use Enter.
*Direction:* the cheap, useful ones: Ctrl+W closes Statistics again, and drop Ctrl+R. Moving
Ctrl+S is your call; it matches Spotify.

**D5. Liking is mouse-only for any song but the playing one, and a screen reader can't tell
liked from not.** Medium. Ctrl+L likes only the playing song, while Ctrl+Q and Ctrl+G act on
"the selected song, or the playing one". The heart can't take focus, its name is always
"Like", and it has no on/off state (`LikeToggle.cs:13`, `Chrome.xaml:1229`).
*Fix:* give Ctrl+L the Q/G rule; give the heart a liked/not-liked state for screen readers.
Small.

**D6. Things that look alike behave differently.** Low-medium.
- The three panel headers look the same: ARTISTS cycles the sort, PLAYLISTS goes back to
  Artists, ALBUMS does nothing.
- Right-click on the album header's art opens Edit Album Tags at once; right-click everywhere
  else (rows, the playlist header's picture) opens a menu.
- The EQ and Bit-Perfect chips look alike: a click on EQ opens a menu, a click on Bit-Perfect
  toggles. EQ's role is a plain button, so a screen reader doesn't say it opens a menu.

*Direction:* remove the PLAYLISTS-header click (the Playlists button does it) and the art's
instant right-click (the album row's menu has Edit Album Tags…). Tiny.

**D7. Features you'd plausibly never find.** Low, for awareness. Fit the columns
(double-click the # | Song divider); restore a playlist's order (click #); Keep Best Cover or
Convert PNG for the whole library (Ctrl+A in Artists, then right-click, with a Statistics filter
on); the EQ chip's double-click. Findable enough: the ARTISTS sort (hand pointer), the art
viewer, the now-playing title's jump, the second-click edit, type-ahead. With no tooltips by
design, the README is the only map. Worth a line each in Statistics' shortcut guide.

**D8. Settings holds Find and Replace, which isn't a setting; ellipses are uneven.** Low
(touches the 2026-11-19 call). Someone looking for batch tag edits won't look under Settings,
and none of the real settings (theme, EQ, Bit-Perfect) live there. Library Statistics and
Listening History open a window with no "…"; Analyze… and Genius… do the same with one.
*Direction:* Find and Replace… back at the top level beside the other tools; either all four
window-openers get "…" or none do (Windows' rule is "…" only when more input is needed, so none).
Tiny.

**D9. Check for updates is mouse-only, and its icon is Refresh library's.** Low. The About
row's arrow can't take focus (`Chrome.xaml:1211`) and is the same circular arrow as the status
bar's rescan.
*Direction:* a different glyph (a down-arrow into a tray), or make it an About menu item
"Check for Updates…". Small.

---

## E. Dialogs

**E1. Genius and Listening History open at full screen height for one line.** Medium.
Loading, offline, not connected, no match: one line at the top, Close at the very bottom,
hundreds of pixels of nothing between (`GeniusWindow.xaml.cs:35`,
`ListeningHistoryWindow.xaml.cs:29` set Height to the work area). Statistics gets this right
with MaxHeight (`StatisticsWindow.xaml.cs:28`).
*Fix:* size to content up to the screen's height, as Statistics. Small.

**E2. Blocking dialogs have Minimize and Maximize.** Medium-low. Statistics, Listening
History, Find and Replace and the several-songs tag window block the main window while open,
yet show Minimize and Maximize. Minimize one and the main window ignores you until you find the
dialog on the taskbar, so the app looks frozen. Maximize stretches the 460 px tag form across
the screen. From the code; not tried on screen.
*Fix:* hide both, as Last.fm and the prompts already do (`ShowMinimize="False"
ShowMaximize="False"`). Tiny.

**E3. Last.fm shows the shared secret in plain text.** Low. You work from screenshots; a
screenshot of this window carries the secret (`LastFmWindow.xaml:79`).
*Fix:* mask the secret box. Small.

**E4. Statistics' two columns end far apart.** Low-medium. The left column ends at 1,029 px,
the right at 2,223 (with empty check sections; real results lengthen the left a little). Below
the first screen half the width is blank, and Tracks by Decade and Keyboard Shortcuts are only
reached by scrolling, with no scrollbar to hint at them.
*Direction:* move Keyboard Shortcuts (about 650 px) to the left column. Small.

**E5. Find and Replace's "No matches." is far from where you look.** Low. It's small text at
the bottom left, 530 px below the Find box, with an empty table between.
*Fix:* show it inside the empty table, as the main window's empty states do. Tiny.

**E6. Genius's closest results don't look clickable.** Low. Under "Genius has no page that
matches this song", the rows under CLOSEST ON GENIUS are picks, but at rest they're plain text.
*Fix:* say so in the line: "Genius has no page that matches this song. Pick one of the closest
below if it's right." Tiny.

**E7. The art viewer's caption repeats its title.** Low. "Artist · Album" in the title bar
and again under the picture (`ArtWindow.xaml.cs:21`, `:24`).
*Fix:* caption with only the size and format. Tiny.

---

## Keep as they are

- The four regions read at a glance; the transport lines up with the Songs panel.
- The now-playing row: hue, semibold text and triangle, track number kept. Clear in both
  themes.
- Scope is always said: "429 of 19,249 tracks match "love" · FLAC", "1 of 11 tracks", "9 of 13
  tracks".
- Empty states fit the moment: No music yet, No matches, Nothing added in the last 30 days.
- Every icon-only control has a name; Shuffle, Mute, Bit-Perfect and Playlists announce on/off;
  Repeat, Theme and Equalizer carry their state in the name.
- One Tab loop that follows the screen, no traps; the focus ring is 4–10:1 in both themes.
- Escape is Cancel everywhere, and the tag window asks Save / Don't Save / Cancel in Windows'
  order before Escape loses typing.
- Statistics, Listening History and Genius share tiles, headers and bar rows: one family.
- Analyze's plain-words verdict under the spectrogram.

---

## Suggested order

1. **Fix round (no design calls):** A1, A2, A3, A5, D1, D2, E1, E2, E3, E5, E6, E7, B6. All
   small; each is a wording, a flag or a size.
2. **Your calls, one at a time:** C1 (the Light artist name), A4 (Shuffle in playlist mode),
   D3 (Ctrl+B), D6/D8 (header clicks, Settings).
3. **Design rounds:** B2 (search results in the table), then B1 (album-view columns), then
   B3/B4 if you want AudioFool at half a screen.
4. **Not covered yet:** a pass on the real screen with a real mouse (hover, drag, the press
   nudge, the close X), and Art Search and Analyze rendered.
