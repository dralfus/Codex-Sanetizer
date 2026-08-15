param(
    [string] $Configuration = "Release",
    [string] $Runtime = "win-x64",
    [string] $InnoSetupCompiler = "",
    [string] $BuildVersion = "",
    [string] $PublishDirectory = "artifacts\publish",
    [string] $InstallerOutputDirectory = "artifacts\installer",
    [string] $ScannerSourceDirectory = "artifacts\scanners\gitleaks",
    [switch] $RequireScannerPackage
)

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($BuildVersion)) {
    $now = Get-Date
    $BuildVersion = "0.1.$($now.ToString('yyyyMMdd')).t$($now.ToString('HHmm'))"
}

& (Join-Path $PSScriptRoot "build-release.ps1") `
    -Configuration $Configuration `
    -Runtime $Runtime `
    -OutputDirectory $PublishDirectory `
    -ScannerSourceDirectory $ScannerSourceDirectory `
    -BuildVersion $BuildVersion `
    -RequireScannerPackage:$RequireScannerPackage

if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}

if ([string]::IsNullOrWhiteSpace($InnoSetupCompiler)) {
    $candidates = New-Object System.Collections.Generic.List[string]
    $resolvedIscc = Get-Command iscc -ErrorAction SilentlyContinue
    if ($resolvedIscc -and $resolvedIscc.Path) {
        $candidates.Add($resolvedIscc.Path)
    }

    foreach ($candidate in @(
        (Join-Path $env:LOCALAPPDATA "Programs\Inno Setup 6\ISCC.exe"),
        "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
        "$env:ProgramFiles\Inno Setup 6\ISCC.exe"
    )) {
        if ((Test-Path $candidate) -and -not $candidates.Contains($candidate)) {
            $candidates.Add($candidate)
        }
    }

    $InnoSetupCompiler = $candidates | Select-Object -First 1
}

if ([string]::IsNullOrWhiteSpace($InnoSetupCompiler) -or -not (Test-Path $InnoSetupCompiler)) {
    throw "Inno Setup compiler not found. Pass -InnoSetupCompiler with a path to ISCC.exe."
}

$installerOutput = Join-Path $repoRoot $InstallerOutputDirectory
New-Item -ItemType Directory -Force $installerOutput | Out-Null
Get-ChildItem -LiteralPath $installerOutput -Filter "CodexRedactionGateSetup-*.exe" -File -ErrorAction SilentlyContinue |
    Remove-Item -Force

$script = Join-Path $repoRoot "packaging\windows\CodexRedactionGate.iss"
& $InnoSetupCompiler `
    "/DMyAppVersion=$BuildVersion" `
    "/DSourceDir=$(Join-Path $repoRoot $PublishDirectory)" `
    "/DOutputDir=$installerOutput" `
    $script

if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}

$installerPath = Join-Path $installerOutput "CodexRedactionGateSetup-$BuildVersion.exe"
if (-not (Test-Path $installerPath)) {
    throw "Expected installer was not created: $installerPath"
}

$installerProductVersion = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($installerPath).ProductVersion
if ([string]::IsNullOrWhiteSpace($installerProductVersion) -or
    -not [string]::Equals($installerProductVersion.Trim(), $BuildVersion.Trim(), [System.StringComparison]::Ordinal)) {
    throw "Installer version smoke failed. Expected '$BuildVersion', got '$installerProductVersion'."
}

Write-Host "installer_output=$installerOutput"
Write-Host "installer_path=$installerPath"
Write-Host "installer_product_version=$installerProductVersion"
Write-Host "installer_version_smoke=passed"
