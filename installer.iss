[Setup]
AppName=Sagar Download Manager
AppVersion=1.0
AppPublisher=Sagar
DefaultDirName={autopf}\SagarDownloadManager
DefaultGroupName=Sagar Download Manager
OutputDir=installer_out
OutputBaseFilename=SagarDownloadManager_Setup
Compression=lzma2/ultra64
SolidCompression=yes
PrivilegesRequired=admin
WizardStyle=modern
UninstallDisplayIcon={app}\DM.App.exe
SetupIconFile=

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a &desktop shortcut"; GroupDescription: "Additional icons:"; Flags: unchecked

[Files]
Source: "publish_out\DM.App\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\Sagar Download Manager"; Filename: "{app}\DM.App.exe"
Name: "{group}\Uninstall Sagar Download Manager"; Filename: "{uninstallexe}"
Name: "{autodesktop}\Sagar Download Manager"; Filename: "{app}\DM.App.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\DM.App.exe"; Description: "Launch Sagar Download Manager"; Flags: nowait postinstall skipifsilent
