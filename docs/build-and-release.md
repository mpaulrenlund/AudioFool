# Build and release

The manual build → verify → ship commands, version control and the release steps. Moved out of HANDOFF.md on 2026-11-11, word for word.

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

Three shell notes:

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

All on GitHub (private, so downloads need a signed-in account); `gh release list` has
them all. **Latest: v0.11.0** (2026-11-10, session 77, tag on `2c1fcdf`, 60.8 MB zip,
19 files). Every one after v0.1.0 was made by Claude.
`AudioFool.csproj` starts with a byte-order mark: keep it when bumping the version
(v0.6.0's bump dropped it and needed a follow-up commit). Handle a release end to end when asked, but
**confirm the version number and the notes with the user before `gh release create`**:
it publishes a tag. Middle number for new features, last for fixes only.

1. Commit and push anything outstanding, and run the tests.
2. Bump `<Version>` in `src/AudioFool/AudioFool.csproj` (the only place it lives),
   commit ("Version x.y.z") and push, so the tag lands on that commit.
3. Fresh `dotnet publish` (the command above), then delete `publish\*.pdb`. Check
   `(Get-Item publish\AudioFool.exe).VersionInfo.ProductVersion` shows the new version
   (About's version line reads it, so it follows by itself).
4. Compare the file list with the last release (`gh release download v<old> --dir
   <temp>`, unzip, `Compare-Object` the names): **19 files** (the exe, 13 BASS DLLs,
   5 WPF native DLLs). A missing loose DLL breaks the app on another machine.
5. `Compress-Archive publish\* $env:TEMP\AudioFool-<ver>-win-x64.zip` (never inside
   the repo). About 60 MB.
6. Notes in a file (dodges quoting): `gh release create v<ver> <zip> --target main
   --title "AudioFool <ver>" --notes-file <file>`, then `git fetch --tags`.
7. Check the tag with `git rev-list -n 1 v<ver>`. **Not `git rev-parse v<ver>^{commit}`**:
   PowerShell mangles the `^{}` and the command fails.
8. Install the same build over the installed copy (close the app first; it stops
   playback, so say so).

The notes are written for the user, grouped New / Improved / Fixed, from
`git log v<old>..HEAD` with the Docs and ThemeLab commits left out.

Write commit messages to a file and use `git commit -F <file>`: in Windows
PowerShell 5.1 a double quote inside a here-string message breaks the argument
and the commit fails.
