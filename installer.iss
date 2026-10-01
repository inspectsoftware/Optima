; Optima setup script (Inno Setup 7), compiled by installer.ps1:
;
;   tools\InnoSetup\ISCC.exe /DAppVersion=<version> installer.iss
;
; The script packs whatever publish\ currently holds, so run publish.ps1 first
; (installer.ps1 does both steps). Produces artifacts\Optima-Setup-<version>.exe.
;
; Install model: per-user, no administrator prompt. Files go to
; %LOCALAPPDATA%\Programs\Optima; registry writes are HKCU only. Uninstall
; removes the files, the shortcuts and Optima's own registry values, but never
; touches user data under %LOCALAPPDATA%\Optima\ (config, profiles, sessions).

#ifndef AppVersion
  ; Fallback for compiling outside installer.ps1 (e.g. from the Inno IDE) when
  ; publish\Optima.exe has not been built yet.
  #define AppVersion "dev"
#endif

#define AppName "Optima"

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
OutputDir=artifacts
OutputBaseFilename=Optima-Setup-{#AppVersion}
SetupIconFile=src\Optima.App\Assets\optima.ico
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
UninstallDisplayName={#AppName}
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

[Files]
Source: "publish\*"; DestDir: "{app}"; Flags: recursesubdirs createallsubdirs ignoreversion restartreplace

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

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then
  begin
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
