param(
    [string] $Project = "src\CodexRedactionGate\CodexRedactionGate.csproj",
    [switch] $NoCache
)

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
. (Join-Path $PSScriptRoot "resolve-dotnet-sdk.ps1")
$dotnet = Resolve-Net10Sdk -RepositoryRoot $repoRoot

Write-Host "dotnet_sdk_host=$dotnet"
Write-Host "restore_source=nuget.org"
Write-Host "restore_signature_validation=enabled"

$projectPath = [System.IO.Path]::GetFullPath((Join-Path $repoRoot $Project))
$arguments = @("restore", $projectPath, "--nologo")
if ($NoCache) {
    $arguments += "--no-cache"
}

$restoreOutput = & $dotnet @arguments 2>&1 | Out-String
$restoreExitCode = $LASTEXITCODE
if ($restoreExitCode -eq 0) {
    Write-Host "restore_status=passed"
    exit 0
}

if ($restoreOutput -match "NU1301|repository-signatures|SSL|TLS|authentication") {
    Write-Error "restore_status=failed"
    Write-Error "restore_code=nuget_source_or_tls_unavailable"
    Write-Error "restore_action=check_windows_proxy_tls_and_nuget_connectivity_then_retry"
    Write-Error "restore_signature_validation=enabled"
}
else {
    Write-Error "restore_status=failed"
    Write-Error "restore_code=nuget_restore_failed"
    Write-Error "restore_action=review_dotnet_restore_diagnostics_and_retry"
}

exit $restoreExitCode
