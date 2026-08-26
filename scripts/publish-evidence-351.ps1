param(
    [string] $PublishDirectory = "artifacts\publish",
    [string] $BuildVersion = "",
    [string] $VerificationArtifactPath = "artifacts\evidence\351-proof.txt"
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
$verificationArtifact = [System.IO.Path]::GetFullPath((Join-Path $repoRoot $VerificationArtifactPath))
if (-not (Test-Path -LiteralPath $verificationArtifact)) {
    throw "Verification artifact was not found: $verificationArtifact"
}
$verificationArtifactSha256 = (Get-FileHash -LiteralPath $verificationArtifact -Algorithm SHA256).Hash.ToLowerInvariant()
$recordPath = Join-Path $repoRoot "artifacts\evidence\351.json"
$recordDirectory = Split-Path -Parent $recordPath

function Assert-NoReparsePoint {
    param([string] $Path)

    if (Test-Path -LiteralPath $Path) {
        $attributes = (Get-Item -LiteralPath $Path -Force).Attributes
        if (($attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "Evidence path contains a reparse point."
        }
    }
}

Assert-NoReparsePoint -Path $repoRoot
Assert-NoReparsePoint -Path (Join-Path $repoRoot "artifacts")
Assert-NoReparsePoint -Path $verificationArtifact
Assert-NoReparsePoint -Path $recordDirectory
Assert-NoReparsePoint -Path $recordPath
New-Item -ItemType Directory -Force -Path $recordDirectory | Out-Null

$record = [ordered]@{
    schema_version = "1"
    ticket_id = "ticket_351"
    behavior_id = "protected_send_evidence"
    state = "locally_verified"
    reproduction_id = "repro_protected_send"
    reproduction_command_id = "cmd_repro_protected_send"
    highest_required_seam = "deterministic_transaction"
    build_version = $publishedBuildVersion
    source_commit = $sourceCommit
    executable_sha256 = $executableSha256
    installer_identity = "not_applicable"
    compatibility_fingerprint = $executableSha256
    submit_binding = "not_applicable"
    transition_history = @("proposed", "reproduced_red", "implemented", "locally_verified")
    claim = "unverified"
    validator_artifact_sha256 = $executableSha256
    verification_artifact_sha256 = $verificationArtifactSha256
}

$temporaryRecordPath = "$recordPath.$([Guid]::NewGuid().ToString('N')).tmp"
$record | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $temporaryRecordPath -Encoding utf8NoBOM
try {
    [System.IO.File]::Move($temporaryRecordPath, $recordPath, $true)
}
catch {
    Remove-Item -LiteralPath $temporaryRecordPath -Force -ErrorAction SilentlyContinue
    throw
}

& $executable --evidence-current-record-check `
    $repoRoot `
    $publishedBuildVersion `
    $sourceCommit `
    $executableSha256 `
    $executableSha256 `
    $verificationArtifactSha256
if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}

Write-Output "evidence_351: locally_verified"
Write-Output "evidence_351_record: artifacts/evidence/351.json"
