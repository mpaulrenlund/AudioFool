# AudioFool — session handoff

Updated 2026-11-11 (docs restructure; the last app session was 77). Read this alongside
`README.md`: the README covers *how the app works*, this covers *where things stand and
what is open*. How-to and reference notes are in `docs/` (index at the end); read a file
only when its line says so. What each past session changed, and why, is in
**`HISTORY.md`** (oldest first); look there when you need the story behind a piece of
code, not at the start of every session.

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

## Build and install

`tools\ship.ps1` builds in Release, runs the tests, publishes, and installs over the
installed copy; `-NoInstall` stops before installing, and `-InstallOnly` installs what is
already in `publish` without rebuilding. Ask before closing a running
AudioFool to install (it stops the music). Releases: the `/release` skill,
`.claude/skills/release/SKILL.md`. The manual commands, and the traps behind each step:
`docs/build-and-release.md`.

## Version control

`C:\MusicPlayer` is a git repository on branch `main` (no feature branches), with a
**private** GitHub remote `origin`; `git push origin main` works with no setup. Commit and
push only when the user asks, and report the result in a line. Write the message to a file
and use `git commit -F <file>`: a double quote in a PowerShell here-string breaks the
commit. More (vendored DLLs, `eol=lf`, ignores): `docs/build-and-release.md`.

---

## State at handoff

- **1,126 tests** pass. **Last release: v0.11.0** (2026-11-10, session 77, tag on
  `2c1fcdf`): everything through session 77 is built, installed, committed and pushed.
  Nothing unreleased. A fix from the items below goes out as 0.11.1 (last number: fixes
  only), version and notes confirmed with the user first.
- **The library cache is version 4** (session 76: `Track.LeadingZeros`). The first start
  after installing re-reads every song's tags in the background, once.
- An install is checked by hashing the files, not by launching AudioFool from Claude's
  shell (*A launch from Claude's shell writes playback.log* in `docs/traps.md`).
- The full notes of sessions 66–77 are in `HISTORY.md`; the handoff's old session list
  is there too, under *Retired from HANDOFF.md, 2026-11-11*.

**Built and installed, not yet seen by the user** (what to try: next step 2):

- **Session 77** (committed `0b9c505`): the album header's artist name in its own cover's
  colour; Dark's Play/Pause glow only while playing. The playback bar's cover colour
  itself the user has seen.
- **Session 76**: Statistics → ARTIST VS ALBUM ARTIST (committed `80f8a6b`) and LEADING
  ZEROS (committed `5f34ead`, with the one-time re-read).
- **Session 75**: the Genius window (committed `89926f0` + `dc0b6a2`).
- Older items checked off-screen only, from sessions 71, 68, 67, 61, 60, 59 and 39.

**Waiting on the user:** how the Genius window went (step 1); whether the close X has
ignored a click since the safety net (step 3).

**Known open bugs:** the close X that sometimes ignores clicks (step 3; a safety net
covers it); the small items under step 5.

**Audio pops** were traced to the work laptop (sessions 55–57), not AudioFool. Closed;
don't raise it. If they are reported again, read *If audio pops are reported again* in
`HISTORY.md` (under *Retired from HANDOFF.md, 2026-11-11*).

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

---

## Suggested next steps

Open items only; finished ones are in `HISTORY.md`. Open a session by asking the user
about step 1 (ask; don't re-explain); then ask about step 2, then step 3, then which of
steps 4 and 5 to take.

1. **Ask how the Genius window went** (session 75; installed, never seen by the user).
   Right-click a song → Genius…, or Ctrl+G. Worth hearing about: a song it matched to
   the wrong page, or missed though Genius has it (the fix is in `GeniusMatch`, see
   *The Genius window* in `docs/architecture.md`); how the sections read; "Show 50 more"
   on a song with many comments; the closest results offered for a soundtrack or
   instrumental. A design note from the user means the usual plan-then-approve loop.
2. **Ask the user to try what was checked off-screen only:**
   - **Session 77:** the album header's artist name in its cover's colour (browse a few
     albums, and a theme switch), and Dark's Play/Pause glow going on pause. The bar's
     cover colour itself the user has seen (their screenshot that asked for the header).
   - **Session 76:** Statistics → ARTIST VS ALBUM ARTIST, a click on a row, and a
     fixed track leaving the filter. LEADING ZEROS: the one-time re-read on the first
     start ("Updating the library for this version…", about a minute), then the row,
     and an album leaving it after Edit Album Tags → Remove Leading Zeros.
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
3. **The close X that sometimes ignores clicks** (sessions 51, 59 and 66). In tag
   windows and Statistics the X sometimes ignored **several clicks in a row**. Since
   session 66 `Services/CloseButtonSafetyNet` clicks the X when WPF-UI loses a click, and
   the click log records every hit test between a press on the X and its release (*The
   close X: safety net and click log* in `docs/architecture.md`). The user: "It's working
   well so far."
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
4. **A visible, editable queue view**: the most conspicuous missing player feature.
5. **Smaller open items** (session 60's review is otherwise all fixed):
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
signing. The visualizer was removed in session 65 (the user may build one of their own
later); don't suggest projectM again. Its groundwork in Core is kept; the notes are in
`HISTORY.md` (*The visualizer and the projectM trial*, and the old step 7 under *Retired
from HANDOFF.md, 2026-11-11*).

---

## Other docs: read when...

- **`docs/verifying.md`**: before any visual check or UI test (UI Automation and
  `PrintWindow`, ThemeLab, theme-before-window, testing a portable drive with `subst`).
- **`docs/traps.md`**: before running shell commands that build, test or touch AppData
  (Claude's AppData redirect, PowerShell traps, WPF gotchas that cost real time).
- **`docs/architecture.md`**: before changing playback, the library cache, tagging,
  themes, the title bar, single-instance start-up or the Genius window.
- **`docs/build-and-release.md`**: if `tools\ship.ps1` fails, for the manual build
  commands, or for a release (with the `/release` skill).
- **`HISTORY.md`**: the story behind a piece of code, finished items, and reference notes
  retired from this file.
