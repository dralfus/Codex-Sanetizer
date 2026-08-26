param(
    [string] $PublishDirectory = "artifacts\publish",
    [string] $BuildVersion = ""
)

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
$publishRoot = [System.IO.Path]::GetFullPath((Join-Path $repoRoot $PublishDirectory))
$executable = Join-Path $publishRoot "CodexRedactionGate.exe"
if (-not (Test-Path -LiteralPath $executable)) {
    throw "Published executable was not found: $executable"
}

$sourceCommit = (& git -C $repoRoot rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($sourceCommit)) {
    throw "Unable to resolve the current source commit."
}

$publishedBuildVersion = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($executable).ProductVersion
if ([string]::IsNullOrWhiteSpace($publishedBuildVersion)) {
    throw "Published executable has no build version."
}

if ((-not [string]::IsNullOrWhiteSpace($BuildVersion)) -and $BuildVersion -ne $publishedBuildVersion) {
    throw "Requested build version does not match the published executable."
}

$executableSha256 = (Get-FileHash -LiteralPath $executable -Algorithm SHA256).Hash.ToLowerInvariant()
& $executable --evidence-current-record-publish `
    $repoRoot `
    $publishedBuildVersion `
    $sourceCommit `
    $executableSha256 `
    $executableSha256
if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}

& $executable --evidence-current-record-check `
    $repoRoot `
    $publishedBuildVersion `
    $sourceCommit `
    $executableSha256 `
    $executableSha256
if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}

Write-Output "evidence_351: locally_verified"
Write-Output "evidence_351_record: artifacts/evidence/351.json"
