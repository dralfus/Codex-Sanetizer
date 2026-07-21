param(
    [string] $Configuration = "Release",
    [string] $Runtime = "win-x64",
    [string] $OutputDirectory = "artifacts\publish",
    [string] $ScannerSourceDirectory = "artifacts\scanners\gitleaks",
    [switch] $RequireScannerPackage
)

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repoRoot "src\CodexRedactionGate\CodexRedactionGate.csproj"
$trayProject = Join-Path $repoRoot "src\CodexRedactionGate.Tray\CodexRedactionGate.Tray.csproj"
$output = Join-Path $repoRoot $OutputDirectory
$dotnet = $env:DOTNET_EXE
if ([string]::IsNullOrWhiteSpace($dotnet)) {
    $candidates = @(
        "dotnet",
        "$env:LOCALAPPDATA\Microsoft\dotnet\dotnet.exe",
        "$env:ProgramFiles\dotnet\dotnet.exe"
    )
    $dotnet = $candidates | Where-Object {
        if ($_ -eq "dotnet") {
            $null -ne (Get-Command dotnet -ErrorAction SilentlyContinue)
        }
        else {
            Test-Path $_
        }
    } | Select-Object -First 1
}

if ([string]::IsNullOrWhiteSpace($dotnet)) {
    throw "dotnet SDK was not found. Set DOTNET_EXE to dotnet.exe."
}

New-Item -ItemType Directory -Force $output | Out-Null

& $dotnet publish $project `
    -c $Configuration `
    -r $Runtime `
    --self-contained false `
    -p:UseAppHost=true `
    -p:PublishSingleFile=false `
    -o $output

if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}

& $dotnet publish $trayProject `
    -c $Configuration `
    -r $Runtime `
    --self-contained false `
    -p:UseAppHost=true `
    -p:PublishSingleFile=false `
    -o $output

if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}

$scannerSource = Join-Path $repoRoot $ScannerSourceDirectory
$scannerBinary = Join-Path $scannerSource "gitleaks.exe"
$scannerProvenance = Join-Path $scannerSource "gitleaks-provenance.json"
$hasScannerBinary = Test-Path $scannerBinary
$hasScannerProvenance = Test-Path $scannerProvenance

if ($RequireScannerPackage -and (-not $hasScannerBinary -or -not $hasScannerProvenance)) {
    throw "Required scanner package is incomplete. Expected gitleaks.exe and gitleaks-provenance.json under $scannerSource."
}

if ($hasScannerBinary -or $hasScannerProvenance) {
    if (-not $hasScannerBinary -or -not $hasScannerProvenance) {
        throw "Scanner package is partial. Provide both gitleaks.exe and gitleaks-provenance.json, or remove the partial scanner directory."
    }

    $scannerOutput = Join-Path $output "scanners\gitleaks"
    New-Item -ItemType Directory -Force $scannerOutput | Out-Null
    Copy-Item -LiteralPath $scannerBinary -Destination (Join-Path $scannerOutput "gitleaks.exe") -Force
    Copy-Item -LiteralPath $scannerProvenance -Destination (Join-Path $scannerOutput "gitleaks-provenance.json") -Force
    Write-Host "scanner_output=$scannerOutput"
}
elseif (-not $RequireScannerPackage) {
    Write-Host "scanner_output=safe_disabled_missing"
}

Write-Host "release_output=$output"
Write-Host "resident_tray_exe=$(Join-Path $output "CodexRedactionGate.Tray.exe")"
