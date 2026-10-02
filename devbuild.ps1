# Builds the local dev edition in one command:
#
#   .\devbuild.ps1            republish the Desktop "Optima Dev" folder and build its installer
#   .\devbuild.ps1 -Run       ... and start the app afterwards
#
# Publishes the app and its elevated helper (self-contained, ReadyToRun, dev edition) into
# %USERPROFILE%\Desktop\Optima Dev, then compiles a matching per-user setup next to them, named
# Optima-Setup-<version>-dev-<timestamp>.exe so every change leaves its own installer behind (an
# earlier run's setups are kept, not overwritten).
#
# Nothing here touches the released version, the git history or GitHub: the dev folder is the
# whole output of this pipeline.

param(
    [switch]$Run,
    # Where the dev build lives. Defaults to the Desktop folder.
    [string]$Folder = (Join-Path ([Environment]::GetFolderPath("Desktop")) "Optima Dev")
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $MyInvocation.MyCommand.Path

$label = "-dev-" + (Get-Date -Format "yyyyMMdd-HHmm")
Write-Host "dev build: $Folder"

& (Join-Path $root "installer.ps1") -Publish -DevEdition -Source $Folder -OutDir $Folder -Label $label
if ($LASTEXITCODE -ne 0) { throw "installer.ps1 failed (exit $LASTEXITCODE)" }

if ($Run) {
    Start-Process -FilePath (Join-Path $Folder "Optima.exe") -WorkingDirectory $Folder
    Write-Host "started $(Join-Path $Folder 'Optima.exe')"
}
