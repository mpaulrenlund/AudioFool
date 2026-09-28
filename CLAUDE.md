# AudioFool

A personal Windows 11 music player. **C# / .NET 10 / WPF**, with WPF-UI for Fluent dark
theming, ManagedBass for audio and TagLibSharp for metadata.

## Read these first

- **[HANDOFF.md](HANDOFF.md)** — current state, the build-and-install loop, verification
  techniques, and the traps worth not rediscovering. Start here.
- **[README.md](README.md)** — how the app works: output modes, DSD, the library cache,
  portable-drive handling, keyboard shortcuts.

## Three things to know before touching anything

**1. Publishing does not update the app the user runs.**
`dotnet publish` writes to `C:\MusicPlayer\publish\`. The shortcuts launch
`%LOCALAPPDATA%\Programs\AudioFool\`. Always finish by copying publish output over the
installed copy, or your change appears to do nothing. Full commands in HANDOFF.md.

**2. Do not drive the mouse or keyboard to test the UI.**
The user works on this machine. Moving the pointer, sending keystrokes to the focused
window, or pulling windows to the foreground interrupts them — this stopped a previous
session. Use UI Automation (sets values without stealing focus) and `PrintWindow` for
screenshots, calling `SetProcessDPIAware()` first. Details in HANDOFF.md.

**3. The music library lives on a removable drive.**
`D:\Music`, an external SSD, ~26,000 tracks. Drive letters can change and the folder may
be absent at startup. Both are handled (`LibraryRelocator`; unreachable folders are
carried over rather than treated as deleted). Never write code that assumes a stable
absolute path, and never let a missing folder be interpreted as a deleted library.

## Shell notes

`dotnet` is not on PATH in a fresh shell — prefix with
`$env:PATH = "C:\Program Files\dotnet;$env:PATH"`. Never combine that assignment with a
`Remove-Item` in one PowerShell call; the sandbox guard misreads it as deleting
`C:\Program` and blocks the command.

## Verify, don't assume

Playback, DSD correctness and drive behaviour have all been verified by driving the real
app or a headless probe against `AudioFool.Core` — not by reasoning about the code. Keep
that standard. `dotnet test` covers the 295 unit tests over sorting, caching, search, path
handling, play order, tag writing (including in-place grid edits), release dates,
library statistics, online cover-art parsing, Last.fm scrobbling and track-number spelling.
