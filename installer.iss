; Optima setup script (Inno Setup 7), compiled by installer.ps1:
;
;   tools\InnoSetup\ISCC.exe /DAppVersion=<version> installer.iss
;
;   tools\InnoSetup\ISCC.exe /DSourceDir=<payload> /DOutDir=<folder> /DLabel=<suffix> installer.iss
;
; The script packs whatever the payload folder currently holds (publish\ by default), so run
; publish.ps1 first (installer.ps1 does both steps). Produces
; <OutDir>\Optima-Setup-<version><Label>.exe. The local dev pipeline points both at the
; Desktop\Optima Dev folder and stamps Label with the build time.
;
; Install model: per-user, so the wizard itself never asks for administrator rights:
; files go to %LOCALAPPDATA%\Programs\Optima and registry writes are HKCU only. The
; one step that does need them is the bundled virtual display driver, so a task
; (checked by default) hands that to Optima's own elevated helper once the files are
; in place: one UAC prompt. Declining it still leaves a working Optima, because the
; Display page offers the same install later. Uninstall removes the files, the
; shortcuts and Optima's own registry values, but never touches user data under
; %LOCALAPPDATA%\Optima\ (config, profiles, sessions) or the installed driver.

#ifndef AppVersion
  ; Fallback for compiling outside installer.ps1 (e.g. from the Inno IDE) when
  ; publish\Optima.exe has not been built yet.
  #define AppVersion "dev"
#endif

#define AppName "Optima"

; Payload and output are redirectable; the defaults are the repo's own folders. The local dev
; pipeline passes the Desktop\Optima Dev folder for both so a dev build and the setup made from
; it sit together.
#ifndef SourceDir
  #define SourceDir "publish"
#endif
#ifndef OutDir
  #define OutDir "artifacts"
#endif
#ifndef Label
  ; Filename suffix that tells one local build from the next, e.g. "-dev-20261002-2312".
  #define Label ""
#endif

; A setup that ships without the driver package and the helper that installs it would bring
; back exactly the bug this script now fixes, so refuse to build one.
#if !DirExists(AddBackslash(SourceDir) + "drivers")
  #error The payload has no drivers folder: run publish.ps1 first, or use installer.ps1 -Publish.
#endif
#if !FileExists(AddBackslash(SourceDir) + "Optima.Watchdog.exe")
  #error The payload has no Optima.Watchdog.exe: the driver install needs the elevated helper.
#endif

[Setup]
AppId={{f4474d52-ee70-458e-b99d-5c3eef769b1c}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher=Inspect Software
DefaultDirName={autopf}\{#AppName}
; {autopf} resolves per-user to %LOCALAPPDATA%\Programs when the installer runs
; as the unprivileged user (PrivilegesRequired=lowest).
DisableProgramGroupPage=yes
; Fixed install location; the folder is not asked about.
DisableDirPage=yes
UsePreviousAppDir=yes
UsePreviousTasks=yes
LicenseFile=LICENSE
OutputDir={#OutDir}
OutputBaseFilename=Optima-Setup-{#AppVersion}{#Label}
SetupIconFile=src\Optima.App\Assets\optima.ico
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
; Names the build in Add/Remove Programs, so a machine carrying a local dev build says which one.
UninstallDisplayName={#AppName} {#AppVersion}{#Label}
; Let Setup ask Windows to close Optima when its files are being replaced
; (upgrade in place) and relaunch it afterwards if it was running.
CloseApplicationsFilter=*.exe,*.dll
RestartApplications=no

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"
; Start-with-Windows is written as the same HKCU Run value the app's own
; Settings toggle manages (AutostartService), so installer and app never
; disagree about the mechanism.
Name: "autostart"; Description: "Start Optima automatically at sign-in (minimized to the tray)"; GroupDescription: "Startup:"
; A virtual display is a machine-wide device, so this step alone needs Windows to
; approve an administrator prompt. Checked by default: the Optima Virtualization
; features only work once the driver is installed.
Name: "vdddriver"; Description: "Install the Optima virtual display driver (Windows asks once for administrator approval)"; GroupDescription: "Virtual display:"

[Files]
; Setups built earlier from this same folder are excluded: the local dev pipeline writes them next
; to the payload, and without this each setup would embed a copy of the previous one.
Source: "{#SourceDir}\*"; DestDir: "{app}"; Excludes: "Optima-Setup-*.exe"; Flags: recursesubdirs createallsubdirs ignoreversion restartreplace

[Icons]
Name: "{userprograms}\{#AppName}"; Filename: "{app}\Optima.exe"; WorkingDir: "{app}"
Name: "{userdesktop}\{#AppName}"; Filename: "{app}\Optima.exe"; WorkingDir: "{app}"; Tasks: desktopicon

[Run]
Filename: "{app}\Optima.exe"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent

[Code]
const
  RunKey = 'Software\Microsoft\Windows\CurrentVersion\Run';
  AutostartValueName = 'Optima';
  SingleInstanceMutex = 'Local\Optima.SingleInstance';
  BundledDriverFolder = 'drivers';
  ElevatedHelperFileName = 'Optima.Watchdog.exe';
  DriverTaskName = 'vdddriver';

{ True when Optima currently holds its single-instance mutex. }
function OptimaIsRunning(): Boolean;
begin
  Result := CheckForMutexes(SingleInstanceMutex);
end;

{ The exact command line AutostartService writes, so installer and app never
  disagree about the value's shape. Always quoted, like the app's own toggle. }
function AutostartCommandLine(): String;
begin
  Result := '"' + ExpandConstant('{app}\Optima.exe') + '" --tray';
end;

{ True when the existing autostart Run value points at this very install, so
  it is ours to manage. A value left by another (manually installed) copy is
  never touched. Accepts the app's quoted form and an unquoted legacy form. }
function AutostartPointsAtThisInstall(): Boolean;
var
  Value: String;
begin
  Result := False;
  if RegQueryStringValue(HKEY_CURRENT_USER, RunKey, AutostartValueName, Value) then
  begin
    Value := LowerCase(Trim(Value));
    Result :=
      (Value = LowerCase(AutostartCommandLine())) or
      (Value = LowerCase(ExpandConstant('{app}\Optima.exe') + ' --tray'));
  end;
end;

{ Refuse to install over a running instance: the game companion watches files,
  and a half-replaced install can confuse a live session. Retry/cancel loop. }
function NextButtonClick(CurPageID: Integer): Boolean;
begin
  Result := True;
  if (CurPageID = wpWelcome) and not WizardSilent() then
  begin
    while OptimaIsRunning() do
      if MsgBox('Optima is still running. Close it first, then choose Retry to continue the installation.',
                mbError, MB_RETRYCANCEL) = IDCANCEL then
      begin
        Result := False;
        Exit;
      end;
  end;
end;

{ Installs the bundled virtual display driver.

  Setup runs as the unprivileged user, and creating a display device node is not
  something a per-user process may do, so the elevated helper does the work: its
  manifest makes Windows show one administrator prompt. The helper only stages the
  package when a device with the driver's hardware id is already present, so running
  a new setup over an existing install never adds a second virtual display. }
procedure InstallBundledVirtualDisplayDriver();
var
  ResultCode: Integer;
  DriverFolder, HelperPath: String;
begin
  DriverFolder := ExpandConstant('{app}\') + BundledDriverFolder;
  if not DirExists(DriverFolder) then
  begin
    Log('No bundled driver folder, skipping the virtual display driver install.');
    Exit;
  end;

  HelperPath := ExpandConstant('{app}\') + ElevatedHelperFileName;
  if not FileExists(HelperPath) then
  begin
    Log('No elevated helper next to Optima.exe, skipping the virtual display driver install.');
    Exit;
  end;

  Log('Installing the bundled virtual display driver.');
  if ShellExec('runas', HelperPath, '--install-driver "' + DriverFolder + '"',
               ExpandConstant('{app}'), SW_HIDE, ewWaitUntilTerminated, ResultCode) then
  begin
    if ResultCode = 0 then
    begin
      Log('Virtual display driver installed.');
      Exit;
    end;
    if ResultCode = 2 then
    begin
      Log('The bundled folder holds no driver package for this architecture.');
      Exit;
    end;
    Log(Format('The virtual display driver install failed (exit code %d).', [ResultCode]));
  end
  else
    Log('The elevated helper for the virtual display driver could not be started.');

  { Optional by design: Optima runs without the driver, and the Display page installs
    it later with the same single administrator prompt. }
  if not WizardSilent() then
    MsgBox('Optima could not install its virtual display driver automatically.' + #13#10 + #13#10 +
           'Install it from inside Optima instead: open the DISPLAY page and choose ' +
           '"Install driver". Everything else in Optima works without it.',
           mbInformation, MB_OK);
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then
  begin
    { The task is checked by default, and silent installs keep that default, so a run of the
      setup always tries to install the driver. Unattended installs that want none of it can
      pass /MERGETASKS=!vdddriver, and the checkbox does the same in the wizard. }
    if WizardIsTaskSelected(DriverTaskName) then
      InstallBundledVirtualDisplayDriver();

    if WizardIsTaskSelected('autostart') then
      RegWriteStringValue(HKEY_CURRENT_USER, RunKey, AutostartValueName, AutostartCommandLine())
    else if AutostartPointsAtThisInstall() then
      RegDeleteValue(HKEY_CURRENT_USER, RunKey, AutostartValueName);
  end;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usUninstall then
  begin
    if AutostartPointsAtThisInstall() then
      RegDeleteValue(HKEY_CURRENT_USER, RunKey, AutostartValueName);
  end;
end;
