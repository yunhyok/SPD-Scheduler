#ifndef AppVersion
  #define AppVersion "0.1.8"
#endif
#define AppName "SPD Scheduler"

[Setup]
AppId={{B32859F7-30C7-442F-A6EF-0BC64DCFA218}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher=yunhyok
AppPublisherURL=https://github.com/yunhyok/SPD-Scheduler
DefaultDirName={localappdata}\Programs\SPD Scheduler
DefaultGroupName=SPD Scheduler
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
AppMutex=Local\SPDScheduler_SingleInstance
CloseApplications=no
RestartApplications=no
SetupIconFile=..\src\SPDScheduler.App\app.ico
UninstallDisplayIcon={app}\SPDScheduler.exe
OutputDir=..\artifacts\installer
OutputBaseFilename=SPD-Scheduler-Setup-{#AppVersion}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern

[Languages]
Name: "korean"; MessagesFile: "compiler:Languages\Korean.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[CustomMessages]
korean.AutoStart=Windows 로그인 시 트레이에서 시작
english.AutoStart=Start in the system tray when signing in to Windows
korean.OpenScheduler=SPD Scheduler 열기
english.OpenScheduler=Open SPD Scheduler

[Tasks]
Name: "autostart"; Description: "{cm:AutoStart}"; Flags: unchecked

[Files]
Source: "..\artifacts\publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\SPD Scheduler"; Filename: "{app}\SPDScheduler.exe"; Parameters: "--show"

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueName: "SPDScheduler"; Flags: deletevalue; Check: not WizardIsTaskSelected('autostart')
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "SPDScheduler"; ValueData: """{app}\SPDScheduler.exe"""; Flags: uninsdeletevalue; Tasks: autostart

[Run]
Filename: "{app}\SPDScheduler.exe"; Parameters: "--show"; Description: "{cm:OpenScheduler}"; Flags: nowait postinstall skipifsilent unchecked
