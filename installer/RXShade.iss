; ---------------------------------------------------------------------------
; RXShade installer (Inno Setup 6)
;
; Builds a per-user installer with an uninstaller registered in
; "Apps & features". No elevation is requested, so it works from any account.
;
; Compile with (per-user Inno Setup install; Program Files works too):
;   & "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe" installer\RXShade.iss
;
; The app itself must already be published to ..\dist\RXShade.exe
; (run ..\build.ps1 first).
; ---------------------------------------------------------------------------

#define MyAppName "RXShade"
#define MyAppVersion "1.0.1"
#define MyAppVersionLabel "1.0.1 Beta"
#define MyAppPublisher "CortexDev"
#define MyAppExeName "RXShade.exe"
#define MyAppId "{{B7C4E2A1-9F3D-4E7A-8C21-5D6F0A9B3E74}"

[Setup]
AppId={#MyAppId}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersionLabel}
VersionInfoVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppComments=Real-time GPU shader filters for the Roblox window. Capture-only overlay with no injection, no memory reads and no game files touched.
DefaultDirName={localappdata}\Programs\{#MyAppName}
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
; Per-user install: no UAC prompt, uninstall entry under HKCU.
PrivilegesRequired=lowest
OutputDir=..\dist
OutputBaseFilename=RXShadeSetup
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
; Icon shown by the setup program, the uninstall entry and the shortcuts.
SetupIconFile=..\src\RXShade\RXShade.ico
UninstallDisplayIcon={app}\{#MyAppExeName}
UninstallDisplayName={#MyAppName} {#MyAppVersionLabel}
CloseApplications=yes
RestartApplications=no
AllowNoIcons=yes
MinVersion=10.0.19041

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Additional shortcuts:"; Flags: unchecked

[Files]
Source: "..\dist\{#MyAppExeName}"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\README.md"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\LICENSE"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\Uninstall {#MyAppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "Launch {#MyAppName}"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
; The app keeps its settings (and error log) under %LOCALAPPDATA%\RXShade,
; deliberately left in place so a reinstall remembers the user's choices.
Type: filesandordirs; Name: "{app}"
