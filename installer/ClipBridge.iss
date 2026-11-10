#ifndef AppVersion
  #error AppVersion is required
#endif
#ifndef PublishDir
  #error PublishDir is required
#endif
#ifndef OutputDir
  #error OutputDir is required
#endif
[Setup]
AppId={{C442CD76-2632-4C65-A89B-C0C2CF45A379}
AppName=ClipBridge
AppVersion={#AppVersion}
AppPublisher=ClipBridge
DefaultDirName={localappdata}\Programs\ClipBridge
DefaultGroupName=ClipBridge
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
OutputDir={#OutputDir}
OutputBaseFilename=ClipBridge-Setup-{#AppVersion}-win-x64
SetupIconFile=..\src\ClipBridge.Windows\App.ico
UninstallDisplayIcon={app}\ClipBridge.Windows.exe
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
AppMutex=Local\ClipBridge.Install
CloseApplications=yes
RestartApplications=no
SetupLogging=yes
[Languages]
Name: "chinesesimplified"; MessagesFile: "compiler:Languages\ChineseSimplified.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"
[Tasks]
Name: "desktopicon"; Description: "创建桌面快捷方式"; Flags: unchecked
[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs; Excludes: "*.pdb"
Source: "..\docs\THIRD_PARTY.md"; DestDir: "{app}\licenses"; Flags: ignoreversion
Source: "..\docs\LICENSE-BOUNCYCASTLE.md"; DestDir: "{app}\licenses"; Flags: ignoreversion
Source: "..\docs\LICENSE-QRCODER.txt"; DestDir: "{app}\licenses"; Flags: ignoreversion
Source: "..\docs\LICENSE-TOAST-TOOLKIT.md"; DestDir: "{app}\licenses"; Flags: ignoreversion
Source: "..\LICENSE"; DestDir: "{app}\licenses"; Flags: ignoreversion
[Icons]
Name: "{userprograms}\ClipBridge"; Filename: "{app}\ClipBridge.Windows.exe"
Name: "{userdesktop}\ClipBridge"; Filename: "{app}\ClipBridge.Windows.exe"; Tasks: desktopicon
[Run]
Filename: "{app}\ClipBridge.Windows.exe"; Description: "启动 ClipBridge"; Flags: nowait postinstall skipifsilent
[UninstallRun]
Filename: "{app}\ClipBridge.Windows.exe"; Parameters: "--unregister-notifications"; Flags: runhidden waituntilterminated

[Code]
const RunKey = 'Software\Microsoft\Windows\CurrentVersion\Run';
procedure CurStepChanged(CurStep: TSetupStep);
var Value: String;
begin
  if (CurStep = ssPostInstall) and RegQueryStringValue(HKCU, RunKey, 'ClipBridge', Value) and (Value <> '') then
    RegWriteStringValue(HKCU, RunKey, 'ClipBridge', '"' + ExpandConstant('{app}\ClipBridge.Windows.exe') + '" --startup');
  if (CurStep = ssPostInstall) and RegQueryStringValue(HKCU, 'Software\Classes\*\shell\ClipBridge\command', '', Value) then
    RegWriteStringValue(HKCU, 'Software\Classes\*\shell\ClipBridge\command', '', '"' + ExpandConstant('{app}\ClipBridge.Windows.exe') + '" --send "%1"');
end;
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var Value: String;
begin
  if (CurUninstallStep = usUninstall) and RegQueryStringValue(HKCU, RunKey, 'ClipBridge', Value) then
    if CompareText(Value, '"' + ExpandConstant('{app}\ClipBridge.Windows.exe') + '" --startup') = 0 then
      RegDeleteValue(HKCU, RunKey, 'ClipBridge');
  if (CurUninstallStep = usUninstall) and RegQueryStringValue(HKCU, 'Software\Classes\*\shell\ClipBridge\command', '', Value) then
    if CompareText(Value, '"' + ExpandConstant('{app}\ClipBridge.Windows.exe') + '" --send "%1"') = 0 then
      RegDeleteKeyIncludingSubkeys(HKCU, 'Software\Classes\*\shell\ClipBridge');
  if (CurUninstallStep = usUninstall) and RegQueryStringValue(HKCU, 'Software\Classes\clipbridge-desktop\shell\open\command', '', Value) then
    if CompareText(Value, '"' + ExpandConstant('{app}\ClipBridge.Windows.exe') + '" --notification "%1"') = 0 then
      RegDeleteKeyIncludingSubkeys(HKCU, 'Software\Classes\clipbridge-desktop');
  { Pairing data in LocalAppData\ClipBridge is deliberately preserved. }
end;
