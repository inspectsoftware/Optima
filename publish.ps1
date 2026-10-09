# Builds the runnable, self-contained app into .\publish with one command.
#
#   .\publish.ps1            rebuild publish\Optima.exe
#   .\publish.ps1 -Run       rebuild and start it
#
# Exists because the two-command flow in the README kept producing stale publish
# folders: the app and the elevated helper were published separately, nobody re-ran
# them after changes, and a running instance silently locked files. This script
# stops running instances, cleans the folder so no stale binaries survive, and
# publishes the helper apart from the app and merges it in afterwards, so the
# app's newer shared dependencies always win a collision.

param(
    [switch]$Run,
    [string]$Output = "publish",
    [string]$Runtime = "win-x64",
    [string]$Configuration = "Release",
    # Leave Optima Shield out of the payload even when vendor\shield has it. The app is then the
    # same kind of build as one made from source: no protected play, and nothing in it says so.
    [switch]$NoShield
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $MyInvocation.MyCommand.Path

# Assembly file version, or null when the file has none (native libraries mostly).
function Get-AssemblyVersion([string]$path) {
    try { return [version](Get-Item $path).VersionInfo.FileVersion } catch { return $null }
}
# The output may be an absolute path: the local dev pipeline publishes straight to the
# Desktop\Optima Dev folder.
$out = if ([System.IO.Path]::IsPathRooted($Output)) { $Output } else { Join-Path $root $Output }

# A running instance locks the assemblies it loaded; publishing over them fails.
# Only an instance started from the output folder does that. The installed copy runs from its own
# folder, and stopping it here ended whatever session it was in with nothing put back. A process
# whose path cannot be read is the elevated helper of some Optima, seen from an ordinary shell: it
# is left alone as well. Should it be this folder's, the clean below fails on the locked file and
# says so, which is better than ending the installed copy's helper in the middle of a session.
$outPrefix = $out.TrimEnd('\') + '\'
$stopped = @()
foreach ($name in @("Optima", "Optima.Watchdog", "Optima.Shield")) {
    foreach ($process in Get-Process -Name $name -ErrorAction SilentlyContinue) {
        $path = try { $process.Path } catch { $null }
        if (-not $path) {
            Write-Host "leaving running $name (pid $($process.Id)) alone: it is elevated, so not this folder's to stop"
            continue
        }
        if (-not $path.StartsWith($outPrefix, [StringComparison]::OrdinalIgnoreCase)) {
            Write-Host "leaving running $name (pid $($process.Id)) alone: it runs from $(Split-Path $path -Parent)"
            continue
        }
        Write-Host "stopping running $name (pid $($process.Id))"
        try {
            $process | Stop-Process -Force -ErrorAction Stop
            $stopped += $process
        } catch {}
    }
}
foreach ($process in $stopped) {
    try { $process.WaitForExit(5000) | Out-Null } catch {}
}

# Clean output so files from earlier publishes cannot mix with the new build.
# Guarded: only wipe a folder that is recognizably our own publish output.
# Retried: Windows can hold file locks for a moment after the process exits.
if ((Test-Path $out) -and (Test-Path (Join-Path $out "Optima.exe"))) {
    Write-Host "cleaning $out"
    # Setup installers built from this folder (the local dev pipeline keeps them here, in Installers\
    # and from older runs possibly in the root) are the one thing that survives: a rebuild must not
    # erase the installers made from earlier runs.
    $keepFile = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($setup in Get-ChildItem -Path $out -File -Filter "Optima-Setup-*.exe" -ErrorAction SilentlyContinue) {
        [void]$keepFile.Add($setup.Name)
    }
    $keepDir = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($dirName in @("Installers")) { [void]$keepDir.Add($dirName) }
    $attempts = 0
    while ($true) {
        try {
            foreach ($child in Get-ChildItem -Path $out -Force) {
                if (-not $child.PSIsContainer -and $keepFile.Contains($child.Name)) { continue }
                if ($child.PSIsContainer -and $keepDir.Contains($child.Name)) { continue }
                Remove-Item -Recurse -Force $child.FullName -ErrorAction Stop
            }
            break
        } catch {
            $attempts++
            if ($attempts -ge 10) { throw "could not clean $out after $attempts attempts: $_" }
            Start-Sleep -Milliseconds 500
        }
    }
}

# ReadyToRun precompiles the assemblies, trading a little disk for a colder, faster first
# paint: the launcher and its helper both start from a jitted-nothing state today.
#
# The helper is published into a folder of its own and merged in *after* the app, because the two
# share dependencies at different versions (TraceEvent drags 6.x Microsoft.Extensions.* into the
# helper) and MSBuild skips a copy whose destination is newer than its source. The helper's freshly
# compiled copies are always newer than the app's package files, so publishing both into one folder
# let the older assembly win and the launcher died on startup with "could not load file or assembly".
# Publishing apart, app first and helper merged in without overwriting anything, removes the race
# rather than trying to win it.
$helperOut = Join-Path $root "artifacts\helper-publish"
if (Test-Path $helperOut) { Remove-Item -Recurse -Force $helperOut }
Write-Host "publishing Optima.Watchdog ($Configuration $Runtime)"
dotnet publish (Join-Path $root "src\Optima.Watchdog") -c $Configuration -r $Runtime --self-contained -o $helperOut --nologo -v quiet -p:PublishReadyToRun=true
if ($LASTEXITCODE -ne 0) { throw "publishing Optima.Watchdog failed (exit $LASTEXITCODE)" }

Write-Host "publishing Optima.App ($Configuration $Runtime)"
$includeShield = if ($NoShield) { "false" } else { "true" }
dotnet publish (Join-Path $root "src\Optima.App") -c $Configuration -r $Runtime --self-contained -o $out --nologo -v quiet -p:PublishReadyToRun=true "-p:IncludeShield=$includeShield"
if ($LASTEXITCODE -ne 0) { throw "publishing Optima.App failed (exit $LASTEXITCODE)" }

# The helper's own files always travel; everything else only fills gaps the app left, so a shared
# dependency stays at the app's version whichever of the two was compiled last.
Write-Host "merging the elevated helper into the payload"
$merged = 0
foreach ($file in Get-ChildItem $helperOut -File -Recurse) {
    $relative = $file.FullName.Substring($helperOut.Length + 1)
    $destination = Join-Path $out $relative
    if ($relative -notlike "Optima.Watchdog.*" -and (Test-Path $destination)) { continue }
    $folder = Split-Path $destination -Parent
    if (-not (Test-Path $folder)) { New-Item -ItemType Directory -Force $folder | Out-Null }
    Copy-Item $file.FullName $destination -Force
    $merged++
}
Remove-Item -Recurse -Force $helperOut
Write-Host "helper files added: $merged"

# The payload has to be the app's own assemblies: a shared dependency left at an older version is
# not a cosmetic problem, it is a launcher that cannot start. Compared by version rather than by
# timestamp, since a timestamp comparison is what caused the problem in the first place.
$appBin = Join-Path $root "src\Optima.App\bin\$Configuration\net10.0-windows\$Runtime"
$stale = @()
foreach ($source in Get-ChildItem $appBin -File -Filter *.dll) {
    if ($source.Name -like "Optima.Watchdog*") { continue }
    $published = Join-Path $out $source.Name
    if (-not (Test-Path $published)) { continue }
    $expected = Get-AssemblyVersion $source.FullName
    $actual = Get-AssemblyVersion $published
    if ($expected -and $actual -and $actual -lt $expected) {
        $stale += "$($source.Name): payload $actual, app $expected"
    }
}
if ($stale.Count -gt 0) {
    throw ("the payload carries older assemblies than the app builds:`n  " + ($stale -join "`n  "))
}

$exe = Join-Path $out "Optima.exe"
$stamp = (Get-Item $exe).LastWriteTime
Write-Host "done: $exe (built $stamp)"

# Optima Shield is not built here: it arrives as vendor\shield\Optima.Shield.exe. Say which kind of
# payload this is, so a build without it is never published by accident.
if (Test-Path (Join-Path $out "Optima.Shield.exe")) {
    Write-Host "protected play: Optima.Shield.exe is in the payload"
} else {
    Write-Host "protected play: NOT in this payload (no vendor\shield\Optima.Shield.exe). This build runs without it."
}

if ($Run) {
    Start-Process -FilePath $exe -WorkingDirectory $out
    Write-Host "started $exe"
}
