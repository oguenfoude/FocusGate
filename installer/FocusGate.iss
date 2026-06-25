[Setup]
AppId={{FocusGate-5582A6E3-5F17-2901}
AppName=FocusGate
AppVersion=2.0.0
AppPublisher=FocusGate
DefaultDirName={autopf}\FocusGate
DefaultGroupName=FocusGate
OutputDir=..\dist
OutputBaseFilename=FocusGate-Setup
Compression=lzma2
SolidCompression=yes
PrivilegesRequired=admin
WizardStyle=modern
WizardSizePercent=110
SetupIconFile=..\src\FocusGate.Desktop\icon.ico
UninstallDisplayIcon={app}\FocusGate.exe
CloseApplications=yes
RestartApplications=no

[Files]
Source: "..\dist\FocusGate.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\dist\FocusGate.Desktop.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\dist\*.dll"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\dist\icon.ico"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\dist\*.json"; DestDir: "{app}"; Flags: ignoreversion onlyifdoesntexist skipifsourcedoesntexist

[Dirs]
Name: "{app}\data"; Flags: uninsalwaysuninstall

[Icons]
Name: "{group}\FocusGate"; Filename: "{app}\FocusGate.exe"; WorkingDir: "{app}"
Name: "{group}\Uninstall FocusGate"; Filename: "{uninstallexe}"
Name: "{commondesktop}\FocusGate"; Filename: "{app}\FocusGate.exe"; WorkingDir: "{app}"

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "FocusGate"; ValueData: """{app}\FocusGate.exe"""; Flags: uninsdeletevalue

[Run]
Filename: "{app}\FocusGate.exe"; Description: "Start FocusGate now"; Flags: postinstall nowait skipifsilent

[UninstallDelete]
Type: filesandordirs; Name: "{app}\data"

[Code]
function InitializeSetup(): Boolean;
var
  ResultCode: Integer;
begin
  Exec('cmd.exe', '/C taskkill /F /IM FocusGate.exe /T', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Exec('cmd.exe', '/C taskkill /F /IM FocusGate.Desktop.exe /T', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Sleep(2000);
  Result := True;
end;
