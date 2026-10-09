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
; Install model: per machine. Setup asks for administrator rights once and puts everything in
; Program Files. That is where it has to be: Optima's helper runs as administrator, and a helper,
; its libraries and the driver package in a folder the user's own programs can write to is
; administrator rights for anything that drops a file there. In Program Files only an
; administrator can change them. The same prompt covers the bundled virtual display driver, so
; there is no second one. Optima itself is started as the person who ran the setup, never elevated.
;
; User data stays where it was, under %LOCALAPPDATA%\Optima\ (config, profiles, sessions): setup
; and uninstall never touch it, and uninstall leaves the installed driver as well.
;
; Coming from 0.7.5 and earlier, which installed per user: the copy in
; %LOCALAPPDATA%\Programs\Optima is removed by this setup, with its shortcuts and its entry in
; Add/Remove Programs. The autostart entry is left for Optima to point at the new folder on its
; first start.
;
; Upgrading: a setup run over an existing install keeps the folder it finds
; (UsePreviousAppDir) and empties it before laying the new payload down, so files the
; new build no longer ships cannot linger beside it.
;
; Updates from inside Optima run this setup with /SILENT /relaunch=1: no pages, and Optima is
; started again at the end.

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
; Administrator rights, so {autopf} is Program Files. 64-bit mode makes that the real
; Program Files and not the (x86) one: the payload is a 64-bit program.
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
DefaultDirName={autopf}\{#AppName}
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
; Names the build in Add/Remove Programs, so a machine carrying a local dev build says which one.
UninstallDisplayName={#AppName} {#AppVersion}{#Label}
; Setup does not close Optima for the player. A closed launcher is a session ended with nothing
; put back; PrepareToInstall waits for Optima to go and stops with a message when it does not.
CloseApplications=no
RestartApplications=no

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"
; Checked by default: the virtual display only works once the driver is installed. Setup is
; already running as administrator, so this asks for nothing more.
Name: "vdddriver"; Description: "Install the Optima virtual display driver"; GroupDescription: "Virtual display:"

[Files]
; The local dev pipeline keeps the setups it builds in an Installers subfolder of the payload; they
; are excluded, together with any setup left in the payload root, so a setup never embeds another
; setup. (createallsubdirs is deliberately absent: it would recreate that folder, empty, inside the
; installed app.)
Source: "{#SourceDir}\*"; DestDir: "{app}"; Excludes: "Installers\*,Optima-Setup-*.exe"; Flags: recursesubdirs ignoreversion

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
;
; Only a folder that already holds Optima is cleared. The folder page is hidden, but /DIR= on
; the command line still chooses the folder, and pointed at one with other things in it this
; line would have emptied it.
Type: filesandordirs; Name: "{app}\*"; Check: AppFolderHoldsOptima

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\Optima.exe"; WorkingDir: "{app}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\Optima.exe"; WorkingDir: "{app}"; Tasks: desktopicon

[Run]
; As the person who started the setup, in both entries: setup runs as administrator, and an
; Optima started by it without this would be an elevated Optima.
Filename: "{app}\Optima.exe"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent runasoriginaluser
; The update from inside Optima: a silent run has no last page to tick a box on, so it asks for
; the restart on the command line.
Filename: "{app}\Optima.exe"; Flags: nowait runasoriginaluser; Check: RelaunchRequested

[Code]
const
  RunKey = 'Software\Microsoft\Windows\CurrentVersion\Run';
  AutostartValueName = 'Optima';
  SingleInstanceMutex = 'Local\Optima.SingleInstance';
  BundledDriverFolder = 'drivers';
  ElevatedHelperFileName = 'Optima.Watchdog.exe';
  DriverTaskName = 'vdddriver';

{ True when the install folder is an Optima install, which is the only kind of folder the
  InstallDelete section may clear. A first install finds no folder, and has nothing to clear. }
function AppFolderHoldsOptima(): Boolean;
begin
  Result := FileExists(ExpandConstant('{app}\Optima.exe'));
end;

{ True when Optima was the one that started this setup and wants to be started again. }
function RelaunchRequested(): Boolean;
begin
  Result := ExpandConstant('{param:relaunch|0}') = '1';
end;

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

{ One value of the previous install's uninstall entry, or an empty string when there is no
  previous install. Three places can hold one: this setup's own (machine-wide, 64-bit), the
  machine-wide one an "all users" 0.7.5 wrote (32-bit), and the per-user one of 0.7.5 and
  earlier. The last is only visible when the person at the keyboard is the administrator.

  Read from the registry rather than from the install folder, because this is asked on
  the welcome page and the app constant is not initialized that early: expanding it there
  raises "An attempt was made to expand the app constant before it was initialized" and
  takes the whole setup down with a runtime error. The registry answers the same question
  at any point in the run. }
function PreviousInstallValue(Name: String): String;
begin
  Result := '';
  if not RegQueryStringValue(HKLM64, UninstallKey(), Name, Result) then
    if not RegQueryStringValue(HKLM32, UninstallKey(), Name, Result) then
      RegQueryStringValue(HKEY_CURRENT_USER, UninstallKey(), Name, Result);
end;

function PreviousInstallDir(): String;
begin
  Result := PreviousInstallValue('InstallLocation');
end;

{ True when a previous Optima install is on this PC, so this run replaces one rather
  than making a fresh install. Safe to call from the wizard, including the welcome page. }
function IsUpgrade(): Boolean;
begin
  Result := PreviousInstallDir() <> '';
end;

{ The exact command line AutostartService writes. Always quoted, like the app's own toggle. }
function AutostartCommandLine(): String;
begin
  Result := '"' + ExpandConstant('{app}\Optima.exe') + '" --tray';
end;

{ True when the existing autostart Run value points at this very install. Only the uninstaller
  asks: the entry itself belongs to the app (its Settings page and first-run wizard write it),
  and setup no longer offers it. }
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

  This only rewrites the welcome text; the replacement itself happens whether or not a
  wizard is ever shown (a silent install has no pages). }
procedure CurPageChanged(CurPageID: Integer);
var
  Previous: String;
begin
  if (CurPageID = wpWelcome) and IsUpgrade() then
  begin
    Previous := PreviousInstallValue('DisplayVersion');
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

  Setup is already running as administrator, so the helper is started directly and Windows
  asks nothing further. The helper only stages the package when a device with the driver's
  hardware id is already present, so running a new setup over an existing install never adds
  a second virtual display. }
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
  if Exec(HelperPath, '--install-driver "' + DriverFolder + '"',
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
    it later with one administrator prompt. }
  if not WizardSilent() then
    MsgBox('Optima could not install its virtual display driver automatically.' + #13#10 + #13#10 +
           'Install it from inside Optima instead: open the DISPLAY page and choose ' +
           '"Install driver". Everything else in Optima works without it.',
           mbInformation, MB_OK);
end;

// Optima Shield runs beside Optima while a session is up. Before its file is replaced or removed
// it is asked to send its final report and exit. It would also exit by itself a few seconds after
// Optima is gone; asking makes the wait short and the session's end clean.
procedure StopShield(Dir: String);
var
  Code: Integer;
  Exe: String;
begin
  if Dir = '' then
    Exit;
  Exe := AddBackslash(Dir) + 'Optima.Shield.exe';
  if FileExists(Exe) then
    Exec(Exe, '--stop', Dir, SW_HIDE, ewWaitUntilTerminated, Code);
end;

// The copy 0.7.5 and earlier installed for one person, in that person's own folder.
//
// It is removed as that person and not as the administrator this setup runs as. The two are
// the same account on most PCs, but where a standard account borrowed an administrator's
// password for the prompt they are not, and "the local app data folder" and "the current
// user's registry" then mean the administrator's. Run as the original user, both mean the
// player's.
//
// One PowerShell command, because every path in it has to be worked out on that side: the
// folder (only when it holds Optima.exe, never a folder that is merely called Optima), the two
// shortcuts, and the entry in Add/Remove Programs. The old uninstaller is deliberately not
// used: it would take the autostart entry with it, and that entry is the player's choice,
// which Optima points at the new folder on its first start.
procedure RemovePerUserInstall();
var
  Code: Integer;
  Script: String;
begin
  Script :=
    '$d = Join-Path $env:LOCALAPPDATA ''Programs\Optima''; ' +
    'if (Test-Path -LiteralPath (Join-Path $d ''Optima.exe'')) { ' +
      '$s = Join-Path $d ''Optima.Shield.exe''; ' +
      'if (Test-Path -LiteralPath $s) { Start-Process -FilePath $s -ArgumentList ''--stop'' -WindowStyle Hidden -Wait }; ' +
      'Remove-Item -LiteralPath $d -Recurse -Force -ErrorAction SilentlyContinue; ' +
      'foreach ($f in ''Programs'', ''Desktop'') { ' +
        'Remove-Item -LiteralPath (Join-Path ([Environment]::GetFolderPath($f)) ''Optima.lnk'') -Force -ErrorAction SilentlyContinue }; ' +
      'Remove-Item -LiteralPath ''HKCU:\' + UninstallKey() + ''' -Recurse -Force -ErrorAction SilentlyContinue }';
  if not ExecAsOriginalUser(ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'),
       '-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command "' + Script + '"',
       '', SW_HIDE, ewWaitUntilTerminated, Code) then
    Log('The per-user copy of an earlier Optima could not be looked for.')
  else
    Log(Format('Looked for a per-user copy of an earlier Optima (exit code %d).', [Code]));
end;

// The copy a 0.7.5 setup installed "for all users": Program Files (x86), recorded in the
// 32-bit half of the registry. When this setup took that folder over as its own there is only
// the old entry to remove, or Add/Remove Programs would list Optima twice. When it did not,
// the old folder goes as well, under the same rule as everywhere: only a folder that holds
// Optima.exe.
procedure RemoveOldMachineInstall();
var
  Dir: String;
begin
  if not RegQueryStringValue(HKLM32, UninstallKey(), 'InstallLocation', Dir) then
    Exit;
  Dir := RemoveBackslashUnlessRoot(Dir);
  if (Dir <> '') and (CompareText(Dir, RemoveBackslashUnlessRoot(ExpandConstant('{app}'))) <> 0)
     and FileExists(AddBackslash(Dir) + 'Optima.exe') then
  begin
    StopShield(Dir);
    DelTree(Dir, True, True, True);
    Log('Removed the earlier all-users copy in ' + Dir + '.');
  end;
  RegDeleteKeyIncludingSubkeys(HKLM32, UninstallKey());
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssInstall then
  begin
    { [InstallDelete] has already run by now, so this records what was replaced. The app
      constant is initialized by this point, which it is not on the wizard's first page. }
    if IsUpgrade() then
      Log('Replacing an existing Optima install in ' + ExpandConstant('{app}') + '.');
    RemovePerUserInstall();
    RemoveOldMachineInstall();
  end;

  if CurStep = ssPostInstall then
  begin
    { The task is checked by default, and silent installs keep that default, so a run of the
      setup always tries to install the driver. Unattended installs that want none of it can
      pass /MERGETASKS=!vdddriver, and the checkbox does the same in the wizard. }
    if WizardIsTaskSelected(DriverTaskName) then
      InstallBundledVirtualDisplayDriver();
  end;
end;

// Also the answer for a run with no pages (an update started from inside Optima, which closes
// itself right after starting this): the wizard's own "still running" question is never asked
// there. Optima gets fifteen seconds to be gone, and setup stops with a reason when it is not.
function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  Waited: Integer;
begin
  Result := '';
  Waited := 0;
  while OptimaIsRunning() and (Waited < 30) do
  begin
    Sleep(500);
    Waited := Waited + 1;
  end;
  if OptimaIsRunning() then
  begin
    Result := 'Optima is still running. Close it (also from the tray), then run this setup again.';
    Exit;
  end;
  StopShield(PreviousInstallDir());
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usUninstall then
  begin
    StopShield(ExpandConstant('{app}'));
    if AutostartPointsAtThisInstall() then
      RegDeleteValue(HKEY_CURRENT_USER, RunKey, AutostartValueName);
  end;
end;
