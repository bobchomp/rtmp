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
; Close the running app (and any orphaned mediamtx.exe / ffmpeg.exe) before replacing files
CloseApplications=yes
CloseApplicationsFilter={#MyAppExeName};mediamtx.exe;ffmpeg.exe
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
; Download and install NDI Runtime if it isn't already present (interactive installs only —
; never during a silent auto-update, where a surprise admin prompt would be confusing).
; The runtime is free software from Vizrt / NewTek (ndi.video). The script downloads to a
; fresh temp folder, gives up after 10 minutes, and only runs the installer if it carries a
; valid Authenticode signature from NDI / Vizrt / NewTek.
Filename: "{sys}\WindowsPowerShell\v1.0\powershell.exe"; \
  Parameters: "-NoProfile -ExecutionPolicy Bypass -Command ""$ErrorActionPreference='Stop'; $u='https://downloads.ndi.tv/SDK/NDI_SDK/NDI%206%20Runtime.exe'; $d=Join-Path $env:TEMP ([guid]::NewGuid()); New-Item -ItemType Directory $d | Out-Null; $t=Join-Path $d 'NDI6Runtime_Setup.exe'; try {{ Invoke-WebRequest $u -OutFile $t -UseBasicParsing -TimeoutSec 600; $sig=Get-AuthenticodeSignature $t; if ($sig.Status -ne 'Valid' -or $sig.SignerCertificate.Subject -notmatch 'NDI|Vizrt|NewTek') {{ throw ('NDI installer signature check failed: ' + $sig.Status) }; $p=Start-Process $t '/quiet /norestart' -PassThru; if (-not $p.WaitForExit(600000)) {{ $p.Kill() } } finally {{ Remove-Item $d -Recurse -Force -ErrorAction SilentlyContinue }"""; \
  StatusMsg: "Installing NDI Runtime (for NDI output feature)..."; \
  Check: ShouldInstallNdiRuntime; \
  Flags: runhidden waituntilterminated

; Interactive install: user sees a "Launch RTMP Projector" checkbox on the finish page
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(MyAppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent runasoriginaluser
; Silent install (auto-update path): always relaunch — WizardSilent() is true when /SILENT is passed
; runasoriginaluser: if setup was elevated (Program Files install), don't relaunch the app as admin
Filename: "{app}\{#MyAppExeName}"; Flags: nowait runasoriginaluser; Check: WizardSilent

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
        and not FileExists(ExpandConstant('{pf}\NDI\NDI 5 Runtime\v5\Processing.NDI.Lib.x64.dll'))
        and (GetEnv('NDI_RUNTIME_DIR_V6') = '')
        and (GetEnv('NDI_RUNTIME_DIR_V5') = '');
end;

{ Interactive installs only — never pop an admin prompt in the middle of a silent auto-update. }
function ShouldInstallNdiRuntime: Boolean;
begin
  Result := NdiRuntimeMissing and not WizardSilent;
end;
