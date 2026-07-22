#ifndef SourceDir
#define SourceDir "..\..\artifacts\publish"
#endif

#ifndef OutputDir
#define OutputDir "..\..\artifacts\installer"
#endif

#ifndef MyAppVersion
#define MyAppVersion "0.1.0"
#endif

[Setup]
AppId={{9A3AF91C-ED0F-4C8C-86F9-CF69F5A1A04A}
AppName=Codex Redaction Gate
AppVersion={#MyAppVersion}
AppVerName=Codex Redaction Gate {#MyAppVersion}
UninstallDisplayName=Codex Redaction Gate {#MyAppVersion}
AppPublisher=Codex Redaction Gate
DefaultDirName={localappdata}\Programs\CodexRedactionGate
DefaultGroupName=Codex Redaction Gate
DisableProgramGroupPage=yes
OutputDir={#OutputDir}
OutputBaseFilename=CodexRedactionGateSetup-{#MyAppVersion}
Compression=lzma
SolidCompression=yes
PrivilegesRequired=lowest
ArchitecturesInstallIn64BitMode=x64compatible
UninstallDisplayIcon={app}\CodexRedactionGate.Tray.exe
CloseApplications=no
RestartApplications=no

[Tasks]
Name: autostart; Description: "Start Codex Redaction Gate when I sign in"; GroupDescription: "Startup:"; Flags: unchecked

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\Codex Redaction Gate"; Filename: "{app}\CodexRedactionGate.Tray.exe"
Name: "{group}\Diagnostics"; Filename: "{app}\CodexRedactionGate.exe"; Parameters: "--doctor"
Name: "{group}\Audit viewer"; Filename: "{app}\CodexRedactionGate.exe"; Parameters: "--audit-view"

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "CodexRedactionGate"; ValueData: """{app}\CodexRedactionGate.Tray.exe"""; Flags: uninsdeletevalue; Tasks: autostart

[Run]
Filename: "{app}\CodexRedactionGate.Tray.exe"; Description: "Launch Codex Redaction Gate"; Flags: nowait postinstall skipifsilent

[Code]
function RunPowerShell(Command: String; var ResultCode: Integer): Boolean;
begin
  Result := Exec(
    ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'),
    '-NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -Command "' + Command + '"',
    '',
    SW_HIDE,
    ewWaitUntilTerminated,
    ResultCode);
end;

function InstalledProcessCommand(ExitWhenFound: Boolean): String;
var
  AppPath: String;
begin
  AppPath := ExpandConstant('{app}');
  StringChangeEx(AppPath, '''', '''''', True);
  Result :=
    '$app=[IO.Path]::GetFullPath(''' + AppPath + ''');' +
    '$matches=@(Get-Process CodexRedactionGate* -ErrorAction SilentlyContinue | Where-Object { try { [IO.Path]::GetFullPath($_.Path).StartsWith($app,[StringComparison]::OrdinalIgnoreCase) } catch { $false } });';

  if ExitWhenFound then
  begin
    Result := Result + 'if($matches.Count -gt 0){exit 0}else{exit 1}';
  end
  else
  begin
    Result := Result + 'foreach($p in $matches){Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue};Start-Sleep -Milliseconds 1500;' +
      '$left=@(Get-Process CodexRedactionGate* -ErrorAction SilentlyContinue | Where-Object { try { [IO.Path]::GetFullPath($_.Path).StartsWith($app,[StringComparison]::OrdinalIgnoreCase) } catch { $false } });' +
      'if($left.Count -gt 0){exit 2}else{exit 0}';
  end;
end;

function IsCodeSanitizerRunning(): Boolean;
var
  ResultCode: Integer;
begin
  Result := RunPowerShell(InstalledProcessCommand(True), ResultCode) and (ResultCode = 0);
end;

function StopCodeSanitizerProcesses(): Boolean;
var
  ResultCode: Integer;
begin
  Result := RunPowerShell(InstalledProcessCommand(False), ResultCode) and (ResultCode = 0);
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  Result := '';
  if not IsCodeSanitizerRunning() then
  begin
    Exit;
  end;

  if MsgBox(
    'Code Sanitizer is currently running.' + #13#10#13#10 +
    'The installer must stop resident protection before updating files. Selected AI apps will not be protected until setup launches Code Sanitizer again.' + #13#10#13#10 +
    'Continue and stop Code Sanitizer now?',
    mbConfirmation,
    MB_OKCANCEL) <> IDOK then
  begin
    Result := 'Installation canceled. Code Sanitizer is still running.';
    Exit;
  end;

  if not StopCodeSanitizerProcesses() then
  begin
    Result := 'Code Sanitizer could not be stopped. Close it from Task Manager and run setup again.';
  end;
end;

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
