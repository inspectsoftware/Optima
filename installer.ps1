# Builds the Windows installer in one command:
#
#   .\installer.ps1                 build installer for the current publish\
#   .\installer.ps1 -Publish        republish first, then build the installer
#   .\installer.ps1 -Run            ... and start the finished installer
#
# The payload and the output folder are redirectable, which is how the local dev pipeline
# (devbuild.ps1) packages the Desktop\Optima Dev folder instead of publish\:
#
#   .\installer.ps1 -Publish -DevEdition -Source "C:\...\Optima Dev" -OutDir "C:\...\Optima Dev" -Label "-dev-20261002-2312"
#
# Produces <OutDir>\Optima-Setup-<version><Label>.exe. The setup is per-user: it
# installs to %LOCALAPPDATA%\Programs\Optima without an administrator prompt,
# adds Start Menu shortcuts and an optional sign-in autostart, and registers a
# proper uninstaller in Add/Remove Programs.
#
# The Inno Setup compiler is fetched once into tools\InnoSetup (portable mode,
# nothing installed machine-wide); its presence is verified before use.

param(
    # Refresh the payload from source before packaging it.
    [switch]$Publish,
    # Start the produced installer after a successful build.
    [switch]$Run,
    # Payload to package: publish\ by default, or any folder (the dev pipeline passes the
    # Desktop\Optima Dev folder).
    [string]$Source = "publish",
    # Where the setup lands: artifacts\ by default.
    [string]$OutDir = "artifacts",
    # Filename suffix carried by this build, so local builds never collide.
    [string]$Label = "",
    # Dev edition: compiles in the DEVELOPER EMULATOR settings section (used only with -Publish).
    [switch]$DevEdition,
    # Publish configuration (used only with -Publish).
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64"
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $MyInvocation.MyCommand.Path

# Absolute paths are taken as given; relative ones are resolved against the repo root.
function Resolve-FromRoot([string]$path) {
    if ([System.IO.Path]::IsPathRooted($path)) { return $path }
    return Join-Path $root $path
}

$sourceDir = Resolve-FromRoot $Source
$outDir = Resolve-FromRoot $OutDir

# --- The publish payload -----------------------------------------------------

if ($Publish) {
    $publishArgs = @{ Configuration = $Configuration; Runtime = $Runtime; Output = $sourceDir }
    if ($DevEdition) { $publishArgs.DevEdition = $true }
    & (Join-Path $root "publish.ps1") @publishArgs
    if ($LASTEXITCODE -ne 0) { throw "publish.ps1 failed (exit $LASTEXITCODE)" }
}

if (-not (Test-Path (Join-Path $sourceDir "Optima.exe"))) {
    throw "$sourceDir\Optima.exe not found. Run .\installer.ps1 -Publish first (or publish.ps1, then installer.ps1)."
}

# Version for the setup filename and Add/Remove Programs entry; the app's own
# msbuild property is the single source of truth.
$version = dotnet msbuild (Join-Path $root "src\Optima.App\Optima.App.csproj") -getProperty:Version -nologo | Select-Object -Last 1
if ($LASTEXITCODE -ne 0 -or -not $version) { throw "could not read the app version" }
$version = $version.Trim()
Write-Host "Optima version: $version"

# --- The compiler ------------------------------------------------------------

# Pinned minor: same tool, byte for byte, on every machine and CI run.
$innoVersion = "7.1.0"
$innoDir = Join-Path $root "tools\InnoSetup"
$iscc = Join-Path $innoDir "ISCC.exe"

if (-not (Test-Path $iscc)) {
    Write-Host "fetching the Inno Setup $innoVersion compiler into tools\InnoSetup (portable, one-time)"
    $archive = Join-Path $root "tools\innosetup-$innoVersion-x64.exe"
    $url = "https://github.com/jrsoftware/issrc/releases/download/is-${innoVersion.replace('.', '_')}/innosetup-$innoVersion-x64.exe"
    New-Item -ItemType Directory -Force (Join-Path $root "tools") | Out-Null
    [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
    Invoke-WebRequest -Uri $url -OutFile $archive
    New-Item -ItemType Directory -Force $innoDir | Out-Null
    # Portable mode: unpacks the compiler into $innoDir without installing.
    Start-Process -FilePath $archive -ArgumentList "/PORTABLE=1 /VERYSILENT /SUPPRESSMSGBOXES /NORESTART /DIR=`"$innoDir`"" -Wait
    Remove-Item $archive -ErrorAction SilentlyContinue
    if (-not (Test-Path $iscc)) { throw "Inno Setup did not unpack into $innoDir" }
}

# --- Compile -----------------------------------------------------------------

Write-Host "compiling the installer from $sourceDir"
# Compile from the repo root so the script's relative paths (the LICENSE, the icon)
# resolve regardless of the caller's working directory.
Push-Location $root
try {
    & $iscc "/DAppVersion=$version" "/DSourceDir=$sourceDir" "/DOutDir=$outDir" "/DLabel=$Label" "installer.iss"
    if ($LASTEXITCODE -ne 0) { throw "ISCC failed (exit $LASTEXITCODE)" }
} finally {
    Pop-Location
}

$setup = Join-Path $outDir "Optima-Setup-$version$Label.exe"
Write-Host "done: $setup"

if ($Run) {
    Start-Process -FilePath $setup
    Write-Host "started $setup"
}
