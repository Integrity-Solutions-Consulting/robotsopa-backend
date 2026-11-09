#define MyAppName "Bot"
#define MyAppVersion "1.0.1"
[Setup]
AppId={{B2411B51-2C2D-47B1-887A-495C8974E3C5}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
DefaultDirName={autopf}\Bot
DefaultGroupName=Bot
OutputDir=output
OutputBaseFilename=Bot-Setup-1.0.1-rc
Compression=lzma2
SolidCompression=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequired=admin
UninstallDisplayIcon={app}\Bot.exe
[Files]
Source: "publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
[Icons]
Name: "{autoprograms}\Bot"; Filename: "{app}\Bot.exe"
Name: "{autodesktop}\Bot"; Filename: "{app}\Bot.exe"; Tasks: desktopicon
[Tasks]
Name: desktopicon; Description: "Crear acceso directo en el escritorio"; Flags: unchecked
[Run]
Filename: "{app}\Bot.exe"; Description: "Abrir Bot"; Flags: nowait postinstall skipifsilent
