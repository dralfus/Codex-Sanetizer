param(
    [string] $SourceDirectory = "artifacts\publish",
    [string] $InstallDirectory = "$env:LOCALAPPDATA\Programs\CodexRedactionGate",
    [string] $StartMenuDirectory = "$env:APPDATA\Microsoft\Windows\Start Menu\Programs\Codex Redaction Gate",
    [switch] $EnableAutostart,
    [switch] $NoLaunch
)

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
$source = if ([System.IO.Path]::IsPathRooted($SourceDirectory)) { $SourceDirectory } else { Join-Path $repoRoot $SourceDirectory }

if (-not (Test-Path (Join-Path $source "CodexRedactionGate.exe")) -or -not (Test-Path (Join-Path $source "CodexRedactionGate.Tray.exe"))) {
    throw "Published app was not found. Run scripts\build-release.ps1 first."
}

New-Item -ItemType Directory -Force $InstallDirectory | Out-Null
Copy-Item -Path (Join-Path $source "*") -Destination $InstallDirectory -Recurse -Force

$exe = Join-Path $InstallDirectory "CodexRedactionGate.exe"
$trayExe = Join-Path $InstallDirectory "CodexRedactionGate.Tray.exe"
New-Item -ItemType Directory -Force $StartMenuDirectory | Out-Null

$shell = New-Object -ComObject WScript.Shell
$shortcuts = @(
    @{ Name = "Codex Redaction Gate.lnk"; Target = $trayExe; Arguments = "" },
    @{ Name = "Diagnostics.lnk"; Target = $exe; Arguments = "--doctor" },
    @{ Name = "Audit viewer.lnk"; Target = $exe; Arguments = "--audit-view" }
)

foreach ($item in $shortcuts) {
    $shortcut = $shell.CreateShortcut((Join-Path $StartMenuDirectory $item.Name))
    $shortcut.TargetPath = $item.Target
    $shortcut.Arguments = $item.Arguments
    $shortcut.WorkingDirectory = $InstallDirectory
    $shortcut.Save()
}

if ($EnableAutostart) {
    & $exe --autostart-enable | Out-Host
    if ($LASTEXITCODE -ne 0) {
        exit $LASTEXITCODE
    }
}

if (-not $NoLaunch) {
    Start-Process -FilePath $trayExe -WindowStyle Hidden
}

Write-Host "install_status=installed"
Write-Host "install_path_length=$($InstallDirectory.Length)"
Write-Host "start_menu_path_length=$($StartMenuDirectory.Length)"
