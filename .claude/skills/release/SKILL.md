---
name: release
description: Cut a GitHub release of AudioFool for Windows (the desktop app in C:\MusicPlayer) end to end - propose the version and notes, bump, fresh publish, zip, `gh release create`, install. Use whenever the user says "release", "new release", "cut a release", "release it", "let's do a 0.x.0 release", "publish a new version" or "commit/push and publish" in this repo, even when they don't name a version number.
---

# Release AudioFool (desktop)

A release publishes a tag to GitHub, which can't be quietly taken back. So the user
approves two things before anything is tagged: the **version number** and the **notes**.
Everything else you do without walkthroughs, and report at the end.

Shell notes that apply to every step: `dotnet` needs
`$env:PATH = "C:\Program Files\dotnet;$env:PATH"` first, and that assignment must never
share a PowerShell call with `Remove-Item` (the sandbox guard blocks it). Write commit
messages and release notes to a file in the scratchpad and pass the file (`git commit -F`,
`--notes-file`): quotes inside PowerShell arguments break silently.

## 1. Propose the version and the notes, then wait

- `gh release list --limit 1` gives the last tag. `git log v<old>..HEAD --oneline` gives
  what changed since.
- Draft the notes for the user, not for a developer: what they will see or can now do,
  grouped **New / Improved / Fixed**. Leave out `Docs:` and ThemeLab commits.
- Version: middle number when there is anything new, last number for fixes only. The
  leading 0 stays until the user calls the app finished. Offer your recommendation and
  one alternative; their pick wins.
- Show both and stop. Go on only when they say yes.

## 2. Make sure what ships is what was tested

- `git status`: commit and push anything outstanding (code and docs as separate commits;
  docs commits start with `Docs:` so the next release's notes can skip them).
- `tools\ship.ps1 -NoInstall` builds in Release, runs the tests and publishes, and prints
  one line per step. Stop if anything fails. (If a single test fails, run it once more
  before believing it: `VisualizerFeedTests.A_frame_allocates_nothing` failed once and
  then passed four times on 2026-11-11.)

## 3. Bump the version

- `<Version>` in `src/AudioFool/AudioFool.csproj` is the only place it lives. The file
  starts with a byte-order mark: keep it (v0.6.0's bump dropped it and needed a second
  commit).
- Commit as `Version x.y.z`, push, and note the commit ID: the tag must land on it.

## 4. Publish fresh

`tools\ship.ps1 -NoInstall -SkipTests` (the tests ran in step 2). Its `publish` line must
show the new version and 19 files. The manual publish command, should the script fail, is
in `docs/build-and-release.md`.

## 5. Check the file list

The zip must hold **19 files**: the exe, 13 BASS DLLs and 5 WPF native DLLs. A missing
loose DLL breaks the app on another machine and nothing on this one will show it. Compare
names with the last release (`gh release download v<old> --dir <temp folder>`, unzip,
`Compare-Object`). If the list differs, stop and tell the user why.

## 6. Zip and release

- `Compress-Archive C:\MusicPlayer\publish\* $env:TEMP\AudioFool-<ver>-win-x64.zip`
  (about 60 MB; never inside the repo).
- `gh release create v<ver> <zip> --target main --title "AudioFool <ver>" --notes-file <file>`
- `git fetch --tags`, then check `git rev-list -n 1 v<ver>` is the bump commit. Don't use
  `git rev-parse v<ver>^{commit}`: PowerShell mangles the `^{}`.

## 7. Install the same build

`tools\ship.ps1 -InstallOnly` installs exactly what is in `publish\` (the build that went
into the zip) without rebuilding, and compares the hashes afterwards. Installing closes
AudioFool. The script closes it by itself when nothing is playing; when music is playing,
or it can't tell, it stops with exit code 2 instead. Then ask the user, and run it again
with `-Force` once they say yes.

Don't launch the app from the shell (a shell-launched copy reads stale private settings
and writes to the log): ask the user to open it from their shortcut.

## 8. Record it and report

- Update the latest-release line in `HANDOFF.md` and add the release to `HISTORY.md`;
  commit as `Docs: ...` and push.
- Report in a few lines: version, the commit the tag is on, zip size and file count,
  whether it is installed, and the release link.
