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
;
; Upgrading: a setup run over an existing install keeps the folder it finds
; (UsePreviousAppDir) and empties it before laying the new payload down, so files the
; new build no longer ships cannot linger beside it. Only the install folder is
; cleared; the uninstaller is rewritten in place and the user's data, which lives
; outside {app}, is never touched.

#ifndef AppVersion
  ; Fallback for compiling outside installer.ps1 (e.g. from the Inno IDE) when
  ; publish\Optima.exe has not been built yet.
  #define AppVersion "dev"
#endif

#define AppName "Optima"

; The application's identity, as the uninstall key the script reads to find a previous
; install. The AppId below must stay exactly as written: it is the value Inno recorded for
; every install already on disk, and it is what makes a new setup recognize them. Changing
; its braces or spelling would strand existing installs as unrelated applications.
#define AppGuid "f4474d52-ee70-458e-b99d-5c3eef769b1c"

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
; Names the build in Add/Remove Programs, so a machine carrying a local dev build says which one.
UninstallDisplayName={#AppName} {#AppVersion}{#Label}
; Let Setup ask Windows to close Optima when its files are being replaced
; (upgrade in place). It is not relaunched afterwards.
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
; The local dev pipeline keeps the setups it builds in an Installers subfolder of the payload; they
; are excluded, together with any setup left in the payload root, so a setup never embeds another
; setup. (createallsubdirs is deliberately absent: it would recreate that folder, empty, inside the
; installed app.)
Source: "{#SourceDir}\*"; DestDir: "{app}"; Excludes: "Installers\*,Optima-Setup-*.exe"; Flags: recursesubdirs ignoreversion restartreplace

[InstallDelete]
; Replace, do not merge. Inno's file list only adds and overwrites, so a build that renamed
; or dropped a file would leave the old copy sitting next to the new one, and a stale
; assembly beside Optima.exe is how an upgrade ends up running half of the previous version.
; This runs before [Files] lays the payload down.
;
; Clearing {app}\* is safe because the install folder holds the payload and nothing else:
; settings, profiles, sessions and logs live under %LOCALAPPDATA%\Optima, and the virtual
; display driver is a machine-wide device rather than a file in here. Inno rewrites its own
; uninstaller after this point, so the entry in Add/Remove Programs survives with the new
; version. (No Excludes here on purpose: [InstallDelete] does not support one, and it does
; not need it - the uninstaller is restored either way.)
Type: filesandordirs; Name: "{app}\*"

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

{ The uninstall entry Inno records for this application, so a previous install can be
  found from its own bookkeeping.

  The opening and closing braces are built with Chr(123)/Chr(125) rather than written
  literally: a literal brace in this script starts a constant as far as the preprocessor
  is concerned, which is also why the comments in this section use // instead of the
  brace-delimited comment style used elsewhere in the file. }
function UninstallKey(): String;
begin
  Result := 'Software\Microsoft\Windows\CurrentVersion\Uninstall\' +
            Chr(123) + '{#AppGuid}' + Chr(125) + '_is1';
end;

{ Where the previous install lives, or an empty string when there is none.

  Read from the registry rather than from the install folder, because this is asked on
  the welcome page and the app constant is not initialized that early: expanding it there
  raises "An attempt was made to expand the app constant before it was initialized" and
  takes the whole setup down with a runtime error. The registry answers the same question
  at any point in the run. }
function PreviousInstallDir(): String;
begin
  Result := '';
  RegQueryStringValue(HKEY_CURRENT_USER, UninstallKey(), 'InstallLocation', Result);
end;

{ True when a previous Optima install is on this PC, so this run replaces one rather
  than making a fresh install. Safe to call from the wizard, including the welcome page. }
function IsUpgrade(): Boolean;
begin
  Result := PreviousInstallDir() <> '';
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

{ Say up front what an upgrade is about to do, so replacing an existing install is a
  stated part of the run rather than something noticed afterwards in the folder.

  This only rewrites the welcome text; the replacement itself is [InstallDelete], which
  runs whether or not a wizard is ever shown (a silent install has no pages). }
procedure CurPageChanged(CurPageID: Integer);
var
  Previous: String;
begin
  if (CurPageID = wpWelcome) and IsUpgrade() then
  begin
    Previous := '';
    RegQueryStringValue(HKEY_CURRENT_USER, UninstallKey(), 'DisplayVersion', Previous);
    if Previous <> '' then
      Previous := ' ' + Previous;

    WizardForm.WelcomeLabel2.Caption :=
      'Setup found Optima' + Previous + ' already installed on this PC and will replace it with version {#AppVersion}.' + #13#10 + #13#10 +
      'The old files are removed first, so nothing from the previous build is left behind. ' +
      'Your settings, profiles and session history are kept.' + #13#10 + #13#10 +
      'Click Next to continue, or Cancel to keep the current install.';
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
  if CurStep = ssInstall then
  begin
    { [InstallDelete] has already run by now, so this records what was replaced. The app
      constant is initialized by this point, which it is not on the wizard's first page. }
    if IsUpgrade() then
      Log('Replacing an existing Optima install in ' + ExpandConstant('{app}') + '.');
  end;

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
