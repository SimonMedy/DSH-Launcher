#define MyAppName "DSH Launcher"
#define MyAppExeName "DSHLauncher.exe"
#define MyAppPublisher "SimonMedy"
#define MyAppUrl "https://github.com/SimonMedy/DSH-Launcher"

#ifndef MyAppVersion
  #define MyAppVersion "0.0.0-dev"
#endif

[Setup]
AppId={{7E4F83BB-7A8D-4D2B-9FD2-CCBE461C88A1}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppUrl}
AppSupportURL={#MyAppUrl}/issues
AppUpdatesURL={#MyAppUrl}/releases
DefaultDirName={localappdata}\Programs\DSH Launcher
DefaultGroupName=DSH Launcher
PrivilegesRequired=lowest
SetupArchitecture=x64
ArchitecturesAllowed=x64compatible
MinVersion=10.0
OutputDir=..\artifacts\installer
OutputBaseFilename=DSHLauncher-Setup-{#MyAppVersion}
SetupIconFile=..\assets\DeepSeekHarness.ico
UninstallDisplayIcon={app}\{#MyAppExeName}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
AppMutex=Local\DSHLauncher
CloseApplications=no
RestartApplications=no
AllowNoIcons=yes
DisableProgramGroupPage=auto
DirExistsWarning=no
ChangesAssociations=no
ChangesEnvironment=no
UsePreviousAppDir=yes
SetupLogging=yes

[Files]
Source: "..\artifacts\publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs; Excludes: "*.pdb"
Source: "..\LICENSE"; DestDir: "{app}"; DestName: "LICENSE.txt"; Flags: ignoreversion

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Additional shortcuts:"; Flags: unchecked

[Icons]
Name: "{userprograms}\DSH Launcher"; Filename: "{app}\{#MyAppExeName}"; WorkingDir: "{app}"
Name: "{userdesktop}\DSH Launcher"; Filename: "{app}\{#MyAppExeName}"; WorkingDir: "{app}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "Launch DSH Launcher"; Flags: nowait postinstall skipifsilent
