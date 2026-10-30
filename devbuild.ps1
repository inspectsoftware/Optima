# Builds the local dev payload and its installer in one command:
#
#   .\devbuild.ps1            republish the Desktop "Optima Dev" folder and build its installer
#   .\devbuild.ps1 -Run       ... and start the app afterwards
#
# Publishes the app and its elevated helper (self-contained, ReadyToRun) into
# %USERPROFILE%\Desktop\Optima Dev, then compiles a matching setup into the Installers
# subfolder of that same folder, numbered Optima-Setup-1.exe, -2, -3 ... in build order so every
# change leaves its own installer behind (earlier setups are kept, not overwritten). The setup excludes
# the Installers folder and any setup in the payload root, so no setup ever embeds another.
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

# The app payload is the folder itself; the setups live in their own subfolder so they are easy to
# find and never end up inside the installed app.
$installers = Join-Path $Folder "Installers"
New-Item -ItemType Directory -Force $installers | Out-Null

# The next number is one past the highest already in the folder, so a deleted setup never has its
# number reused by accident while later ones remain.
$number = 1 + (@(Get-ChildItem $installers -Filter "Optima-Setup-*.exe" |
    Where-Object { $_.Name -match '^Optima-Setup-(\d+)\.exe$' } |
    ForEach-Object { [int]$Matches[1] }) + 0 | Measure-Object -Maximum).Maximum
$label = "-dev$number"
Write-Host "dev build $number`: $Folder"

& (Join-Path $root "installer.ps1") -Publish -Source $Folder -OutDir $installers -Label $label
if ($LASTEXITCODE -ne 0) { throw "installer.ps1 failed (exit $LASTEXITCODE)" }

# installer.ps1 names it Optima-Setup-<version>-dev<N>.exe; the folder lists them by number alone.
$built = Get-ChildItem $installers -Filter "Optima-Setup-*$label.exe" | Select-Object -First 1
Move-Item $built.FullName (Join-Path $installers "Optima-Setup-$number.exe")
Write-Host "installer: Optima-Setup-$number.exe"

Write-Host "installers: $installers"

if ($Run) {
    Start-Process -FilePath (Join-Path $Folder "Optima.exe") -WorkingDirectory $Folder
    Write-Host "started $(Join-Path $Folder 'Optima.exe')"
}
