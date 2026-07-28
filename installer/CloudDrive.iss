; CloudDrive installer (Inno Setup 6+)
;
; Build with: scripts\build-installer.ps1
;
; What this has to get right beyond copying files:
;   * install WinFsp if it is missing, because drive mounts fail without it;
;   * register the service so mounts exist before anyone signs in;
;   * put the managed tools directory on the machine PATH, and take it off again on uninstall;
;   * support /VERYSILENT /RESTARTSERVICE, because the in-app updater runs this unattended.

#define AppName        "CloudDrive"
#define AppPublisher   "RHC Solutions"
#define AppUrl         "https://rhcsolutions.com/"
#define AppExe         "CloudDrive.exe"
#define ServiceExe     "CloudDrive.Service.exe"
#define ServiceName    "CloudDrive"

#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif

#ifndef SourceDir
  #define SourceDir "..\publish"
#endif

[Setup]
AppId={{7C4E2A96-3B5D-4F18-9E7A-0D6C1B8F2E43}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher={#AppPublisher}
AppPublisherURL={#AppUrl}
AppSupportURL=https://github.com/RHC-Solutions/CloudDrive
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
OutputDir=output
OutputBaseFilename=CloudDrive-Setup
SetupIconFile=..\src\CloudDrive.App\Assets\clouddrive.ico
UninstallDisplayIcon={app}\{#AppExe}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
; The service runs as LocalSystem and the tools directory goes on the machine PATH, so this is
; per-machine and needs elevation. There is no per-user mode to fall back to.
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
; Server Core has no shell but is a supported target, so the installer must not assume a desktop.
MinVersion=10.0.14393
CloseApplications=yes
RestartApplications=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "service";   Description: "Install the CloudDrive service (recommended)"; GroupDescription: "Setup:"
; No "start at sign-in" task: that is a per-user setting the app owns, and it defaults to on. Offering
; it here would imply the installer can set it for the eventual user, which an elevated install cannot.
Name: "path";      Description: "Add the CloudDrive tools directory to PATH";   GroupDescription: "Setup:"
Name: "desktopicon"; Description: "Create a desktop shortcut";                  GroupDescription: "Setup:"; Flags: unchecked

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "..\third_party\winfsp\winfsp.msi"; DestDir: "{tmp}"; Flags: deleteafterinstall; Check: NeedsWinFsp

[Icons]
Name: "{group}\{#AppName}";           Filename: "{app}\{#AppExe}"
Name: "{group}\Uninstall {#AppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#AppName}";     Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Run]
; WinFsp first: the service will try to mount as soon as it starts, and without the driver every
; drive-letter mapping fails.
Filename: "msiexec.exe"; Parameters: "/i ""{tmp}\winfsp.msi"" /qn /norestart"; \
  StatusMsg: "Installing WinFsp..."; Flags: waituntilterminated; Check: NeedsWinFsp

; The service is registered from [Code] instead of here. An entry in this section runs without its
; exit code ever being examined, so a failure to register the service -- the whole point of the
; product -- produced a setup that reported complete success and left no service behind.

; Launching the tray app is skipped in silent mode, which is how the in-app updater runs this — a
; window appearing on an unattended server would be wrong.
Filename: "{app}\{#AppExe}"; Description: "Start CloudDrive"; \
  Flags: nowait postinstall skipifsilent

[UninstallRun]
Filename: "{app}\cdrive.exe"; Parameters: "service uninstall"; \
  Flags: runhidden waituntilterminated; RunOnceId: "RemoveService"

; No [Registry] section, deliberately.
;
; "Start at sign-in" is an HKCU\...\Run value, and this installer runs elevated. An HKCU write from an
; administrative install lands in the hive of whoever approved the UAC prompt — frequently not the
; person who will use CloudDrive — so the entry would be created for the wrong account and the right
; one would never launch the app. Inno Setup warns about this ("UsedUserAreasWarning") and the warning
; is correct.
;
; The tray app registers itself instead, from StartupRegistration, running unelevated as the real user
; and driven by the StartAtLogin setting. It also repairs a stale entry after an upgrade moves the
; executable, which a one-shot installer write could never do.

[Code]
var
  RestartServiceAfterInstall: Boolean;

{ WinFsp registers itself under the 32-bit view even on x64, because its installer is 32-bit.
  Checking only the native view finds nothing on a 64-bit machine, which looks exactly like
  "not installed" and would reinstall the driver on every upgrade. }
function IsWinFspInstalled: Boolean;
begin
  Result := RegKeyExists(HKLM32, 'SOFTWARE\WinFsp') or RegKeyExists(HKLM, 'SOFTWARE\WinFsp');
end;

function NeedsWinFsp: Boolean;
begin
  Result := not IsWinFspInstalled;
end;

{ True when the service is registered. Checked directly rather than inferred from an exit code. }
function ServiceExists: Boolean;
begin
  Result := RegKeyExists(HKLM, 'SYSTEM\CurrentControlSet\Services\{#ServiceName}');
end;

{ Says so, rather than letting the user find out when nothing mounts.

  Silent during an unattended install, which is how the in-app updater runs setup: a modal dialog
  on a server nobody is watching would hang the update indefinitely. The reason still reaches the
  setup log in that case. }
procedure ReportServiceFailure(Message: String);
begin
  if WizardSilent then
    Log('CloudDrive: ' + Message)
  else
    MsgBox(Message + #13#10#13#10 +
           'CloudDrive is installed, but drive mappings cannot mount until the service is running.' + #13#10 +
           'Register it by hand from an elevated prompt.' + #13#10#13#10 +
           'PowerShell (note the leading &):' + #13#10 +
           '    & "' + ExpandConstant('{app}') + '\cdrive.exe" service install' + #13#10#13#10 +
           'Command Prompt:' + #13#10 +
           '    "' + ExpandConstant('{app}') + '\cdrive.exe" service install',
           mbError, MB_OK);
end;

function InitializeSetup: Boolean;
begin
  { The in-app updater passes /RESTARTSERVICE so the service comes back after the swap. It is a
    custom switch rather than a task, because a silent install runs no task selection. }
  RestartServiceAfterInstall := ExpandConstant('{param:RESTARTSERVICE|no}') <> 'no';
  Result := True;
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  ResultCode: Integer;
begin
  if CurStep = ssInstall then
  begin
    { Stop the service before overwriting its binaries. Without this the file copy fails on a
      running install, which is exactly the path an auto-update takes. }
    Exec(ExpandConstant('{sys}\sc.exe'), 'stop {#ServiceName}', '',
         SW_HIDE, ewWaitUntilTerminated, ResultCode);
    Sleep(2000);
  end;

  if CurStep = ssPostInstall then
  begin
    if WizardIsTaskSelected('path') then
    begin
      { Delegated to the CLI rather than done with [Registry], because appending to PATH safely
        means reading the raw REG_EXPAND_SZ value without expanding it — writing back an expanded
        copy would bake %SystemRoot% into the machine PATH permanently. }
      Exec(ExpandConstant('{app}\cdrive.exe'), 'tools path --register', '',
           SW_HIDE, ewWaitUntilTerminated, ResultCode);
    end;

    { Register the service, then verify two separate things: that the command succeeded, and that
      a service actually exists afterwards. A non-zero exit is the obvious failure; a zero exit
      with nothing registered is worse, because it looks like success. }
    if WizardIsTaskSelected('service') then
    begin
      if not Exec(ExpandConstant('{app}\cdrive.exe'), 'service install', '',
                  SW_HIDE, ewWaitUntilTerminated, ResultCode) then
        ReportServiceFailure('CloudDrive could not start its own command-line tool to register the service.')
      else if ResultCode <> 0 then
        ReportServiceFailure(Format('Registering the CloudDrive service failed (exit code %d).', [ResultCode]))
      else if not ServiceExists then
        ReportServiceFailure('Setup reported success but no CloudDrive service was registered.');
    end;

    if RestartServiceAfterInstall then
    begin
      Exec(ExpandConstant('{sys}\sc.exe'), 'start {#ServiceName}', '',
           SW_HIDE, ewWaitUntilTerminated, ResultCode);
    end;
  end;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  ResultCode: Integer;
  KeepConfig: Boolean;
begin
  if CurUninstallStep = usUninstall then
  begin
    Exec(ExpandConstant('{app}\cdrive.exe'), 'tools path --unregister', '',
         SW_HIDE, ewWaitUntilTerminated, ResultCode);
  end;

  if CurUninstallStep = usPostUninstall then
  begin
    { Configuration and credentials are left behind unless the user asks otherwise. An uninstall is
      very often an upgrade or a reinstall, and silently discarding every account would be a
      genuinely destructive surprise. }
    KeepConfig := True;
    if not UninstallSilent then
    begin
      KeepConfig := MsgBox(
        'Keep CloudDrive''s accounts, mappings and stored credentials?' + #13#10#13#10 +
        'Choose No to remove everything under %ProgramData%\CloudDrive.',
        mbConfirmation, MB_YESNO) = IDYES;
    end;

    if not KeepConfig then
      DelTree(ExpandConstant('{commonappdata}\CloudDrive'), True, True, True);
  end;
end;
