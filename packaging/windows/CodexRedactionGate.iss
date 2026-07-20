#ifndef SourceDir
#define SourceDir "..\..\artifacts\publish"
#endif

#ifndef OutputDir
#define OutputDir "..\..\artifacts\installer"
#endif

[Setup]
AppId={{9A3AF91C-ED0F-4C8C-86F9-CF69F5A1A04A}
AppName=Codex Redaction Gate
AppVersion=0.1.0
AppPublisher=Codex Redaction Gate
DefaultDirName={localappdata}\Programs\CodexRedactionGate
DefaultGroupName=Codex Redaction Gate
DisableProgramGroupPage=yes
OutputDir={#OutputDir}
OutputBaseFilename=CodexRedactionGateSetup
Compression=lzma
SolidCompression=yes
PrivilegesRequired=lowest
ArchitecturesInstallIn64BitMode=x64
UninstallDisplayIcon={app}\CodexRedactionGate.exe

[Tasks]
Name: autostart; Description: "Start Codex Redaction Gate when I sign in"; GroupDescription: "Startup:"; Flags: unchecked

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\Codex Redaction Gate"; Filename: "{app}\CodexRedactionGate.exe"; Parameters: "--tray-app"
Name: "{group}\Diagnostics"; Filename: "{app}\CodexRedactionGate.exe"; Parameters: "--doctor"
Name: "{group}\Audit viewer"; Filename: "{app}\CodexRedactionGate.exe"; Parameters: "--audit-view"

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "CodexRedactionGate"; ValueData: """{app}\CodexRedactionGate.exe"" --tray-app"; Flags: uninsdeletevalue; Tasks: autostart

[Run]
Filename: "{app}\CodexRedactionGate.exe"; Parameters: "--tray-app"; Description: "Launch Codex Redaction Gate"; Flags: nowait postinstall skipifsilent

[Code]
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  ResultCode: Integer;
begin
  if CurUninstallStep = usUninstall then
  begin
    if MsgBox(
      'Codex Redaction Gate keeps local vault, dictionary, policy, audit and settings by default.' + #13#10#13#10 +
      'Delete this local sensitive data now?',
      mbConfirmation,
      MB_YESNO) = IDYES then
    begin
      Exec(
        ExpandConstant('{app}\CodexRedactionGate.exe'),
        '--local-data-cleanup --i-understand-delete-local-sensitive-data',
        '',
        SW_HIDE,
        ewWaitUntilTerminated,
        ResultCode);
    end;
  end;
end;
