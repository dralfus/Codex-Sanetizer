<#
.SYNOPSIS
Runs the local development suite while excluding tests that require the
reference-composer UI fixture or an interactive desktop.
#>
[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$projectPath = Join-Path $repositoryRoot 'src\CodexRedactionGate\CodexRedactionGate.csproj'
$runId = [DateTime]::UtcNow.ToString('yyyyMMddHHmmssfff')
$resultsDirectory = Join-Path $repositoryRoot (Join-Path 'artifacts\non-interactive' $runId)
$trxName = 'non-interactive.trx'

[IO.Directory]::CreateDirectory($resultsDirectory) | Out-Null
& dotnet test $projectPath --nologo '-p:UseAppHost=false' `
    '--filter' 'Category!=interactive-fixture' `
    '--results-directory' $resultsDirectory `
    '--logger' "trx;LogFileName=$trxName"
$exitCode = $LASTEXITCODE

$trxPath = Join-Path $resultsDirectory $trxName
if (-not (Test-Path -LiteralPath $trxPath -PathType Leaf)) {
    throw 'non_interactive_trx_missing'
}

[xml] $trx = Get-Content -LiteralPath $trxPath -Raw
$total = [int] $trx.TestRun.ResultSummary.Counters.total
if ($total -le 0) {
    throw 'non_interactive_trx_total_zero'
}

Write-Output "non_interactive_trx=$trxPath total=$total exit_code=$exitCode"
exit $exitCode
