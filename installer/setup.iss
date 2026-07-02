; ============================================================================
;  setup.iss — Inno Setup script for PowerAppsControl (Windows desktop installer)
;
;  Compiles into PowerAppsControl-<version>-setup.exe, which the release workflow
;  then zips into PowerAppsControl-<version>-setup.zip. Installs the published
;  self-contained output under Program Files with Start Menu + optional desktop
;  shortcuts and a clean uninstaller.
;
;  Invoked by .github/workflows/release.yml as:
;    ISCC.exe installer/setup.iss /DMyAppVersion=1.0.0 /DMyAppName=PowerAppsControl /DPublishDir=publish
; ============================================================================

#ifndef MyAppName
  #define MyAppName "PowerAppsControl"
#endif
#ifndef MyAppVersion
  #define MyAppVersion "0.0.0"
#endif
#ifndef PublishDir
  #define PublishDir "..\publish"
#endif

#define MyAppPublisher "Ilya Fainberg"
#define MyAppURL "https://github.com/ilyafainberg/PowerAppsControl"
#define MyAppExeName "PowerAppsControl.exe"

[Setup]
; Stable AppId — generated once; never change it or upgrades install side-by-side.
AppId={{697B9118-6FF5-4FB8-8F0E-BBD90010ACCE}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}/issues
AppUpdatesURL={#MyAppURL}/releases
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
; LICENSE shown in the wizard — the repo's GPLv3 file.
LicenseFile=..\LICENSE
OutputDir=..\Output
OutputBaseFilename={#MyAppName}-{#MyAppVersion}-setup
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
; Recursively include everything the publish step produced (self-contained → bundles .NET).
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\Uninstall {#MyAppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(MyAppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent

; ----------------------------------------------------------------------------
;  NOTE: PowerAppsControl is a stdio MCP server — it is normally launched by an
;  MCP host (e.g. Microsoft Scout), not double-clicked. The Start Menu shortcut
;  is provided for convenience / manual testing. Recording needs FFmpeg on PATH
;  (winget install Gyan.FFmpeg); the app runs without it (no video).
; ----------------------------------------------------------------------------
