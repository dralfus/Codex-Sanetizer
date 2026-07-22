param(
    [string] $Configuration = "Release",
    [string] $Runtime = "win-x64",
    [string] $OutputDirectory = "artifacts\publish",
    [string] $ScannerSourceDirectory = "artifacts\scanners\gitleaks",
    [string] $BuildVersion = "",
    [switch] $RequireScannerPackage
)

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repoRoot "src\CodexRedactionGate\CodexRedactionGate.csproj"
$trayProject = Join-Path $repoRoot "src\CodexRedactionGate.Tray\CodexRedactionGate.Tray.csproj"
$output = Join-Path $repoRoot $OutputDirectory
$workingOutput = Join-Path $repoRoot "artifacts\publish-work"
$consoleOutput = Join-Path $workingOutput "console"
$trayOutput = Join-Path $workingOutput "tray"
$repoFullPath = [System.IO.Path]::GetFullPath($repoRoot)
$outputFullPath = [System.IO.Path]::GetFullPath($output)
$workingOutputFullPath = [System.IO.Path]::GetFullPath($workingOutput)

function Assert-UnderRepository {
    param(
        [string] $Path,
        [string] $Purpose
    )

    $fullPath = [System.IO.Path]::GetFullPath($Path)
    $repoBoundary = $repoFullPath.TrimEnd([System.IO.Path]::DirectorySeparatorChar, [System.IO.Path]::AltDirectorySeparatorChar) + [System.IO.Path]::DirectorySeparatorChar
    if (-not $fullPath.StartsWith($repoBoundary, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to clean $Purpose outside the repository: $fullPath"
    }
}

function Remove-TestPublishArtifacts {
    param([string] $PublishOutput)

    $filePatterns = @(
        "Microsoft.CodeCoverage*",
        "Microsoft.TestPlatform*",
        "Microsoft.VisualStudio.CodeCoverage*",
        "Microsoft.VisualStudio.TestPlatform*",
        "Microsoft.VisualStudio.TraceDataCollector*",
        "Mono.Cecil*",
        "Newtonsoft.Json.dll",
        "nunit.*",
        "NUnit3.TestAdapter*",
        "testcentric.*",
        "testhost.*",
        "ThirdPartyNotices.txt"
    )

    foreach ($pattern in $filePatterns) {
        Get-ChildItem -LiteralPath $PublishOutput -Recurse -File -Filter $pattern -ErrorAction SilentlyContinue |
            Remove-Item -Force
    }

    foreach ($directoryName in @("CodeCoverage", "InstrumentationEngine")) {
        $directory = Join-Path $PublishOutput $directoryName
        if (Test-Path $directory) {
            Remove-Item -LiteralPath $directory -Recurse -Force
        }
    }

    Get-ChildItem -LiteralPath $PublishOutput -Recurse -Directory -ErrorAction SilentlyContinue |
        Sort-Object FullName -Descending |
        Where-Object { -not (Get-ChildItem -LiteralPath $_.FullName -Force -ErrorAction SilentlyContinue) } |
        Remove-Item -Force
}

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

Assert-UnderRepository -Path $outputFullPath -Purpose "publish output"
Assert-UnderRepository -Path $workingOutputFullPath -Purpose "temporary publish output"

if (Test-Path $output) {
    Remove-Item -LiteralPath $output -Recurse -Force
}

if (Test-Path $workingOutput) {
    Remove-Item -LiteralPath $workingOutput -Recurse -Force
}

New-Item -ItemType Directory -Force $output | Out-Null
New-Item -ItemType Directory -Force $consoleOutput | Out-Null
New-Item -ItemType Directory -Force $trayOutput | Out-Null

$versionProperties = @()
if (-not [string]::IsNullOrWhiteSpace($BuildVersion)) {
    $versionProperties = @(
        "-p:InformationalVersion=$BuildVersion",
        "-p:IncludeSourceRevisionInInformationalVersion=false"
    )
}

& $dotnet publish $project `
    -c $Configuration `
    -r $Runtime `
    --self-contained true `
    -p:UseAppHost=true `
    -p:PublishSingleFile=false `
    @versionProperties `
    -o $consoleOutput

if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}

& $dotnet publish $trayProject `
    -c $Configuration `
    -r $Runtime `
    --self-contained true `
    -p:UseAppHost=true `
    -p:PublishSingleFile=false `
    @versionProperties `
    -o $trayOutput

if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}

Copy-Item -Path (Join-Path $consoleOutput "*") -Destination $output -Recurse -Force
Copy-Item -Path (Join-Path $trayOutput "*") -Destination $output -Recurse -Force
Copy-Item -Path (Join-Path $consoleOutput "CodexRedactionGate.*") -Destination $output -Force

Remove-TestPublishArtifacts -PublishOutput $output
Remove-Item -LiteralPath $workingOutput -Recurse -Force

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
