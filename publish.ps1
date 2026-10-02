# Builds the runnable, self-contained app into .\publish with one command.
#
#   .\publish.ps1            rebuild publish\Optima.exe
#   .\publish.ps1 -Run       rebuild and start it
#
# Exists because the two-command flow in the README kept producing stale publish
# folders: the app and the elevated helper were published separately, nobody re-ran
# them after changes, and a running instance silently locked files. This script
# stops running instances, cleans the folder so no stale binaries survive, and
# publishes the helper first and the app second so the app's newer shared
# dependencies always win a collision.

param(
    [switch]$Run,
    [string]$Output = "publish",
    [string]$Runtime = "win-x64",
    [string]$Configuration = "Release",
    # Dev edition: compiles in the DEVELOPER EMULATOR settings section.
    [switch]$DevEdition
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
# The output may be an absolute path: the local dev pipeline publishes straight to the
# Desktop\Optima Dev folder.
$out = if ([System.IO.Path]::IsPathRooted($Output)) { $Output } else { Join-Path $root $Output }

# A running instance locks the assemblies it loaded; publishing over them fails.
$stopped = @()
foreach ($name in @("Optima", "Optima.Watchdog")) {
    foreach ($process in Get-Process -Name $name -ErrorAction SilentlyContinue) {
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
Write-Host "publishing Optima.Watchdog ($Configuration $Runtime)"
dotnet publish (Join-Path $root "src\Optima.Watchdog") -c $Configuration -r $Runtime --self-contained -o $out --nologo -v quiet -p:PublishReadyToRun=true
if ($LASTEXITCODE -ne 0) { throw "publishing Optima.Watchdog failed (exit $LASTEXITCODE)" }

Write-Host "publishing Optima.App ($Configuration $Runtime)"
$devProps = @()
if ($DevEdition) { $devProps += "-p:DevEdition=true" }
dotnet publish (Join-Path $root "src\Optima.App") -c $Configuration -r $Runtime --self-contained -o $out --nologo -v quiet -p:PublishReadyToRun=true @devProps
if ($LASTEXITCODE -ne 0) { throw "publishing Optima.App failed (exit $LASTEXITCODE)" }

$exe = Join-Path $out "Optima.exe"
$stamp = (Get-Item $exe).LastWriteTime
Write-Host "done: $exe (built $stamp)"

if ($Run) {
    Start-Process -FilePath $exe -WorkingDirectory $out
    Write-Host "started $exe"
}
