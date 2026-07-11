; Verbatim installer script (Inno Setup 6).
; CI passes /DAppVersion=<tag> and /DPublishDir=<workspace>\publish;
; the defaults below make a local build work out of the box.

#ifndef AppVersion
  #define AppVersion "2.0.0"
#endif
#ifndef PublishDir
  #define PublishDir "..\publish"
#endif

[Setup]
; Fixed AppId so upgrades replace previous installs — never regenerate this GUID.
AppId={{D4C7DA89-6931-4FBF-80B6-DB7FEECA730A}
AppName=Verbatim
AppVersion={#AppVersion}
AppPublisher=The-Berin
DefaultDirName={autopf}\Verbatim
DefaultGroupName=Verbatim
DisableProgramGroupPage=yes
; Per-user install, no UAC prompt ({autopf} resolves to %LocalAppData%\Programs).
PrivilegesRequired=lowest
OutputBaseFilename=VerbatimSetup-{#AppVersion}
OutputDir=Output
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
UninstallDisplayIcon={app}\Verbatim.exe
SetupIconFile=..\src\Verbatim\Assets\verbatim.ico

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#PublishDir}\Verbatim.exe"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\Verbatim"; Filename: "{app}\Verbatim.exe"
Name: "{autodesktop}\Verbatim"; Filename: "{app}\Verbatim.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\Verbatim.exe"; Description: "{cm:LaunchProgram,Verbatim}"; Flags: nowait postinstall skipifsilent
