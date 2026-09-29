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
- Three themes, chosen from the logo menu in the title bar and remembered between runs:
  **Dark** (Fluent, Mica backdrop, teal accent), **Vista** (Aero Glass over an
  Acrylic backdrop), and **PS1** - the original grey console as 1990s consumer
  electronics: warm grey plastic (`#A7A4A3`) filling the window, slightly lighter
  raised panels, recessed inputs and tracks, 1 px outlines and small corners. The
  controller colours are rare, like the face buttons on a pad: the Play/Pause key
  is green while it shows Play and red while it shows Pause, and the pane labels carry the pad's shapes (a blue ✕ for artists, a red ○
  for albums, a green △ before the album title). The playing track and the seek
  bar are green, the selected artist and album get a blue marker, and a library
  scan fills a yellow bar. Every coloured mark also has a shape or a word. PS1 respects the
  Windows "show animations" setting.
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

Two buttons flank the transport controls in the now-playing bar: **shuffle** on the
left of Previous, **repeat** on the right of Next. Both light up in the accent colour
when active and show a struck-through icon when not, and both are remembered between
sessions.

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

The status bar shows what the output is actually doing, e.g.
`Exclusive 96 kHz/24-bit (bit-perfect)`. If exclusive mode is refused — another app
already has the device exclusively — it falls back to shared and says so.

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

**Editing tags.** Right-click a track for *Edit Tags…*, or an album - in the Albums
list or its large cover art above the tracks - for *Edit Album Tags…*. Both edit publisher, composer, conductor, genre and comments as well as the
basics; separate several composers or genres with a semicolon. The album dialog also
sets the track count and disc number for every track. Any field the tracks disagree on
starts empty and marked *Varies*, and is left alone unless you type into it. So editing
an album's genre never flattens each track's own comment. To strip a tag, press the ✕
beside it; in the album dialog that clears it on every track, even where they differed.

**Release dates.** The Year box takes a year (`2026`), a month (`2026-10`) or a full
date (`2026-10-02`), always year-month-day. A file that already carries a full date
shows it there, and saving keeps it. Albums sort by that date; the browser still
shows only the year. In
MP3 and DSF files, a full date upgrades the ID3 tag to version 2.4, the first
version with a field that holds a whole date.

**Finding cover art online.** In *Edit Album Tags…*, **Search Internet…** beside
*Choose Image…* looks the album up on the iTunes Store and the Cover Art Archive, and on
fanart.tv as well once you give it an API key. Only JPEGs of at least 1,000 × 1,000 are
shown. Each size is read from the image file itself, not taken from what the site
claims. Covers by the album's artist are shown, exact title matches first, then larger
first; anything by other artists is hidden. Double-click one to use it. Like a picked
file, it is only written when you press Save.

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
- **No playlists, and no visible queue.** Shuffle and repeat do have controls now
  (see [Play order](#play-order)), and so does search - but what is queued cannot be
  seen, reordered or saved - it is simply whatever the track grid was showing when
  you pressed play (an album, or a set of search results).
- **Memory sits high after a *cold* scan** (~900 MB working set for a 26,000-track
  library). A post-scan compaction runs, and the cache means this only happens on a
  first run or after the cache is invalidated - but it is still worth profiling.
- **Tracker formats** (MOD/S3M/XM/IT) load and play, but carry no useful tag metadata.
- **The seek bar runs ~200 ms ahead of what you hear.** Position is read from the
  decode mixer, which is that far ahead of the device buffer. Invisible in practice.
