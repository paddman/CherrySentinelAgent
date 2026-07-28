; Cherry Sentinel Central — dedicated Central API installer (Windows Service)
; Build with: installer\build-setup-central.ps1
; Requires: Inno Setup 6 + published artifacts under repo\artifacts\server-win-x64\
;
; Architecture:
;   Agent  -->  THIS Central API  <--  Dashboard
;
; Silent example:
;   CherrySentinel-Central-Setup-1.0.0.exe /VERYSILENT /Port=7443

#define MyAppName "Cherry Sentinel Central"
#ifndef MyAppVersion
  #define MyAppVersion "1.0.0"
#endif
#define MyAppPublisher "CherryDeskX"
#define MyAppURL "https://github.com/cherrysentinel"

#ifndef SourceRoot
  #define SourceRoot ".."
#endif

[Setup]
AppId={{C9E5F1A3-7D46-6B0C-1A23-0E4F3D2C1B99}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
DefaultDirName={autopf}\Cherry Sentinel Central
DefaultGroupName=Cherry Sentinel Central
DisableProgramGroupPage=no
OutputDir={#SourceRoot}\artifacts\setup
OutputBaseFilename=CherrySentinel-Central-Setup-{#MyAppVersion}
SetupIconFile={#SourceRoot}\assets\icons\CherrySentinel.ico
UninstallDisplayIcon={app}\CherrySentinel.ico
SetupMutex=CherrySentinelCentralSetupMutex
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
WizardImageFile={#SourceRoot}\assets\installer\WizardImage.bmp,{#SourceRoot}\assets\installer\WizardImage@2x.bmp
WizardSmallImageFile={#SourceRoot}\assets\installer\WizardSmallImage.bmp,{#SourceRoot}\assets\installer\WizardSmallImage@2x.bmp
WizardImageStretch=yes
WizardImageBackColor=clWhite
WizardImageAlphaFormat=none
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=6.1sp1
DisableWelcomePage=no
SetupLogging=yes
CloseApplications=force
CloseApplicationsFilter=*.exe,*.dll
RestartApplications=no
AppCopyright=Copyright (C) CherryDeskX
VersionInfoCompany=CherryDeskX
VersionInfoProductName=Cherry Sentinel Central
VersionInfoDescription=Cherry Sentinel Central Server Setup

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Messages]
WelcomeLabel1=Cherry Sentinel Central Setup
WelcomeLabel2=ยินดีต้อนรับสู่ตัวติดตั้ง Central Server (แยก)%n%nCentral คือศูนย์กลางที่ Agent ส่งข้อมูลเข้า และ Dashboard ดึงข้อมูลออก%n%nThis installs the Central API Windows Service only (default SQLite, HTTPS port 7443).%n%nAgent and Dashboard must both use this server URL.
FinishedHeadingLabel=ติดตั้ง Central สำเร็จ
FinishedLabelNoIcons=Cherry Sentinel Central ติดตั้งเรียบร้อยแล้ว!
FinishedLabel=Cherry Sentinel Central ติดตั้งเรียบร้อยแล้ว!%n%nService: CherrySentinelCentral%nURL: https://localhost:<port>%n%nตั้ง Agent Server.Url และ Dashboard Settings ให้ชี้ URL นี้
ClickFinish=คลิก Finish เพื่อปิดตัวติดตั้ง
ButtonNext=Next >
ButtonBack=< Back
ButtonCancel=Cancel
ButtonFinish=Finish
SelectDirLabel3=เลือกโฟลเดอร์ติดตั้ง Central
WizardSelectDir=เลือกตำแหน่งติดตั้ง
WizardReady=พร้อมติดตั้ง
WizardInstalling=กำลังติดตั้ง...
StatusExtractFiles=กำลังติดตั้งไฟล์...

[Tasks]
Name: "startservice"; Description: "Start Central service after install"; GroupDescription: "Service:"; Flags: checkedonce
Name: "desktopicon"; Description: "Create a &desktop icon"; GroupDescription: "Additional icons:"; Flags: checkedonce
Name: "openfirewall"; Description: "Open Windows Firewall for Central HTTPS port"; GroupDescription: "Network:"; Flags: unchecked

[Files]
Source: "{#SourceRoot}\installer\setup-helpers\stop-central-for-upgrade.ps1"; DestDir: "{tmp}"; Flags: dontcopy

Source: "{#SourceRoot}\assets\icons\CherrySentinel.ico"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#SourceRoot}\artifacts\server-win-x64\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#SourceRoot}\assets\icons\CherrySentinel.ico"; DestDir: "{app}"; Flags: ignoreversion
; Always ship CONNECTION info so desktop shortcut never breaks (register script refreshes host/port)
Source: "{#SourceRoot}\installer\templates\CONNECTION.txt"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#SourceRoot}\installer\templates\Open-Central-Info.cmd"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#SourceRoot}\config\signatures\opensource-signatures.json"; DestDir: "{app}\signatures"; Flags: ignoreversion skipifsourcedoesntexist
Source: "{#SourceRoot}\installer\setup-helpers\register-central-service.ps1"; DestDir: "{app}\Installer"; Flags: ignoreversion
Source: "{#SourceRoot}\installer\setup-helpers\unregister-central-service.ps1"; DestDir: "{app}\Installer"; Flags: ignoreversion
Source: "{#SourceRoot}\installer\setup-helpers\stop-central-for-upgrade.ps1"; DestDir: "{app}\Installer"; Flags: ignoreversion

[Dirs]
Name: "{commonappdata}\CherrySentinel\Server"
Name: "{commonappdata}\CherrySentinel\Server\logs"
Name: "{commonappdata}\CherrySentinel\Server\certs"

[Icons]
Name: "{group}\Central Install Folder"; Filename: "{app}"; IconFilename: "{app}\CherrySentinel.ico"
Name: "{group}\Central Logs"; Filename: "{commonappdata}\CherrySentinel\Server\logs"; IconFilename: "{app}\CherrySentinel.ico"
Name: "{group}\Connection Info"; Filename: "{app}\Open-Central-Info.cmd"; IconFilename: "{app}\CherrySentinel.ico"; WorkingDir: "{app}"
Name: "{group}\Central Folder"; Filename: "{app}"; IconFilename: "{app}\CherrySentinel.ico"
Name: "{group}\Uninstall {#MyAppName}"; Filename: "{uninstallexe}"; IconFilename: "{app}\CherrySentinel.ico"
; Desktop: open helper cmd (always present) — never a missing CONNECTION.txt target
Name: "{autodesktop}\Cherry Sentinel Central"; Filename: "{app}\Open-Central-Info.cmd"; IconFilename: "{app}\CherrySentinel.ico"; WorkingDir: "{app}"; Comment: "Central connection info (service runs in background)"; Tasks: desktopicon

[Run]
Filename: "powershell.exe"; \
  Parameters: "-NoProfile -ExecutionPolicy Bypass -File ""{app}\Installer\register-central-service.ps1"" -InstallDir ""{app}"" -DataDir ""{commonappdata}\CherrySentinel\Server"" -ServiceName ""CherrySentinelCentral"" -StartService {code:StartServiceFlag} -Port ""{code:GetPort}"""; \
  StatusMsg: "Registering Central service..."; \
  Flags: runhidden waituntilterminated

Filename: "powershell.exe"; \
  Parameters: "-NoProfile -ExecutionPolicy Bypass -Command ""New-NetFirewallRule -DisplayName 'Cherry Sentinel Central' -Direction Inbound -Action Allow -Protocol TCP -LocalPort {code:GetPort} -ErrorAction SilentlyContinue"""; \
  StatusMsg: "Opening firewall port..."; \
  Flags: runhidden waituntilterminated; \
  Tasks: openfirewall

[UninstallRun]
Filename: "powershell.exe"; \
  Parameters: "-NoProfile -ExecutionPolicy Bypass -File ""{app}\Installer\unregister-central-service.ps1"" -ServiceName ""CherrySentinelCentral"""; \
  RunOnceId: "StopCentralService"; \
  Flags: runhidden waituntilterminated
Filename: "powershell.exe"; \
  Parameters: "-NoProfile -ExecutionPolicy Bypass -Command ""Remove-NetFirewallRule -DisplayName 'Cherry Sentinel Central' -ErrorAction SilentlyContinue"""; \
  RunOnceId: "RemoveCentralFirewall"; \
  Flags: runhidden waituntilterminated

[Code]
var
  PortPage: TInputQueryWizardPage;
  GPort: String;

function GetCommandLineParam(const ParamName: String): String;
var
  i: Integer;
  S, Prefix: String;
begin
  Result := '';
  Prefix := '/' + ParamName + '=';
  for i := 1 to ParamCount do
  begin
    S := ParamStr(i);
    if CompareText(Copy(S, 1, Length(Prefix)), Prefix) = 0 then
    begin
      Result := Copy(S, Length(Prefix) + 1, MaxInt);
      if (Length(Result) >= 2) and (Result[1] = '"') and (Result[Length(Result)] = '"') then
        Result := Copy(Result, 2, Length(Result) - 2);
      Exit;
    end;
  end;
end;

procedure InitializeWizard;
var
  CmdPort: String;
begin
  PortPage := CreateInputQueryPage(wpSelectDir,
    'Central Listen Port',
    'พอร์ต HTTPS ของ Central API',
    'Agent และ Dashboard จะเชื่อมต่อที่ https://<this-host>:<port>%n%n' +
    'Default SQLite database — no PostgreSQL required.%n' +
    'Example port: 7443');
  PortPage.Add('HTTPS Port:', False);

  CmdPort := GetCommandLineParam('Port');
  if CmdPort <> '' then
    PortPage.Values[0] := CmdPort
  else
    PortPage.Values[0] := '7443';
end;

procedure KillCentralProcesses;
var
  ResultCode: Integer;
begin
  Exec('sc.exe', 'stop CherrySentinelCentral', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Sleep(800);
  Exec('taskkill.exe', '/F /IM CherrySentinel.Server.exe /T', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Sleep(400);
end;

function NextButtonClick(CurPageID: Integer): Boolean;
var
  P: String;
  PortNum: Integer;
begin
  Result := True;
  if CurPageID = PortPage.ID then
  begin
    P := Trim(PortPage.Values[0]);
    PortNum := StrToIntDef(P, -1);
    if (PortNum < 1) or (PortNum > 65535) then
    begin
      MsgBox('Port must be a number between 1 and 65535.' + #13#10 + 'Example: 7443', mbError, MB_OK);
      Result := False;
      Exit;
    end;
    PortPage.Values[0] := IntToStr(PortNum);
    GPort := PortPage.Values[0];
  end;

  if CurPageID = wpReady then
    KillCentralProcesses;
end;

function GetPort(Param: String): String;
var
  CmdPort: String;
begin
  if GPort <> '' then
    Result := GPort
  else if Assigned(PortPage) and (PortPage.Values[0] <> '') then
    Result := Trim(PortPage.Values[0])
  else
  begin
    CmdPort := GetCommandLineParam('Port');
    if CmdPort <> '' then
      Result := CmdPort
    else
      Result := '7443';
  end;
end;

function StartServiceFlag(Param: String): String;
begin
  if WizardIsTaskSelected('startservice') then
    Result := '1'
  else
    Result := '0';
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  ResultCode: Integer;
  Ps1: String;
begin
  Result := '';
  NeedsRestart := False;
  KillCentralProcesses;
  ExtractTemporaryFile('stop-central-for-upgrade.ps1');
  Ps1 := ExpandConstant('{tmp}\stop-central-for-upgrade.ps1');
  if FileExists(Ps1) then
  begin
    Exec('powershell.exe',
      '-NoProfile -ExecutionPolicy Bypass -File "' + Ps1 + '" -ServiceName "CherrySentinelCentral" -InstallDir "' + ExpandConstant('{app}') + '"',
      '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  end;
  KillCentralProcesses;
end;

function InitializeSetup(): Boolean;
begin
  Result := True;
  if not IsWin64 then
  begin
    MsgBox('Cherry Sentinel Central requires a 64-bit Windows operating system.', mbError, MB_OK);
    Result := False;
  end;
end;

function UpdateReadyMemo(Space, NewLine, MemoUserInfoInfo, MemoDirInfo, MemoTypeInfo,
  MemoComponentsInfo, MemoGroupInfo, MemoTasksInfo: String): String;
begin
  Result :=
    MemoDirInfo + NewLine + NewLine +
    'Central HTTPS URL:' + NewLine +
    Space + 'https://localhost:' + GetPort('') + NewLine +
    Space + 'https://<this-host>:' + GetPort('') + NewLine + NewLine +
    'Service: CherrySentinelCentral' + NewLine +
    'Database: SQLite (ProgramData\CherrySentinel\Server\central.db)' + NewLine + NewLine +
    'After install, set Agent Server.Url and Dashboard Settings to this URL.' + NewLine +
    MemoTasksInfo;
end;
