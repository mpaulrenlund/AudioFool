<#
.SYNOPSIS
  Build, test, publish and install AudioFool in one go, printing a few lines.

.DESCRIPTION
  tools\ship.ps1              build (Release), tests, publish, install over the installed copy
  tools\ship.ps1 -NoInstall   stop after publish; the installed app is not touched
  tools\ship.ps1 -SkipTests   leave the unit tests out
  tools\ship.ps1 -InstallOnly install what is already in publish\ without rebuilding
                              (a release installs the very build that went into the zip)
  tools\ship.ps1 -Force       install even while music is playing (it stops the music)

  Installing has to close AudioFool. If it is playing, or the script cannot tell, it does
  not install unless -Force is given: ask the user first.

  Exit codes: 0 done, 1 a step failed, 2 published but not installed (the app is in use).
  The full output of the last step is in %TEMP%\audiofool-ship.log.

  Keep this file ASCII: Windows PowerShell 5.1 reads a script without a byte-order mark
  as ANSI.
#>
[CmdletBinding()]
param(
    [switch]$NoInstall,
    [switch]$SkipTests,
    [switch]$InstallOnly,
    [switch]$Force
)

$ErrorActionPreference = 'Stop'

$repo          = Split-Path -Parent $PSScriptRoot
$sln           = Join-Path $repo 'AudioFool.slnx'
$testProj      = Join-Path $repo 'tests\AudioFool.Core.Tests\AudioFool.Core.Tests.csproj'
$appProj       = Join-Path $repo 'src\AudioFool\AudioFool.csproj'
$publish       = Join-Path $repo 'publish'
$installed     = Join-Path $env:LOCALAPPDATA 'Programs\AudioFool'
$log           = Join-Path ([IO.Path]::GetTempPath()) 'audiofool-ship.log'
$expectedFiles = 19   # the exe, 13 BASS DLLs, 5 WPF native DLLs

# dotnet is not on PATH in a fresh shell on this machine.
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    $env:PATH = "C:\Program Files\dotnet;$env:PATH"
}

function Stop-WithFailure([string]$Step) {
    Write-Host "$Step FAILED"
    $lines = @(Get-Content $log -ErrorAction SilentlyContinue)
    $errs = @($lines | Where-Object { $_ -match '(: error |error MSB|\[FAIL\]|^\s*Failed[ !]|Test Run Failed)' } |
        ForEach-Object { $_.Trim() } | Select-Object -Unique -First 15)
    if ($errs.Count -eq 0) { $errs = @($lines | Select-Object -Last 15) }
    foreach ($e in $errs) { Write-Host "  $e" }
    Write-Host "full log: $log"
    exit 1
}

function Invoke-Step([string]$Step, [string]$Command) {
    # cmd does the redirect: in Windows PowerShell 5.1, 2>&1 on a native program turns
    # its stderr lines into errors.
    cmd /c "$Command > `"$log`" 2>&1"
    if ($LASTEXITCODE -ne 0) { Stop-WithFailure $Step }
}

# Reads the playback position from the running app with UI Automation. This only reads:
# it never moves the pointer, sends keys or takes focus.
function Get-PositionText([int]$ProcessId) {
    Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
    $ae      = [System.Windows.Automation.AutomationElement]
    $scope   = [System.Windows.Automation.TreeScope]
    $pidCond = New-Object System.Windows.Automation.PropertyCondition($ae::ProcessIdProperty, $ProcessId)
    $idCond  = New-Object System.Windows.Automation.PropertyCondition($ae::AutomationIdProperty, 'PositionText')
    foreach ($win in $ae::RootElement.FindAll($scope::Children, $pidCond)) {
        $el = $win.FindFirst($scope::Descendants, $idCond)
        if ($el) { return $el.Current.Name }
    }
    return $null
}

function Get-PlayState([int]$ProcessId) {
    try {
        $first = Get-PositionText $ProcessId
        if ($null -eq $first) { return 'unknown' }
        Start-Sleep -Milliseconds 2500
        $second = Get-PositionText $ProcessId
        if ($null -eq $second) { return 'unknown' }
        if ($first -ne $second) { return 'playing' }
        return 'idle'
    } catch {
        return 'unknown'
    }
}

if (-not $InstallOnly) {
# --- build ---------------------------------------------------------------------------
Invoke-Step 'build' "dotnet build `"$sln`" -c Release --nologo -v q"
$warnings = @(Get-Content $log | Where-Object { $_ -match ': warning ' } | Select-Object -Unique).Count
Write-Host "build    ok, $warnings warnings"

# --- tests ---------------------------------------------------------------------------
if ($SkipTests) {
    Write-Host 'tests    skipped (-SkipTests)'
} else {
    Invoke-Step 'tests' "dotnet test `"$testProj`" -c Release --nologo -v q"
    $summary = Get-Content $log | Where-Object { $_ -match 'Passed:\s*(\d+)' } | Select-Object -Last 1
    if ($summary -and $summary -match 'Failed:\s*(\d+).*Passed:\s*(\d+).*Skipped:\s*(\d+)') {
        Write-Host "tests    $($Matches[2]) passed, $($Matches[1]) failed, $($Matches[3]) skipped"
    } else {
        Write-Host 'tests    ok'
    }
}

# --- publish -------------------------------------------------------------------------
Invoke-Step 'publish' "dotnet publish `"$appProj`" -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -p:DebugType=none -o `"$publish`" --nologo -v q"
Get-ChildItem $publish -Filter *.pdb -ErrorAction SilentlyContinue | Remove-Item -Force
}

if (-not (Test-Path (Join-Path $publish 'AudioFool.exe'))) {
    Write-Host "publish FAILED: there is no AudioFool.exe in $publish"
    exit 1
}
$files   = @(Get-ChildItem $publish -Recurse -File)
$version = ((Get-Item (Join-Path $publish 'AudioFool.exe')).VersionInfo.ProductVersion -split '\+')[0]
$note    = ''
if ($files.Count -ne $expectedFiles) { $note = "  <-- expected $expectedFiles, check before releasing" }
$what    = 'publish '
if ($InstallOnly) { $what = 'existing' }
Write-Host "$what AudioFool $version, $($files.Count) files$note"

if ($NoInstall) {
    Write-Host 'install  skipped (-NoInstall); the installed app is unchanged'
    exit 0
}

# --- install -------------------------------------------------------------------------
$closed = 'AudioFool was not running'
$procs  = @(Get-Process AudioFool -ErrorAction SilentlyContinue)
if ($procs.Count -gt 0) {
    $state = Get-PlayState $procs[0].Id
    if ($state -ne 'idle' -and -not $Force) {
        if ($state -eq 'playing') {
            Write-Host 'install  NOT DONE: AudioFool is playing music, and installing would stop it'
        } else {
            Write-Host 'install  NOT DONE: AudioFool is open and the script could not tell whether it is playing'
        }
        Write-Host '         ask the user, then run again with -Force (or after they close the app)'
        exit 2
    }
    foreach ($p in $procs) { [void]$p.CloseMainWindow() }
    $deadline = (Get-Date).AddSeconds(10)
    while ((Get-Process AudioFool -ErrorAction SilentlyContinue) -and (Get-Date) -lt $deadline) {
        Start-Sleep -Milliseconds 500
    }
    $left = @(Get-Process AudioFool -ErrorAction SilentlyContinue)
    if ($left.Count -gt 0) {
        $left | Stop-Process -Force
        $closed = "AudioFool was closed by force (it did not close by itself; it was $state)"
    } else {
        $closed = "AudioFool was closed (it was $state)"
    }
    Start-Sleep -Seconds 2   # the exe stays locked for a moment after the process ends
}

if (-not (Test-Path $installed)) { New-Item -ItemType Directory -Path $installed | Out-Null }
$copied = $false
for ($try = 1; $try -le 3 -and -not $copied; $try++) {
    try {
        Copy-Item (Join-Path $publish '*') $installed -Recurse -Force
        $copied = $true
    } catch {
        if ($try -eq 3) {
            Write-Host "install  FAILED: $($_.Exception.Message)"
            exit 1
        }
        Start-Sleep -Seconds 2
    }
}

$different = 0
foreach ($f in $files) {
    $relative = $f.FullName.Substring($publish.Length).TrimStart('\')
    $target   = Join-Path $installed $relative
    if (-not (Test-Path $target) -or
        (Get-FileHash $f.FullName).Hash -ne (Get-FileHash $target).Hash) { $different++ }
}
if ($different -gt 0) {
    Write-Host "install  FAILED: $different of $($files.Count) installed files differ from publish\"
    exit 1
}
Write-Host "install  $($files.Count) files, hashes identical. $closed"
Write-Host 'next     ask the user to open AudioFool from their shortcut (do not launch it from the shell)'
exit 0
