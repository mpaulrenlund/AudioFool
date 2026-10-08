# AudioFool

> New session? Read [HANDOFF.md](HANDOFF.md) first — it covers current state, the
> build-and-install loop, and the traps worth not rediscovering.

A lightweight Windows 11 music player built around one idea: **Artist → Album → Song**,
with nothing else in the way.

- Artists sort alphabetically, with a leading "The" ignored — *The Beatles* files under **B**.
- Albums sort oldest at the top, newest at the bottom - by full release date where the
  files carry one, so two albums from the same year still come out in release order.
  An album tagged with only a year sorts at the start of that year.
- Songs sort by disc number, then track number.
- One look, **PS1**: the light grey of the original PlayStation console. Lighter
  panels (`#CAC7C3`) sit on a slightly darker window (`#BAB7B3`), with 1 px outlines
  and squarish 4 px corners. The round transport buttons echo a controller's face
  buttons. Colour is rare and always means something:
  - **Teal is playback:** Play/Pause, the seek bar's waveform and the playing song's
    row.
  - **Blue is artist names.**
  - **Shuffle and Repeat when on:** Shuffle turns blue and Repeat red.
  - **Status chips:** a yellow dot means off, a teal one means on.

  Selection is a mid-grey row with no outline, and lists scroll without visible
  scrollbars. The design is specified in `design/` (spec, tokens and mockup).
- A waveform in the seek bar: the playing track's loud and quiet parts, teal up to where
  you are and grey after, moving smoothly while it plays. It's read in the background
  when the track starts (well under a second for most files); until then the bar is plain.
- Gapless playback.
- [Last.fm scrobbling](#lastfm-scrobbling), with plays kept offline until they can be sent.
- Optional [bit-perfect output](#output-modes) — exclusive WASAPI at the source's own
  sample rate, with DSD-over-PCM passthrough for DACs that support it.

---

## Prerequisites

| What | Why | Install |
|---|---|---|
| **.NET 10 SDK** | Builds the app | `winget install Microsoft.DotNet.SDK.10` |
| **BASS libraries** (x64) | Audio decoding and output | See [one-time setup](#one-time-setup-bass) |

Visual Studio is **not** required. The `dotnet` CLI is enough. If you'd like an IDE,
Visual Studio 2022+ (free Community edition) or VS Code with the C# Dev Kit both work.

---

## One-time setup: BASS

Playback uses [BASS](https://www.un4seen.com/) from un4seen. The NuGet packages
(`ManagedBass.*`) contain only the C# bindings — the actual native DLLs can't be
redistributed through NuGet, so they have to be fetched once by hand.

**They are already in place** at `lib/bass/x64/`. If you ever need to redo it:

1. Download these from <https://www.un4seen.com/bass.html>:

   | Zip | Gives you |
   |---|---|
   | `bass24.zip` | Core: MP3, Ogg Vorbis, WAV, AIFF, MOD/S3M/XM/IT |
   | `bassmix24.zip` | The mixer that makes gapless playback possible |
   | `bassflac24.zip` | FLAC |
   | `bassdsd24.zip` | DSD (`.dsf`, `.dff`) |
   | `basswasapi24.zip` | WASAPI output (needed later for native DSD / bit-perfect) |

2. From each zip, take the DLL out of the **`x64/`** folder — not the one at the root,
   which is 32-bit — and drop it into `lib/bass/x64/`.

The `.csproj` copies everything in that folder next to the executable automatically,
so no code changes are needed when you add an add-on.

> **Licence.** BASS is free for non-commercial use. If AudioFool ever became a product
> you sold, un4seen would want a licence fee. Not an issue for personal use.

### Format coverage

All of these are installed and loading. `BassRuntime` reports any that go missing.

| Format | Provided by |
|---|---|
| MP3, Ogg Vorbis, WAV, AIFF, MOD/S3M/XM/IT | `bass.dll` (core) |
| FLAC | `bassflac.dll` |
| DSD (`.dsf`, `.dff`) | `bassdsd.dll` |
| Opus | `bassopus.dll` |
| AAC / M4A | `bass_aac.dll` |
| ALAC (Apple Lossless) | `bassalac.dll` |
| WMA | `basswma.dll` |
| APE (Monkey's Audio) | `bassape.dll` |
| WavPack | `basswv.dll` |
| Musepack | `bass_mpc.dll` |
| AC-3 | `bass_ac3.dll` |

A missing add-on is never fatal — the app starts, names what's unavailable in the
status bar, and refuses only the files it genuinely can't decode.

**Two formats from the original wish list are not covered: TAK and DTS.** un4seen
publishes no add-on for either. The options are a third-party build
([BASS_DTS](https://github.com/pudding-fox/BASS_DTS),
[BASS_FFMPEG](https://github.com/pudding-fox/BASS_FFMPEG) which covers a wide range
via FFmpeg), or wiring LibVLCSharp in as a fallback decoder for anything BASS
refuses to open. `AudioEngine.CreateDecodeStream` is the single seam where that
would slot in.

Note the third-party add-ons live under `un4seen.com/files/z/2/...` rather than
`files/...` — worth knowing if a download 404s.

---

## Build, test, run

```bash
dotnet build AudioFool.slnx
```

```bash
dotnet test tests/AudioFool.Core.Tests/AudioFool.Core.Tests.csproj
```

```bash
dotnet run --project src/AudioFool/AudioFool.csproj
```

---

## Creating an .exe

```bash
dotnet publish src/AudioFool/AudioFool.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -o publish
```

That produces `publish/AudioFool.exe` (~61 MB) alongside a handful of DLLs that
**must stay in the same folder**:

- `bass.dll`, `bassmix.dll`, `bassflac.dll`, `bassdsd.dll`, `basswasapi.dll` — the audio engine
- `wpfgfx_cor3.dll`, `PresentationNative_cor3.dll`, `D3DCompiler_47_cor3.dll`, `PenImc_cor3.dll`, `vcruntime140_cor3.dll` — WPF's own native bits, which .NET cannot bundle into a single file

**Self-contained** means the target PC needs no .NET installed. If you'd rather have a
~2 MB executable and don't mind requiring the
[.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0), swap
`--self-contained true` for `--self-contained false`.

Add `-p:DebugType=none` to skip the `.pdb` files in a distribution build.

### SmartScreen

The executable is unsigned, so the first launch on another machine shows
"Windows protected your PC" → **More info** → **Run anyway**. Getting rid of that
requires an Authenticode code-signing certificate (a few hundred dollars a year),
which is rarely worth it for a personal project.

---

## Project layout

```
AudioFool.slnx
├── src/AudioFool.Core/          class library, no UI dependencies
│   ├── Models/                  Track, Album, ArtistGroup
│   ├── Library/                 SortRules, TagReader, LibraryScanner, LibraryCache,
│   │                            LibrarySearch, LibraryRelocator
│   ├── Playback/                BassRuntime, AudioEngine, OutputChain
│   └── Settings/                AppSettings (JSON in %APPDATA%\AudioFool)
├── src/AudioFool/               the WPF app
│   ├── ViewModels/              MainViewModel, AlbumItemViewModel
│   ├── Services/                AlbumArtService, GlobalHotkeys, TaskbarControls
│   ├── Formatting/              column display + converters
│   └── MainWindow, ArtWindow    the two windows
├── tests/AudioFool.Core.Tests/  xUnit tests: sorting, cache, search, drives
└── lib/bass/x64/                native BASS DLLs (see above)
```

The sorting rules live in `SortRules.cs` with no UI or file-system dependencies,
which is why they can be tested directly — see `SortRulesTests.cs`.

---

## Keeping the library on a portable drive

Fully supported, including the two things that normally break a music player on
removable storage.

### Moving the library

1. Copy your music to the drive. Use a method that **preserves timestamps** -
   `robocopy /MIR`, or Explorer drag-and-drop, both do. That lets the cached tags be
   reused after the move instead of every file being re-read.
2. In AudioFool, **Add folder** and point at the new location.
3. Confirm the track count matches, and play something.
4. Only then remove the old copy.

The first scan of a new location reads every tag, which takes longer over USB than
off an internal disk - budget a couple of minutes for a large library. It happens
once; after that startup is back to a fraction of a second.

> AudioFool's cache stores file paths and tags, not audio. **It is not a backup.**
> Verify the copy plays before deleting anything.

### When the drive letter changes

Windows assigns letters in the order things are plugged in, so a drive that is `E:`
today can be `F:` tomorrow. Every cached path would then point nowhere and the
library would look empty.

On startup, any configured folder that has gone missing is looked for under the
other drive letters. A candidate is only accepted once files the cache knows about
are confirmed present there - so it can't latch onto an unrelated drive that happens
to have a folder of the same name. When it matches, the folder setting and all cached
paths are re-pointed and **no tags are re-read**.

Verified end to end with `subst`: a library scanned on `X:`, disconnected, then
brought back as `Y:` was re-pointed with zero tags re-read.

### When the drive isn't connected

The library stays browsable, showing what the last scan found, and the status bar
says which folder is unavailable. Playing a track reports that the file can't be
reached rather than failing silently.

Critically, **a disconnected drive is never mistaken for a deleted library**. Tracks
on an unreachable folder are carried over untouched instead of being reconciled, so
the cache survives intact and reconnecting costs nothing. This was a real bug during
development - an absent folder was silently skipped, every track was reported
removed, and the cache was overwritten with nothing. There are now tests for both
halves of the distinction: an *unreachable* folder preserves its tracks, while a
folder that is present and genuinely empty still reports them as removed.

### Playback

Nothing special is needed. BASS reads through ordinary Windows file I/O, so any drive
letter works the same. Verified playing FLAC from a mounted drive letter in exclusive
bit-perfect mode.

If the drive is pulled *during* playback, the current track stops. Reconnect and
press play again.

---

## Keyboard shortcuts

**Global** — these work from any application, whether AudioFool has focus or not:

| Key | Does |
|---|---|
| **F9** | Play / pause |
| **F10** | Previous track, or restart the current one if more than 3 seconds in |
| **F11** | Next track |

F10's split behaviour is the convention every player has trained us to expect: press
it early to go back a track, press it mid-song to start that song again.

> **These keys stop working in other applications while AudioFool is running.**
> Windows grants a hotkey exclusively to whoever registers it first, so F11 will no
> longer toggle fullscreen in a browser, and F10 won't open a menu bar. That is how
> `RegisterHotKey` works, not a choice made here.
>
> To turn them off, set `"GlobalHotkeys": false` in
> `%APPDATA%\AudioFool\settings.json` and restart. There is deliberately no UI toggle
> — taking keys away from every other app shouldn't be one stray click away.

If another application already owns one of the keys, AudioFool cannot claim it. It
says so in the status bar on startup rather than leaving you pressing a key that
does nothing.

Auto-repeat is suppressed, so holding F11 skips one track rather than tearing through
the album.

### Window shortcuts

These only apply when AudioFool has focus:

| Key | Does |
|---|---|
| Media keys | Play/pause, next, previous, stop |
| **F5** | Rescan the library |
| **Enter** | Play the selected track |
| **F2** | Edit the selected track's #, Song, Artist or Album in place (the cell you last clicked). A second single click on the selected row does the same. **Enter** saves, **Escape** cancels |
| **Escape** | Close the album-art viewer |

### Taskbar controls

Hover AudioFool's taskbar icon and the thumbnail carries **Previous, Play/Pause, Next
and Stop** buttons. Play/Pause shows ▶ when nothing is playing and ⏸ while something
is. The glyphs are white on a dark taskbar and black on a light one. The thumbnail's
heading — and the taskbar tooltip and Alt-Tab — read "Artist – Title" while a track
is loaded, "AudioFool" otherwise.

### Fitting the track columns

Double-click the divider between **#** and **Song** to fit every column to what is
on screen at once. Double-clicking any other divider fits just the column to its left,
which is what a data grid normally does.

The fit is not a plain measure-and-set. Time, Disc, Kind, Bitrate, Bit Depth and
Sample Rate are sized first and always get their full width — their headers are wider
than their values, so clipping them loses a word rather than a character. Whatever is
left over goes to **Song**, then **Artist**, then **Album**, in that order, with each
keeping a minimum so it never collapses. Song ends up absorbing the slack, so it keeps
growing and shrinking with the window afterwards.

---

## Play order

Two small buttons sit at the right end of the transport controls in the now-playing
bar, stacked: **repeat** on top, **shuffle** below. Both light up in the accent colour
when active (repeat shows a small "1" on its corner for "this track"), and both are
remembered between sessions.

| Control | States |
|---|---|
| Shuffle | Off, or on |
| Repeat | Off → whole queue → this track → off (the button cycles) |

Shuffle keeps the queue itself in album order and plays it through a separate order,
which has three consequences worth knowing:

- **Turning shuffle on never interrupts what is playing.** The current track is pinned
  to the front of the new order and everything else falls in behind it.
- **Turning shuffle off restores the album running order**, again without interrupting
  the current track.
- **Previous walks back through what you actually heard**, not through the album order.

Shuffle covers the whole queue exactly once before stopping — or before wrapping, if
repeat is set to the whole queue. It will not play the same track twice in a pass.

---

## Likes and playlists

The last column of the song list is a heart. Click it to **like** a song, which adds it
to the end of the **Liked** playlist; click again to unlike it. A liked heart is filled
pink.

The small list button at the right of the **ARTISTS** header switches that panel to
**PLAYLISTS**: **Liked** first, **Recently Added** second, then the rest most recently
changed first; click it again, or click the PLAYLISTS label, to go back. While playlists
show, the Albums panel is empty and the song list shows the chosen playlist, in its
order, under a header with its picture, name, when it was last changed, how many
tracks and how long. **#** is the song's place in the playlist. Double-click a
playlist to play it from the top.

- **Add songs** by right-clicking them (one or a selection) and choosing **Add to
  Playlist**, which also offers **New Playlist…**. A song already in a playlist isn't
  added twice.
- **Remove songs** with **Remove from Playlist** on the same menu, while a playlist
  shows. The files are untouched.
- **Reorder songs** by dragging them: press a row (or one of several selected) and
  drag, and a line shows where they'll land. Hold near the top or bottom edge to
  scroll; Esc cancels. This works in the playlist's own order: if you've sorted by
  another column, click **#** first. A song already playing keeps the order it started
  with; the new order applies the next time you start a song.
- **Rename, delete or give a playlist a picture** by right-clicking it. Without a
  picture it shows the first song's cover. Liked can't be renamed or deleted.
- Typing in the search box goes back to Artists: search covers the library.

**Recently Added** holds every song that arrived on the drive in the last 30 days,
going by each file's creation time (as the ARTISTS · RECENT sort does), from the ticked
library folders. Nothing is saved for it: it's worked out again on every scan, tag save
or folder tick, and each time the Playlists panel opens. Here the **Albums** panel is
used again: it lists the albums with new songs, newest arrival first, each row naming
its artist on a line under the title, with the year and track count below that. The song list
starts with every recent song, album by album, newest album first, each in disc and track
order, under a "Recently Added · Last 30 days" header. **Click an album** to narrow the
list to it, under the usual album header ("9 of 13 tracks" when only part of it is new);
click **Recently Added** again to see everything. It can't be renamed, deleted, given a
picture, added to, removed from or reordered; **#** is the track number. A library copied
to a new drive gets new creation times, so everything would read as recently added for
30 days.

Playlists live in `%LOCALAPPDATA%\AudioFool\playlists.json`, not in the music files,
with chosen pictures copied beside it in `playlist-pictures\`. Each song is stored by
its path plus its artist and album, so a library drive that comes back under another
letter is matched up again. **A song whose file can't be found stays in its playlist**
and simply isn't shown (the header says how many are "not found"), so an unplugged
drive never empties a playlist.

---

## Last.fm scrobbling

Logo menu → **Last.fm…** connects AudioFool to your Last.fm profile.

1. Last.fm gives every application its own API account. Create one at
   [last.fm/api/account/create](https://www.last.fm/api/account/create). Any name and
   description will do, and the callback URL can stay empty.
2. Paste its **API key** and **shared secret** into the Last.fm window and click
   **Connect**.
3. Last.fm opens in your browser. Approve AudioFool there, and the window notices by
   itself within a few seconds. Your password is only ever typed into last.fm.

Once connected, AudioFool sends **now playing** when a track starts and a
**scrobble** once it has played for half its length or four minutes, whichever comes
first. Tracks of 30 seconds or less are not scrobbled, and neither are tracks with no
title or artist tag (a file name makes a poor scrobble). The artist is the track's
Artist tag; Album Artist is sent in its own field, which is how Last.fm files
compilations and features.

- **Only listening counts.** Seeking forward, pausing or the PC sleeping adds nothing.
  Going back to the start of a track (Previous in its first 3 s, or a Repeat One loop)
  is a new play.
- **Offline plays are kept.** Anything that can't be sent waits in
  `%LOCALAPPDATA%\AudioFool\scrobbles.json` and goes out later, retried from one minute
  up to every half hour. Last.fm refuses scrobbles more than two weeks old, so older
  ones are dropped.
- **The status bar shows the state** left of Bit-Perfect once connected. The label
  is **Last.fm** while scrobbling, **Last.fm: off** when switched off,
  **Last.fm: 3 waiting** when sending has failed and plays are queued, and
  **Last.fm: reconnect** when Last.fm rejects the session. Click it to open the
  Last.fm window.
- **Scrobble what I play** turns it off without disconnecting. Plays made while it is
  off are not recorded, not even for later.
- **Disconnect** forgets the session in AudioFool. To withdraw access on Last.fm's side
  as well, remove AudioFool on your
  [applications page](https://www.last.fm/settings/applications).

The key, secret and session are stored in `settings.json`. The session key allows
scrobbling but does not reveal your password.

---

## Analyzing a track

Right-click a song → **Analyze…** opens a window with the track's **spectrogram**
(time left to right, frequency bottom to top, loudness from black through purple, red
and orange to yellow) and **AudioFool's opinion** of whether the audio matches what the
file claims to be: its format, bit depth, sample rate and bitrate, as the song table
shows them.

The whole file is decoded in the background, about a second or two per track, without
touching playback. The window isn't modal, so several can be open side by side to
compare tracks; closing one stops its analysis. If several rows are selected, the one
you right-clicked is analysed.

The opinion is one of **Consistent**, **Can't tell**, **Possibly not…** or **Not…**,
with the reasons in plain sentences and the measured figures underneath. What it looks
for:

- **A lossy source in a lossless file.** MP3 and AAC encoders cut everything above
  16–20.5 kHz, depending on bitrate, and the spectrum stops dead there. A real CD master
  fades out, or ends at 21 kHz or above where the converter's filter is. A cutoff below
  19 kHz is called lossy outright; one between 19 and 20.6 kHz only *possibly* lossy,
  since a few masters are filtered there too. The cutoff is drawn on the picture as a
  dashed line.
- **An MP3 re-encoded from a lower bitrate.** A 320 kbps file that stops at 16 kHz was
  made from something around 128 kbps. Only *possibly* when it's less than 2 kHz short:
  older iTunes and Xing encoders cut at 16 kHz whatever the bitrate.
- **Fake hi-res.** An 88.2 kHz-and-up file (or DSD) with nothing above about 24 kHz, or
  with a mirror image of the treble just above 22.05 kHz, was upsampled from CD quality.
  Faint noise only up there is called *possibly* not hi-res.
- **Fake 24-bit.** A 24-bit file whose music uses only 16 bits, the other eight zero.
  Real 24-bit audio uses them in nearly every sample; some albums are 16-bit with only
  the fades or edits done in 24-bit, and those count as fake too.

It can't see everything: a very quiet or dull recording leaves nothing above 14 kHz to
judge, and is reported as **Can't tell**, as is music that stops below 11 kHz (old
game samples, lo-fi), since no encoder cuts that low. DSD is read as 176.4 kHz PCM for
the analysis. Tracker modules have no source quality to check.

### Checking the whole library

Logo menu → **Statistics…** has a **QUALITY CHECK** section with a row per problem:
likely and possibly transcoded lossless, upscaled and possibly upscaled MP3, fake and
possibly fake hi-res, and fake 24-bit. Click a row to show those tracks in the library,
as with the other rows.

**Check quality** reads three 10-second slices of each track (about 11 minutes for
26,800 tracks, on four background threads while you keep listening; the slices gave the
same verdict as reading whole tracks). Progress shows in the status bar, and Statistics
can be closed while it runs. The results are kept in
`%LOCALAPPDATA%\AudioFool\quality.json`, so later checks only read new or changed
files, and stopping (or quitting) keeps what was done. A result follows its file to a
new drive letter; a retagged file is checked again. Only the ticked folders are checked.

### Clearing a song you know is genuine

The check is sometimes wrong: an early digital master filtered at 20 kHz reads as
possibly transcoded, for example. To take a song out of a row:

- **From the library.** Click the row in Statistics, then right-click a song, several
  selected songs, or an album → **Not Fake 24-bit** (the wording follows the row: **Not
  Fake Hi-Res**, **Not Transcoded**, **Not Upscaled**). An album clears only the songs the
  row shows. They leave the list at once, and the status bar says how many are left.
- **From Analyze.** The window has a button beside Close for each row the song is in.
  Pressing it clears the song from that row and becomes **Put Back: Fake 24-bit**; a line
  under the opinion says it's cleared. The opinion itself doesn't change.

Clearing takes the song out of **that row only**: a song cleared from Fake 24-bit still
shows under Fake hi-res. A row and its *possibly* twin count as one (both say the hi-res
isn't real, for instance). Cleared songs are counted in a grey **Cleared by you** row
under the others; click it, then right-click → **Put Back in Quality Check** to undo.

Clearances are kept in `%LOCALAPPDATA%\AudioFool\quality-cleared.json`, apart from the
check's results, so a retag (here or in Mp3tag) or a later change to the check's rules
doesn't undo them. They follow a song to a new drive letter, as playlists do. A song
replaced by a file of a different format (another bit depth, sample rate, or an MP3's
bitrate) is no longer cleared. Nothing is written to the music files.

---

## Output modes

The **Bit-Perfect** switch in the bottom-right of the status bar chooses how audio
reaches the sound card. It starts off on *every* launch — not just the first — because
exclusive mode silences every other application on the machine while music is playing.
That is a per-session choice, not a setting to be surprised by days later. The volume
slider works the same way, always starting at 100%.

|  | Shared (default) | Bit-perfect (exclusive) |
|---|---|---|
| Device rate | Fixed at Windows' mix rate, usually 48 kHz | Follows each track: 44.1, 48, 88.2, 96, 176.4, 192 kHz |
| Resampling | Yes, everything is converted to the mix rate | None |
| Other apps | Play normally | **Silent while a track plays** |
| Volume slider | Works | **Disabled**, pinned to unity gain |
| Gapless | Always | Within a run of same-rate tracks |

Two consequences worth understanding:

**Volume is disabled in exclusive mode on purpose.** Attenuating in software means
multiplying every sample, which is the definition of *not* bit-perfect. Rather than
quietly compromise, the slider goes dead — use your DAC's volume or the Windows
mixer. Hovering it explains this.

**A sample-rate change costs a short gap.** Gapless works by never stopping the
device; re-clocking a DAC from 44.1 kHz to 96 kHz means stopping it. So a 44.1 kHz
track following a 96 kHz one in the same queue has an audible gap. That's physics,
not a shortcut — and within an album (where rates are uniform) it never happens.

The line under the volume slider shows what the output is actually doing, e.g.
`Exclusive 96 kHz/24-bit (bit-perfect)`. If exclusive mode is refused — another app
already has the device exclusively — it falls back to shared and says so.

**Switching sound devices.** Output always goes to the Windows default device, and
follows it when it changes while the app is open — headphones plugged in, a different
device picked in Windows' sound settings. The playing track carries on from where it was
after a short gap (a paused one stays paused), and the status line names the new device.
The device is checked again at the same time: Bit-Perfect is greyed out on one that can't
do exclusive mode (Bluetooth usually can't), and if it was on, it comes back on when you
return to a device that can.

### DSD

The **DSD passthrough** switch chooses between two paths. It requires bit-perfect
output and a DoP-capable DAC, and is greyed out otherwise.

| | Convert to PCM (default) | DSD passthrough |
|---|---|---|
| What reaches the DAC | PCM | The original DSD bitstream |
| Device rate | DSD rate ÷ 8 — **705.6 kHz** for DSD128 | DSD rate ÷ 16 — **352.8 kHz** for DSD128 |
| Works on | Any DAC | DoP-capable DACs only |

**Convert to PCM** runs at the highest rate BASSDSD allows, which is an eighth of the
DSD rate. Only 1/8, 1/16, 1/32… of the DSD rate are valid, and all of them are in the
44.1 kHz family — converting to 48 kHz would add exactly the resampling step this
whole feature exists to avoid. (BASSDSD's own default is 88.2 kHz; AudioFool raises it
to whatever the device will accept.)

**DSD passthrough** uses DoP: the DSD bitstream is carried 16 bits at a time inside
24-bit PCM words, tagged with a marker byte that alternates `0x05` / `0xFA`. A
DoP-aware DAC recognises that pattern and unwraps the original DSD; the PCM container
is just a delivery mechanism. BASSDSD generates it via `BassFlags.DSDOverPCM`, so the
markers aren't hand-rolled here.

DoP is only offered over an exclusive connection, and refused per-file when the device
can't clock that particular DoP rate — falling back to PCM conversion. That isn't
caution for its own sake: DoP relies on the bits arriving *exactly*, and any
resampling or volume change turns the markers into full-scale noise. Hence float
throughout (float32 represents 24-bit integers exactly), unity gain, and matched rates.

If you want to check the marker stream yourself, the approach is to decode a chunk,
recover each 24-bit word with `(int)Math.Round(sample * 8388608.0) & 0xFFFFFF`, and
confirm the top byte alternates between `0x05` and `0xFA` with both channels agreeing.

---

## How a few things work

**The library cache.** Startup used to re-read tags from every file, which is the
only slow part of a scan. Measured on a 25,977-file, 691 GB library:

| Phase | Time |
|---|---|
| Enumerate every file | 0.17s |
| Stat size + write time | 0.90s |
| **Read tags** | **17.6s** |
| Group and sort | 0.04s |
| Load 13.4 MB cache and rebuild the tree | **0.28s** |

So tags are the only thing worth persisting. `LibraryCache` writes them to
`%LOCALAPPDATA%\AudioFool\library.json`, and startup now goes:

1. Load the cache and show the library — about **0.3 seconds**.
2. In the background, enumerate and stat everything (~1s) and re-read tags only for
   files whose size or write time no longer matches the cache.
3. If nothing changed, leave the view alone entirely. If something did, rebuild and
   re-save.

Size plus write time is enough to catch an edited tag: rewriting a tag in place can
leave the file exactly the same length, but it always moves the write time.

Step 3 matters more than it looks. The refresh lands a second or two after startup,
right when you might be clicking around, so a rebuild re-selects the same artist and
album by name rather than throwing you back to the top of the list — and when nothing
changed it doesn't touch the collections at all.

**Opening where you left off.** Each time a song starts, its path, artist and album go
into `settings.json` (`LastPlayed`). On the next launch, once the cached library is
shown, the Artists and Albums lists select that artist and album and the song is
highlighted in the track list. It doesn't start playing. If the music drive has come back
under another letter, the song is found by artist, album and file name; if the album is
gone or its folder is unticked, the app opens on the first artist as usual.

The cache is disposable: delete it, or change `LibraryCache.CurrentVersion`, and the
next start does a full scan and writes a fresh one. It's written to a temp file and
moved into place, so an interrupted write leaves the last good cache rather than a
truncated one.

**Gapless playback.** The audio device is never stopped between tracks. A BASSmix mixer
stays open and each track is a *decode* channel fed into it. When a track runs dry, a
mixtime sync fires from inside BASS's mixing pass and the next channel — already decoded
on a worker thread — is spliced in on the spot. The handover costs zero samples of
silence. See `AudioEngine.cs`.

**BASS holds the "no sound" device.** `Bass.Init(0, ...)` is deliberate: BASS is only
ever a decoder here, and output goes through WASAPI via `OutputChain`. If BASS held the
real endpoint open in shared mode, it would be competing with our own exclusive-mode
request for the same device.

**Artist grouping** keys off the **album artist** tag, falling back to the track artist.
Without that, every compilation shatters into a dozen one-track "artists" and every
"feat." guest gets its own row in the sidebar.

**The # and Disc columns show just the number** - "3", never "3/12". A file with no
track number shows nothing. The track and disc totals are still read and kept, and are
editable in the tag dialog (right-click a track → Edit Tags…); the grid simply doesn't
show them. Clicking the header sorts numerically, so 2 comes before 10.

**Editing tags.** Right-click a track for *Edit Tags…*, or an album in the Albums
list for *Edit Album Tags…*. Right-clicking the large cover art above the tracks opens
*Edit Album Tags…* straight away; a left-click on it opens the full-size cover. Both edit publisher, composer, conductor, genre and comments as well as the
basics; separate several composers or genres with a semicolon. The album dialog also
sets the track count and disc number for every track. Any field the tracks disagree on
starts empty and marked *Varies*, and is left alone unless you type into it. So editing
an album's genre never flattens each track's own comment. To strip a tag, press the ✕
beside it; in the album dialog that clears it on every track, even where they differed.

**Walking through albums.** *Edit Album Tags…* stays open beside the main window and
follows the Albums list: click another album (or another artist, or use the arrow keys)
and the window shows that album instead. If you've typed something you haven't saved,
it asks first: **Save** writes it to the album you were on, **Don't Save** drops it,
**Cancel** stays put. **Save** keeps the window open on the album, read again from the
files, and if you renamed the album the lists move with it. **Close** shuts it.

*Edit Tags…* on a single song works the same way with the Songs table: click another
song and the window shows it, with the same question about unsaved changes. It moves
only when one song is selected, so Ctrl-clicking a second row leaves it alone. The
dialog for several selected songs still blocks the main window while open.

**Editing several artists or albums at once.** The Artists and Albums lists take
Ctrl-click and Shift-click, like the song table. Right-click one of the selected artists
for *Edit Artist Tags…*, or one of the selected albums for *Edit Album Tags…*: one
dialog covers every track of them all (whole artists and albums, even while a search
shows only part of one), titled with what it covers, "3 artists, 214 tracks". Every
box is filled only when all the tracks agree, and only the boxes you change are
written. So selecting the three artist rows an album is split across and typing the
same Artist and Album Artist merges them; selecting "Album" and "Album (Disc 2)" and
typing one Album name merges those. After saving, the lists move to wherever the tracks
now file. With several selected, the panels to the right keep showing the first one
picked. A single artist works too; a single album opens the usual album dialog, with
its cover art.

**Release dates.** The Year box takes a year (`2026`), a month (`2026-10`) or a full
date (`2026-10-02`), always year-month-day. A file that already carries a full date
shows it there, and saving keeps it. Albums sort by that date; the browser still
shows only the year. In
MP3 and DSF files, a full date upgrades the ID3 tag to version 2.4, the first
version with a field that holds a whole date.

**Finding cover art online.** In *Edit Album Tags…*, **Search Internet…** beside
*Choose Image…* looks the album up on the iTunes Store and the Cover Art Archive, and on
fanart.tv as well once you give it an API key. Only JPEGs of at least 600 × 600 are
shown. Each size is read from the image file itself, not taken from what the site
claims. Covers by the album's artist are shown, exact title matches first, then larger
first; anything by other artists is hidden. Double-click one to use it. Like a picked
file, it is only written when you press Save.

**When a file carries several covers.** Many files hold the cover two or three times,
sometimes at different sizes. AudioFool shows the best one wherever it is stored: a front
cover over other pictures (such as a band photo), then the most pixels, then a JPEG over
a PNG of the same size, then the larger file. The same choice is used everywhere the
embedded cover is: the album header, the full-size viewer, the tag editor and *Save
Embedded Art*. Nothing is written to the files.

**Saving the embedded art as a file.** *Edit Album Tags…* also has **Save Embedded
Art**, under *Choose Image…*. It takes the cover embedded in the album's files and writes
it as `cover.jpg` in the same folder (in each folder, for an album kept as "Disc 1",
"Disc 2"…). It acts at once; you don't need to press Save, and no tags change. If the
tracks in a folder carry different pictures, the one with the most pixels is used. A JPEG
is copied byte for byte, so nothing is compressed. A PNG has to become a JPEG to be
`cover.jpg`: that is the one case where the picture is re-encoded, at quality 100 with
full-resolution colour, with any transparency set against white. An existing `cover.jpg`
is replaced only by a picture with more pixels. Other cover files (`folder.jpg`…) are left
alone. The message line at the bottom of the dialog says what happened.

fanart.tv needs a free personal API key from <https://fanart.tv/get-an-api-key/>. Put
it in `%APPDATA%\AudioFool\settings.json` as `"FanartTvApiKey": "..."` and restart.
Bandcamp is not searched: it has no public API, and it turns automated requests away
with a bot check.

**Library statistics.** Logo menu → **Statistics…** opens a summary of the library:
totals, the five artists with the most tracks, the split by file type and by audio
quality (hi-res, CD-quality, DSD, and lossy above or below 256 kbps), which tags are
most often missing, and tracks by decade. It counts the same tracks the status bar
does, so folders unchecked under Libraries are left out. The cover-art row counts
folder images only; embedded artwork is not cached, so it cannot be counted without
opening every file.

Every row can be clicked. An artist takes you to that artist. Anything else - *Missing
Year*, *FLAC*, *Lossy, under 256 kbps*, *1990s* - narrows the browser to exactly those
tracks, with a chip beside the search box naming the filter; click the chip to show
everything again. Search still works inside the filter. It is made for fixing tags:
edit an album while *Missing Year* is on and it drops out of the list, and the status
bar says how many are left.

**Bit Depth is blank for lossy formats.** MP3, AAC, Vorbis, Opus and WMA decode to float
and have no meaningful source bit depth, so the column shows an em dash rather than
inventing a "16". DSD correctly reports 1 bit.

**Album art** prefers the picture embedded in the file and falls back to `cover.jpg` /
`folder.jpg` / `front.jpg` in the album's folder. Covers are decoded off the UI thread at
the size they'll actually be displayed, and the cache is bounded at 64 MB, evicted
least-recently-used first.

---

## Known limitations

- **No TAK or DTS decoder.** See [Format coverage](#format-coverage) for the options.
- **No visible queue.** Shuffle and repeat do have controls now
  (see [Play order](#play-order)), and so does search - but what is queued cannot be
  seen, reordered or saved - it is simply whatever the track grid was showing when
  you pressed play (an album, or a set of search results).
- **Memory sits high after a *cold* scan** (~900 MB working set for a 26,000-track
  library). A post-scan compaction runs, and the cache means this only happens on a
  first run or after the cache is invalidated - but it is still worth profiling.
- **Tracker formats** (MOD/S3M/XM/IT) load and play, but carry no useful tag metadata.
- **The seek bar runs ~200 ms ahead of what you hear.** Position is read from the
  decode mixer, which is that far ahead of the device buffer. Invisible in practice.
