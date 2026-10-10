#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif

#define AppName "Audio Output Switcher"
#define AppExeName "AudioOutputSwitcher.exe"

[Setup]
AppId={{9B80A181-877B-42AB-9E2E-415367F5468A}
AppName={#AppName}
AppVersion={#AppVersion}
DefaultDirName={autopf}\Audio Output Switcher
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
PrivilegesRequired=admin
OutputDir=..\artifacts\installer-output
OutputBaseFilename=AudioOutputSwitcher-Setup-{#AppVersion}
SetupIconFile=..\assets\speaker.ico
UninstallDisplayIcon={app}\{#AppExeName}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
CloseApplications=yes
CloseApplicationsFilter={#AppExeName}
RestartApplications=no
UsePreviousAppDir=yes
SetupLogging=yes

[Files]
Source: "..\artifacts\installer-stage\{#AppExeName}"; DestDir: "{app}"; Flags: ignoreversion restartreplace
Source: "..\README.md"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExeName}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExeName}"; Tasks: desktopicon

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Additional shortcuts:"; Flags: unchecked

[Run]
Filename: "{app}\{#AppExeName}"; Description: "Launch {#AppName}"; Flags: nowait postinstall skipifsilent

[UninstallRun]
Filename: "{app}\{#AppExeName}"; Parameters: "--startup-sync disable highest"; Flags: runhidden waituntilterminated; RunOnceId: "RemoveOwnedStartup"
