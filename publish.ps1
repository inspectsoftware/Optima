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
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$out = Join-Path $root $Output

# A running instance locks the assemblies it loaded; publishing over them fails.
$stopped = @()
foreach ($name in @("Optima", "Optima.Elevated")) {
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
    $attempts = 0
    while ($true) {
        try {
            Remove-Item -Recurse -Force $out -ErrorAction Stop
            break
        } catch {
            $attempts++
            if ($attempts -ge 10) { throw "could not clean $out after $attempts attempts: $_" }
            Start-Sleep -Milliseconds 500
        }
    }
}

Write-Host "publishing Optima.Elevated ($Configuration $Runtime)"
dotnet publish (Join-Path $root "src\Optima.Elevated") -c $Configuration -r $Runtime --self-contained -o $out --nologo -v quiet
if ($LASTEXITCODE -ne 0) { throw "publishing Optima.Elevated failed (exit $LASTEXITCODE)" }

Write-Host "publishing Optima.App ($Configuration $Runtime)"
dotnet publish (Join-Path $root "src\Optima.App") -c $Configuration -r $Runtime --self-contained -o $out --nologo -v quiet
if ($LASTEXITCODE -ne 0) { throw "publishing Optima.App failed (exit $LASTEXITCODE)" }

$exe = Join-Path $out "Optima.exe"
$stamp = (Get-Item $exe).LastWriteTime
Write-Host "done: $exe (built $stamp)"

if ($Run) {
    Start-Process -FilePath $exe -WorkingDirectory $out
    Write-Host "started $exe"
}
