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
#define Repo           "RHC-Solutions/CloudDrive"

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

{ ----------------------------------------------------------------------------------------------
  Self-update: before installing anything, look for a newer release and hand over to it.

  The point is that one downloaded CloudDrive-Setup.exe stays useful indefinitely -- run the copy you
  already have and it fetches whatever is current rather than installing a stale build.

  Rules this follows, each for a reason:

    * A failed check never blocks the install. No network, a rate-limited API, a malformed response:
      all fall through to the bundled payload. An installer that refuses to work offline would be a
      far worse defect than installing a slightly old version.
    * The download is verified against a SHA-256 published alongside it, and DownloadTemporaryFile
      refuses the file if it does not match. This code downloads and then *executes* an executable,
      so an unverified byte stream is not acceptable at any point.
    * Skipped entirely for a silent install. That is how the in-app updater runs setup, and it has
      already chosen a specific version deliberately; hopping to a different one behind its back
      could also loop. /NOSELFUPDATE forces the same skip for anyone scripting an install.
    * The replacement is launched with /NOSELFUPDATE, so the hand-off happens at most once.
  ---------------------------------------------------------------------------------------------- }

var
  SkipSelfUpdate: Boolean;

{ A small HTTP GET returning the body as text. WinHttp rather than DownloadTemporaryFile because the
  GitHub API needs request headers -- it rejects a request with no User-Agent -- and because the
  response is wanted in memory rather than on disk. }
function HttpGetText(const Url: String; var Text: String): Boolean;
var
  Http: Variant;
begin
  Result := False;
  try
    Http := CreateOleObject('WinHttp.WinHttpRequest.5.1');
    { Short timeouts: this runs before the wizard appears, so a hanging endpoint would look like a
      setup that failed to start. Resolve, connect, send, receive. }
    Http.SetTimeouts(5000, 5000, 5000, 15000);
    Http.Open('GET', Url, False);
    Http.SetRequestHeader('User-Agent', 'CloudDrive-Setup');
    Http.SetRequestHeader('Accept', 'application/vnd.github+json');
    Http.Send();
    if Http.Status = 200 then
    begin
      Text := Http.ResponseText;
      Result := True;
    end;
  except
    Result := False;
  end;
end;

{ Reads the first "tag_name" out of a GitHub releases response.

  Deliberately the only field parsed out of that JSON. Everything else is derived from the tag by
  building a URL, because hand-rolled JSON scraping is brittle and the less of it there is the better.
  Draft releases are invisible to an anonymous caller, so the first entry is the newest published one. }
function ExtractFirstTag(const Json: String): String;
var
  P, Q: Integer;
begin
  Result := '';
  P := Pos('"tag_name"', Json);
  if P = 0 then Exit;

  P := P + Length('"tag_name"');
  { Step over the colon, any whitespace, and the opening quote. }
  while (P <= Length(Json)) and (Json[P] <> '"') do Inc(P);
  Inc(P);

  Q := P;
  while (Q <= Length(Json)) and (Json[Q] <> '"') do Inc(Q);
  if Q > P then Result := Copy(Json, P, Q - P);
end;

{ Splits a version into three numbers, tolerating a leading v and any -suffix. }
function ParseVersion(V: String; var A, B, C: Integer): Boolean;
var
  Parts: TArrayOfString;
  Cut: Integer;
begin
  Result := False;
  A := 0; B := 0; C := 0;

  if (Length(V) > 0) and ((V[1] = 'v') or (V[1] = 'V')) then V := Copy(V, 2, Length(V) - 1);

  { Drop a prerelease or build suffix such as -beta.1 before splitting. }
  Cut := Pos('-', V);
  if Cut > 0 then V := Copy(V, 1, Cut - 1);
  Cut := Pos('+', V);
  if Cut > 0 then V := Copy(V, 1, Cut - 1);

  Parts := StringSplitEx(V, ['.'], '"', stExcludeEmpty);
  if GetArrayLength(Parts) < 2 then Exit;

  A := StrToIntDef(Parts[0], -1);
  B := StrToIntDef(Parts[1], -1);
  if GetArrayLength(Parts) > 2 then C := StrToIntDef(Parts[2], 0);

  Result := (A >= 0) and (B >= 0) and (C >= 0);
end;

{ True when Candidate is a strictly newer version than Installed. }
function IsNewerVersion(const Candidate, Installed: String): Boolean;
var
  Ca, Cb, Cc, Ia, Ib, Ic: Integer;
begin
  Result := False;
  if not ParseVersion(Candidate, Ca, Cb, Cc) then Exit;
  if not ParseVersion(Installed, Ia, Ib, Ic) then Exit;

  if Ca <> Ia then Result := Ca > Ia
  else if Cb <> Ib then Result := Cb > Ib
  else Result := Cc > Ic;
end;

{ Reads the hex digest out of a published .sha256 sidecar, which may be bare hex or
  "<hex>  <filename>" in sha256sum's format. }
function ParseSha256(const Text: String): String;
var
  I: Integer;
  Ch: Char;
begin
  Result := '';
  for I := 1 to Length(Text) do
  begin
    Ch := Text[I];
    if ((Ch >= '0') and (Ch <= '9')) or ((Ch >= 'a') and (Ch <= 'f')) or ((Ch >= 'A') and (Ch <= 'F')) then
      Result := Result + Ch
    else
      Break;
  end;
  if Length(Result) <> 64 then Result := '';
end;

{ Looks for a newer release and, if the user agrees, downloads and launches it.

  Returns True when the hand-off happened and this setup should stop. }
function TrySelfUpdate: Boolean;
var
  Json, Tag, ShaText, Sha, Url, Downloaded, Handoff, Params: String;
  ResultCode: Integer;
begin
  Result := False;

  if SkipSelfUpdate or WizardSilent then Exit;

  if not HttpGetText('https://api.github.com/repos/{#Repo}/releases?per_page=10', Json) then Exit;

  Tag := ExtractFirstTag(Json);
  if Tag = '' then Exit;
  if not IsNewerVersion(Tag, '{#AppVersion}') then Exit;

  { The digest comes from a sidecar published next to the installer, fetched over its own connection.
    Releases before this feature existed have no sidecar, in which case there is nothing to verify
    against and the bundled payload is used instead -- never an unverified download. }
  if not HttpGetText('https://github.com/{#Repo}/releases/download/' + Tag + '/CloudDrive-Setup.exe.sha256', ShaText) then
  begin
    Log('CloudDrive: ' + Tag + ' publishes no checksum; installing the bundled version instead.');
    Exit;
  end;

  Sha := ParseSha256(ShaText);
  if Sha = '' then Exit;

  if MsgBox('A newer version of CloudDrive is available.' + #13#10#13#10 +
            'This installer contains ' + '{#AppVersion}' + '.' + #13#10 +
            Tag + ' has been published.' + #13#10#13#10 +
            'Download and install it instead?' + #13#10#13#10 +
            'The download is about 78 MB and setup will look idle while it runs.',
            mbConfirmation, MB_YESNO) <> IDYES then
  begin
    Exit;
  end;

  Url := 'https://github.com/{#Repo}/releases/download/' + Tag + '/CloudDrive-Setup.exe';
  try
    { The third argument makes this refuse a file whose SHA-256 does not match. }
    DownloadTemporaryFile(Url, 'CloudDrive-Setup.exe', Sha, nil);
  except
    MsgBox('The newer version could not be downloaded or failed verification, so ' + '{#AppVersion}' +
           ' will be installed instead.' + #13#10#13#10 + GetExceptionMessage,
           mbInformation, MB_OK);
    Exit;
  end;

  { Copied out of Setup's temporary directory, which Setup deletes on exit -- the child process would
    otherwise lose its own executable mid-run.

    That directory's constant cannot be named inside a brace comment: Inno reads the constant's closing
    brace as the end of the comment and compiles the rest of the line, which is what "Identifier
    expected" on this line meant. }
  Downloaded := ExpandConstant('{tmp}') + '\CloudDrive-Setup.exe';
  Handoff := ExpandConstant('{%TEMP}') + '\CloudDrive-Setup-' + Tag + '.exe';
  if not FileCopy(Downloaded, Handoff, False) then Exit;

  { Pass the original switches through so an operator's choices survive the hand-off, plus the flag
    that stops the replacement doing this again. }
  Params := GetCmdTail;
  if Params <> '' then Params := Params + ' ';
  Params := Params + '/NOSELFUPDATE';

  if Exec(Handoff, Params, '', SW_SHOW, ewNoWait, ResultCode) then
    Result := True
  else
    MsgBox('The newer installer was downloaded but could not be started, so ' + '{#AppVersion}' +
           ' will be installed instead.', mbInformation, MB_OK);
end;

function InitializeSetup: Boolean;
begin
  { The in-app updater passes /RESTARTSERVICE so the service comes back after the swap. It is a
    custom switch rather than a task, because a silent install runs no task selection. }
  RestartServiceAfterInstall := ExpandConstant('{param:RESTARTSERVICE|no}') <> 'no';
  SkipSelfUpdate := ExpandConstant('{param:NOSELFUPDATE|no}') <> 'no';

  { Returning False here stops this setup because a newer one has been launched in its place. }
  Result := not TrySelfUpdate;
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
