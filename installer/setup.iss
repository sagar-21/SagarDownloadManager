; Inno Setup 6 script for Sagar Download Manager
; https://jrsoftware.org/isinfo.php
;
; Compiled by Release.ps1, which passes:
;   /DMyAppVersion=1.2.3        (semantic version)
;   /DMySourceDir=C:\...\win-x64-obf    (publish output)
;   /DMyOutputDir=C:\...\installer\output
;
; To compile manually (from repo root):
;   iscc /DMyAppVersion="1.0.0" /DMySourceDir="publish\win-x64-obf" /DMyOutputDir="installer\output" installer\setup.iss

; ── Defaults when compiled without /D overrides ────────────────────────────
#ifndef MyAppVersion
  #define MyAppVersion "0.0.0"
#endif
#ifndef MySourceDir
  #define MySourceDir "..\publish\win-x64-obf"
#endif
#ifndef MyOutputDir
  #define MyOutputDir "output"
#endif

; ── App identity ────────────────────────────────────────────────────────────
#define MyAppName      "Sagar Download Manager"
#define MyAppPublisher "Your Company Name"
#define MyAppURL       "https://yourapp.com"
#define MyAppExeName   "DM.App.exe"
; Stable GUID — generate once with [System.Guid]::NewGuid() in PowerShell.
; NEVER change this after first release; it tells Windows how to find the
; existing installation for upgrades.
#define MyAppId        "{{A1B2C3D4-E5F6-7890-ABCD-EF1234567890}"

[Setup]
AppId={#MyAppId}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}/support
AppUpdatesURL={#MyAppURL}/updates
AppCopyright=Copyright (C) 2025 {#MyAppPublisher}

; Install to C:\Program Files\Sagar Download Manager on 64-bit Windows
DefaultDirName={autopf64}\{#MyAppName}
DefaultGroupName={#MyAppName}

; Installer output
OutputDir={#MyOutputDir}
OutputBaseFilename=SagarDM-Setup-{#MyAppVersion}

; Appearance
SetupIconFile=..\DM.App\Assets\app.ico
WizardStyle=modern
WizardResizable=no
DisableWelcomePage=no
DisableProgramGroupPage=yes

; Compression
Compression=lzma2/ultra64
SolidCompression=yes
LZMAUseSeparateProcess=yes

; Windows 10 1809+ required (WPF-UI Fluent Design needs Win10 1809+)
MinVersion=10.0.17763

; Only x64 — we publish win-x64 only
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible

; UAC prompt — app writes to AppData, not ProgramFiles, so admin is only
; needed to write to the install directory and Start Menu.
PrivilegesRequired=admin

; Allow upgrading over an existing install without uninstalling first
AllowUNCPath=no
CloseApplications=yes
CloseApplicationsFilter=*.exe
RestartApplications=no

; Uninstaller
UninstallDisplayIcon={app}\{#MyAppExeName}
UninstallDisplayName={#MyAppName} {#MyAppVersion}

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
; Optional desktop icon — unchecked by default so power users aren't annoyed
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
; Everything from the publish output directory
Source: "{#MySourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
; Start Menu
Name: "{group}\{#MyAppName}";           Filename: "{app}\{#MyAppExeName}"
Name: "{group}\Uninstall {#MyAppName}"; Filename: "{uninstallexe}"
; Desktop (only created if task is checked)
Name: "{autodesktop}\{#MyAppName}";     Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
; Offer to launch the app after setup completes
Filename: "{app}\{#MyAppExeName}"; \
  Description: "{cm:LaunchProgram,{#StringChange(MyAppName, '&', '&&')}}"; \
  Flags: nowait postinstall skipifsilent

[UninstallRun]
; Nothing special needed — the uninstaller handles file removal automatically

[Code]
// Close the app if it is running before upgrade/install begins.
// Inno Setup's CloseApplications=yes handles this for known processes,
// but we name it explicitly as a fallback.
function InitializeSetup(): Boolean;
var
  ResultCode: Integer;
begin
  Result := True;
end;
