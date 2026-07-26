; Cherry Sentinel — Windows Setup (EXE)
; Build with: installer\build-setup.ps1
; Requires: Inno Setup 6 + published artifacts under repo\artifacts\

#define MyAppName "Cherry Sentinel"
#ifndef MyAppVersion
  #define MyAppVersion "1.0.0"
#endif
#define MyAppPublisher "CherryDeskX"
#define MyAppURL "https://github.com/cherrysentinel"
#define MyAppExeName "CherrySentinel.Dashboard.exe"
#define MyAgentExeName "CherrySentinel.Agent.exe"

#ifndef SourceRoot
  #define SourceRoot ".."
#endif

[Setup]
AppId={{A7C3E9D1-5B24-4F8A-9E01-8C2D1B0A9F77}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
DefaultDirName={autopf}\Cherry Sentinel
DefaultGroupName=Cherry Sentinel
DisableProgramGroupPage=no
LicenseFile=
OutputDir={#SourceRoot}\artifacts\setup
OutputBaseFilename=CherrySentinel-Setup-{#MyAppVersion}
; Classic BMP ICO required (PNG-in-ICO often ignored by Inno)
SetupIconFile={#SourceRoot}\assets\icons\CherrySentinel.ico
UninstallDisplayIcon={app}\CherrySentinel.ico
SetupMutex=CherrySentinelSetupMutex
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
; Installer side art (from imggui 5b8ee925 vertical + face crop)
WizardImageFile={#SourceRoot}\assets\installer\WizardImage.png
WizardSmallImageFile={#SourceRoot}\assets\installer\WizardSmallImage.png
WizardImageStretch=yes
WizardImageAlphaFormat=defined
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=6.1sp1
DisableWelcomePage=no
SetupLogging=yes
CloseApplications=force
RestartApplications=no
AppCopyright=Copyright (C) CherryDeskX
VersionInfoCompany=CherryDeskX
VersionInfoProductName=Cherry Sentinel Agent
VersionInfoDescription=Cherry Sentinel Agent Setup

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Messages]
WelcomeLabel1=Welcome to Cherry Sentinel Agent Setup
WelcomeLabel2=This will install Cherry Sentinel Agent (Windows Service) and Dashboard on your computer.%n%nSecure endpoint monitoring — detect password spray, lateral movement, and abnormal process activity.%n%nIt is recommended that you close all other applications before continuing.
FinishedHeadingLabel=Completing Cherry Sentinel Agent Setup
FinishedLabelNoIcons=Setup has finished installing Cherry Sentinel Agent on your computer.
FinishedLabel=Setup has finished installing Cherry Sentinel Agent on your computer. The application may be launched by selecting the installed shortcuts.
ClickFinish=Click Finish to exit Setup.

[Types]
Name: "full"; Description: "Full installation (Agent + Dashboard)"
Name: "agent"; Description: "Agent only (Windows Service)"
Name: "dashboard"; Description: "Dashboard only (UI)"
Name: "custom"; Description: "Custom"; Flags: iscustom

[Components]
Name: "agent"; Description: "Cherry Sentinel Agent (Windows Service)"; Types: full agent custom
Name: "dashboard"; Description: "Cherry Sentinel Dashboard (Desktop App)"; Types: full dashboard custom

[Tasks]
Name: "desktopicon"; Description: "Create a &desktop icon (Dashboard)"; GroupDescription: "Additional icons:"; Components: dashboard; Flags: checkedonce
Name: "startservice"; Description: "Start Agent service after install"; GroupDescription: "Agent:"; Components: agent; Flags: checkedonce

[Files]
; Brand icon
Source: "{#SourceRoot}\assets\icons\CherrySentinel.ico"; DestDir: "{app}"; Flags: ignoreversion

; Agent binaries
Source: "{#SourceRoot}\artifacts\agent-win-x64\*"; DestDir: "{app}\Agent"; Flags: ignoreversion recursesubdirs createallsubdirs; Components: agent
Source: "{#SourceRoot}\assets\icons\CherrySentinel.ico"; DestDir: "{app}\Agent"; Flags: ignoreversion; Components: agent
Source: "{#SourceRoot}\config\rules.json"; DestDir: "{app}\Agent"; Flags: ignoreversion; Components: agent
Source: "{#SourceRoot}\config\allowlist.json"; DestDir: "{app}\Agent"; Flags: ignoreversion skipifsourcedoesntexist; Components: agent

; Dashboard binaries
Source: "{#SourceRoot}\artifacts\dashboard-win-x64\*"; DestDir: "{app}\Dashboard"; Flags: ignoreversion recursesubdirs createallsubdirs; Components: dashboard
Source: "{#SourceRoot}\assets\icons\CherrySentinel.ico"; DestDir: "{app}\Dashboard"; Flags: ignoreversion; Components: dashboard

; Helper scripts (post-install / uninstall service)
Source: "{#SourceRoot}\installer\setup-helpers\register-agent-service.ps1"; DestDir: "{app}\Installer"; Flags: ignoreversion; Components: agent
Source: "{#SourceRoot}\installer\setup-helpers\unregister-agent-service.ps1"; DestDir: "{app}\Installer"; Flags: ignoreversion; Components: agent

[Dirs]
Name: "{commonappdata}\CherrySentinel\Agent"; Components: agent
Name: "{commonappdata}\CherrySentinel\Agent\logs"; Components: agent
Name: "{commonappdata}\CherrySentinel\Agent\evidence"; Components: agent

[Icons]
Name: "{group}\Cherry Sentinel Dashboard"; Filename: "{app}\Dashboard\{#MyAppExeName}"; IconFilename: "{app}\CherrySentinel.ico"; Components: dashboard
Name: "{group}\Agent Logs"; Filename: "{commonappdata}\CherrySentinel\Agent\logs"; IconFilename: "{app}\CherrySentinel.ico"; Components: agent
Name: "{group}\Uninstall {#MyAppName}"; Filename: "{uninstallexe}"; IconFilename: "{app}\CherrySentinel.ico"
Name: "{autodesktop}\Cherry Sentinel Dashboard"; Filename: "{app}\Dashboard\{#MyAppExeName}"; IconFilename: "{app}\CherrySentinel.ico"; Tasks: desktopicon; Components: dashboard

[Run]
; Patch agent appsettings + register Windows Service
Filename: "powershell.exe"; \
  Parameters: "-NoProfile -ExecutionPolicy Bypass -File ""{app}\Installer\register-agent-service.ps1"" -InstallDir ""{app}\Agent"" -DataDir ""{commonappdata}\CherrySentinel\Agent"" -ServiceName ""CherrySentinelAgent"" -StartService {code:StartServiceFlag}"; \
  StatusMsg: "Registering Cherry Sentinel Agent service..."; \
  Flags: runhidden waituntilterminated; \
  Components: agent

; Optionally launch Dashboard
Filename: "{app}\Dashboard\{#MyAppExeName}"; Description: "Launch Cherry Sentinel Dashboard"; Flags: nowait postinstall skipifsilent; Components: dashboard

[UninstallRun]
Filename: "powershell.exe"; \
  Parameters: "-NoProfile -ExecutionPolicy Bypass -File ""{app}\Installer\unregister-agent-service.ps1"" -ServiceName ""CherrySentinelAgent"""; \
  RunOnceId: "StopAgentService"; \
  Flags: runhidden waituntilterminated; \
  Components: agent

[Code]
function StartServiceFlag(Param: String): String;
begin
  if WizardIsTaskSelected('startservice') then
    Result := '1'
  else
    Result := '0';
end;

function InitializeSetup(): Boolean;
begin
  Result := True;
  if not IsWin64 then
  begin
    MsgBox('Cherry Sentinel requires a 64-bit Windows operating system.', mbError, MB_OK);
    Result := False;
  end;
end;
