<#
.SYNOPSIS
Records Windows Sandbox worker startup before invoking the constrained worker.

.DESCRIPTION
The .wsb LogonCommand is otherwise silent when PowerShell fails before the
worker publishes worker.json. This wrapper writes only timestamps, paths, and
exception types/messages to the mapped job log; it never writes job content,
test output, or environment values.
#>
[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$jobsRoot = Join-Path $repositoryRoot '.sandbox-jobs'
$startupLog = Join-Path $jobsRoot 'worker-startup.log'

function Write-StartupLog {
    param([Parameter(Mandatory)] [string] $Message)

    [System.IO.Directory]::CreateDirectory($jobsRoot) | Out-Null
    $line = "{0} {1}`r`n" -f [DateTime]::UtcNow.ToString('O'), $Message
    [System.IO.File]::AppendAllText($startupLog, $line, [System.Text.UTF8Encoding]::new($false))
}

try {
    Write-StartupLog "bootstrap_started script=$PSScriptRoot"
    & (Join-Path $PSScriptRoot 'SandboxTestWorker.ps1')
    Write-StartupLog "worker_exited exit_code=$LASTEXITCODE"
}
catch {
    Write-StartupLog "worker_start_failed type=$($_.Exception.GetType().FullName) message=$($_.Exception.Message)"
    exit 1
}
