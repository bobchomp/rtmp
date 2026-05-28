#define MyAppName "RTMP Projector"
#define MyAppPublisher "bobchomp"
#define MyAppExeName "RTMPProjector.exe"
; AppVersion is passed on the command line: iscc /DAppVersion=x.y.z RTMPProjector.iss

[Setup]
AppId={{A7F3C9D1-4B82-4E56-9C3A-F12E8D047B65}
AppName={#MyAppName}
AppVersion={#AppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL=https://github.com/bobchomp/rtmp
AppSupportURL=https://github.com/bobchomp/rtmp/issues
AppUpdatesURL=https://github.com/bobchomp/rtmp/releases
; Default to user's AppData — no UAC required. User may change to any path.
DefaultDirName={localappdata}\RTMPProjector
DisableProgramGroupPage=yes
OutputDir=.
OutputBaseFilename=RTMPProjector-{#AppVersion}-Setup
Compression=lzma
SolidCompression=yes
WizardStyle=modern
; No UAC prompt by default, but offer elevation dialog if user wants Program Files
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
UninstallDisplayIcon={app}\{#MyAppExeName}
; Close the running app automatically during silent (/SILENT) installs
CloseApplications=yes
CloseApplicationsFilter={#MyAppExeName};mediamtx.exe
SetupIconFile=..\src\Assets\tray.ico

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
; publish\ is produced by `dotnet publish` — CI runs iscc from the repo root
Source: "..\publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs

[Icons]
Name: "{autoprograms}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
; Interactive install: user sees a "Launch RTMP Projector" checkbox on the finish page
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(MyAppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent
; Silent install (auto-update path): always relaunch — WizardSilent() is true when /SILENT is passed
Filename: "{app}\{#MyAppExeName}"; Flags: nowait; Check: WizardSilent

[Registry]
; Store install dir so the updater can pass it as /DIR= next time
Root: HKCU; Subkey: "Software\RTMPProjector"; ValueType: string; ValueName: "InstallDir"; ValueData: "{app}"; Flags: uninsdeletekey
