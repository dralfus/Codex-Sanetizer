param(
    [string] $Configuration = "Release",
    [string] $Runtime = "win-x64",
    [string] $InnoSetupCompiler = "",
    [string] $PublishDirectory = "artifacts\publish",
    [string] $InstallerOutputDirectory = "artifacts\installer",
    [string] $ScannerSourceDirectory = "artifacts\scanners\gitleaks",
    [switch] $RequireScannerPackage
)

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
& (Join-Path $PSScriptRoot "build-release.ps1") `
    -Configuration $Configuration `
    -Runtime $Runtime `
    -OutputDirectory $PublishDirectory `
    -ScannerSourceDirectory $ScannerSourceDirectory `
    -RequireScannerPackage:$RequireScannerPackage

if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}

if ([string]::IsNullOrWhiteSpace($InnoSetupCompiler)) {
    $candidates = @(
        "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
        "$env:ProgramFiles\Inno Setup 6\ISCC.exe"
    )
    $InnoSetupCompiler = $candidates | Where-Object { Test-Path $_ } | Select-Object -First 1
}

if ([string]::IsNullOrWhiteSpace($InnoSetupCompiler) -or -not (Test-Path $InnoSetupCompiler)) {
    throw "Inno Setup compiler not found. Pass -InnoSetupCompiler with a path to ISCC.exe."
}

$installerOutput = Join-Path $repoRoot $InstallerOutputDirectory
New-Item -ItemType Directory -Force $installerOutput | Out-Null

$script = Join-Path $repoRoot "packaging\windows\CodexRedactionGate.iss"
& $InnoSetupCompiler `
    "/DSourceDir=$(Join-Path $repoRoot $PublishDirectory)" `
    "/DOutputDir=$installerOutput" `
    $script

if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}

Write-Host "installer_output=$installerOutput"
