param(
    [string] $SourceDirectory = "artifacts\publish",
    [string] $InstallDirectory = "$env:LOCALAPPDATA\Programs\CodexRedactionGate",
    [string] $StartMenuDirectory = "$env:APPDATA\Microsoft\Windows\Start Menu\Programs\Codex Redaction Gate",
    [switch] $EnableAutostart,
    [switch] $NoLaunch,
    [switch] $StopRunning
)

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
$source = if ([System.IO.Path]::IsPathRooted($SourceDirectory)) { $SourceDirectory } else { Join-Path $repoRoot $SourceDirectory }

if (-not (Test-Path (Join-Path $source "CodexRedactionGate.exe")) -or -not (Test-Path (Join-Path $source "CodexRedactionGate.Tray.exe"))) {
    throw "Published app was not found. Run scripts\build-release.ps1 first."
}

$exe = Join-Path $InstallDirectory "CodexRedactionGate.exe"
$trayExe = Join-Path $InstallDirectory "CodexRedactionGate.Tray.exe"

function Stop-InstalledProcessesIfNeeded {
    param(
        [string[]] $TargetPaths,
        [switch] $Confirmed
    )

    $fullTargetPaths = $TargetPaths | ForEach-Object { [System.IO.Path]::GetFullPath($_) }
    $running = @(Get-Process -ErrorAction SilentlyContinue | Where-Object {
        $processPath = $null
        try {
            $processPath = $_.Path
        }
        catch {
            $processPath = $null
        }

        if ([string]::IsNullOrWhiteSpace($processPath)) {
            return $false
        }

        $fullProcessPath = [System.IO.Path]::GetFullPath($processPath)
        foreach ($targetPath in $fullTargetPaths) {
            if ([string]::Equals($fullProcessPath, $targetPath, [System.StringComparison]::OrdinalIgnoreCase)) {
                return $true
            }
        }

        return $false
    })

    if ($running.Count -eq 0) {
        return
    }

    Write-Warning "Code Sanitizer is currently running from the install directory."
    Write-Warning "Updating it must stop resident protection; selected AI apps will no longer be protected until the tray app starts again."

    if (-not $Confirmed) {
        $answer = Read-Host "Type YES to stop Code Sanitizer and continue installation"
        if ($answer -ne "YES") {
            throw "Install canceled. Code Sanitizer is still running. Exit it from the tray, or rerun install-user.ps1 with -StopRunning."
        }
    }

    foreach ($process in ($running | Sort-Object Id -Unique)) {
        Stop-Process -Id $process.Id -Force -ErrorAction Stop
        try {
            Wait-Process -Id $process.Id -Timeout 5 -ErrorAction SilentlyContinue
        }
        catch {
        }
    }
}

Stop-InstalledProcessesIfNeeded -TargetPaths @($exe, $trayExe) -Confirmed:$StopRunning

New-Item -ItemType Directory -Force $InstallDirectory | Out-Null
Copy-Item -Path (Join-Path $source "*") -Destination $InstallDirectory -Recurse -Force

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
