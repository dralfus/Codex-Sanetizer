param(
    [string] $InstallDirectory = "$env:LOCALAPPDATA\Programs\CodexRedactionGate",
    [string] $StartMenuDirectory = "$env:APPDATA\Microsoft\Windows\Start Menu\Programs\Codex Redaction Gate",
    [switch] $DeleteLocalData
)

$ErrorActionPreference = "Stop"

$exe = Join-Path $InstallDirectory "CodexRedactionGate.exe"

Remove-ItemProperty `
    -Path "HKCU:\Software\Microsoft\Windows\CurrentVersion\Run" `
    -Name "CodexRedactionGate" `
    -ErrorAction SilentlyContinue

if ($DeleteLocalData -and (Test-Path $exe)) {
    & $exe --local-data-cleanup --i-understand-delete-local-sensitive-data | Out-Host
    if ($LASTEXITCODE -ne 0) {
        exit $LASTEXITCODE
    }
}

if (Test-Path $StartMenuDirectory) {
    Remove-Item -LiteralPath $StartMenuDirectory -Recurse -Force
}

if (Test-Path $InstallDirectory) {
    Remove-Item -LiteralPath $InstallDirectory -Recurse -Force
}

Write-Host "uninstall_status=removed"
Write-Host "local_data_deleted=$($DeleteLocalData.ToString().ToLowerInvariant())"
