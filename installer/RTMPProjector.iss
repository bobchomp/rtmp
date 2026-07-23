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
; Close the running app (and any orphaned mediamtx.exe) before replacing files
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
; Download and install NDI Runtime if it isn't already present.
; The runtime is free software from Vizrt / NewTek (ndi.video).
; /quiet suppresses UI; /norestart avoids a reboot mid-install.
Filename: "{sys}\WindowsPowerShell\v1.0\powershell.exe"; \
  Parameters: "-NoProfile -ExecutionPolicy Bypass -Command ""$u='https://downloads.ndi.tv/SDK/NDI_SDK/NDI%206%20Runtime.exe'; $t=Join-Path $env:TEMP 'NDI6Runtime_Setup.exe'; Write-Host 'Downloading NDI 6 Runtime...'; (New-Object Net.WebClient).DownloadFile($u,$t); Write-Host 'Installing NDI 6 Runtime...'; Start-Process $t '/quiet /norestart' -Wait; Remove-Item $t -ErrorAction SilentlyContinue; Write-Host 'NDI Runtime installed.'"""; \
  StatusMsg: "Installing NDI Runtime (for NDI output feature)..."; \
  Check: NdiRuntimeMissing; \
  Flags: runhidden waituntilterminated

; Interactive install: user sees a "Launch RTMP Projector" checkbox on the finish page
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(MyAppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent
; Silent install (auto-update path): always relaunch — WizardSilent() is true when /SILENT is passed
Filename: "{app}\{#MyAppExeName}"; Flags: nowait; Check: WizardSilent

[Registry]
; Store install dir so the updater can pass it as /DIR= next time
Root: HKCU; Subkey: "Software\RTMPProjector"; ValueType: string; ValueName: "InstallDir"; ValueData: "{app}"; Flags: uninsdeletekey

[Code]
{ Returns true when no NDI Runtime (v4/v5/v6) is detected — triggers the download. }
function NdiRuntimeMissing: Boolean;
begin
  Result := not RegKeyExists(HKLM, 'SOFTWARE\NDI\Runtime\v6')
        and not RegKeyExists(HKLM, 'SOFTWARE\NDI\Runtime\v5')
        and not RegKeyExists(HKLM, 'SOFTWARE\NDI\Runtime\v4')
        and not FileExists(ExpandConstant('{pf}\NDI\NDI 6 Runtime\v6\Processing.NDI.Lib.x64.dll'))
        and not FileExists(ExpandConstant('{pf}\NDI\NDI 5 Runtime\v5\Processing.NDI.Lib.x64.dll'));
end;
