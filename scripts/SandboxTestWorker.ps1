<#!
.SYNOPSIS
Runs only fixed restore/test jobs for the mapped Windows Sandbox worktree.

.DESCRIPTION
The worker intentionally does not accept a command line, executable path,
working directory, or PowerShell from a job file. Its only accepted jobs are
`restore` and `test` for the one project below.
#>
[CmdletBinding()]
param(
    [switch] $ValidateOnly
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$projectRelativePath = 'src\CodexRedactionGate\CodexRedactionGate.csproj'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$projectPath = Join-Path $repositoryRoot $projectRelativePath
$dotnetPath = 'C:\HostDotnet\dotnet.exe'
$jobsRoot = Join-Path $repositoryRoot '.sandbox-jobs'
$inboxPath = Join-Path $jobsRoot 'inbox'
$runningPath = Join-Path $jobsRoot 'running'
$resultsPath = Join-Path $jobsRoot 'results'

function Write-JsonFile {
    param(
        [Parameter(Mandatory)] [string] $Path,
        [Parameter(Mandatory)] $Value
    )

    $temporaryPath = "$Path.$PID.tmp"
    $Value | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $temporaryPath -Encoding utf8NoBOM
    Move-Item -LiteralPath $temporaryPath -Destination $Path -Force
}

function Get-SandboxProxy {
    $route = Get-NetRoute -DestinationPrefix '0.0.0.0/0' |
        Where-Object { $_.NextHop -and $_.NextHop -ne '0.0.0.0' } |
        Sort-Object RouteMetric |
        Select-Object -First 1

    if ($null -eq $route) {
        throw 'sandbox_default_gateway_unavailable'
    }

    return "http://$($route.NextHop):10808"
}

function Set-WorkerState {
    param([Parameter(Mandatory)] [string] $State)

    Write-JsonFile -Path (Join-Path $jobsRoot 'worker.json') -Value ([ordered]@{
        State = $State
        StartedAtUtc = [DateTime]::UtcNow.ToString('O')
        Project = $projectRelativePath
    })
}

function Test-Job {
    param([Parameter(Mandatory)] $Job)

    $allowedProperties = @('Id', 'Kind', 'Filter')
    foreach ($property in $Job.PSObject.Properties.Name) {
        if ($property -notin $allowedProperties) { throw 'job_contains_unsupported_property' }
    }

    if ($Job.Id -isnot [string] -or $Job.Id -notmatch '^[a-z0-9][a-z0-9-]{2,80}$') {
        throw 'job_id_invalid'
    }
    if ($Job.Kind -notin @('restore', 'test')) { throw 'job_kind_invalid' }

    if ($Job.Kind -eq 'restore' -and $null -ne $Job.Filter) { throw 'restore_filter_not_allowed' }
    if ($Job.Kind -eq 'test' -and $null -ne $Job.Filter -and
        ($Job.Filter -isnot [string] -or $Job.Filter -notmatch '^FullyQualifiedName~[A-Za-z0-9_.]+$')) {
        throw 'test_filter_invalid'
    }
}

function Invoke-Job {
    param([Parameter(Mandatory)] $Job)

    $proxy = Get-SandboxProxy
    $previousEnvironment = @{}
    foreach ($name in @('HTTP_PROXY', 'HTTPS_PROXY', 'ALL_PROXY', 'NO_PROXY')) {
        $previousEnvironment[$name] = [Environment]::GetEnvironmentVariable($name, 'Process')
    }

    try {
        [Environment]::SetEnvironmentVariable('HTTP_PROXY', $proxy, 'Process')
        [Environment]::SetEnvironmentVariable('HTTPS_PROXY', $proxy, 'Process')
        [Environment]::SetEnvironmentVariable('ALL_PROXY', $proxy, 'Process')
        [Environment]::SetEnvironmentVariable('NO_PROXY', 'localhost,127.0.0.1', 'Process')

        $arguments = if ($Job.Kind -eq 'restore') {
            @('restore', $projectPath, '--disable-parallel', '--nologo')
        }
        else {
            $testArguments = @('test', $projectPath, '--disable-parallel', '--nologo', '-p:UseAppHost=false')
            if ($null -ne $Job.Filter) { $testArguments += @('--filter', $Job.Filter) }
            $testArguments
        }

        $output = & $dotnetPath @arguments 2>&1 | Out-String
        return [ordered]@{ ExitCode = $LASTEXITCODE; Output = $output; Proxy = $proxy }
    }
    finally {
        foreach ($name in $previousEnvironment.Keys) {
            [Environment]::SetEnvironmentVariable($name, $previousEnvironment[$name], 'Process')
        }
    }
}

foreach ($directory in @($inboxPath, $runningPath, $resultsPath)) {
    [System.IO.Directory]::CreateDirectory($directory) | Out-Null
}
if (-not (Test-Path -LiteralPath $projectPath -PathType Leaf)) { throw 'fixed_project_missing' }
if (-not (Test-Path -LiteralPath $dotnetPath -PathType Leaf)) { throw 'sandbox_dotnet_missing' }

Set-WorkerState -State 'ready'
if ($ValidateOnly) { return }

while ($true) {
    $jobs = Get-ChildItem -LiteralPath $inboxPath -Filter '*.json' -File | Sort-Object Name
    foreach ($jobFile in $jobs) {
        $runningFile = Join-Path $runningPath $jobFile.Name
        if (Test-Path -LiteralPath $runningFile) {
            # Preserve the duplicate request for audit; an operator must submit a new Id.
            continue
        }

        try {
            Move-Item -LiteralPath $jobFile.FullName -Destination $runningFile -ErrorAction Stop
        }
        catch {
            continue
        }

        $jobId = [System.IO.Path]::GetFileNameWithoutExtension($jobFile.Name)
        $resultPath = Join-Path $resultsPath "$jobId.json"
        $logPath = Join-Path $resultsPath "$jobId.log"
        $startedAtUtc = [DateTime]::UtcNow.ToString('O')
        $exitCode = 1
        $proxy = $null
        $output = ''
        $status = 'rejected'
        $failure = 'job_invalid'
        $job = $null

        try {
            $job = Get-Content -Raw -LiteralPath $runningFile | ConvertFrom-Json
            Test-Job -Job $job
            if ($job.Id -ne $jobId) { throw 'job_filename_id_mismatch' }

            $execution = Invoke-Job -Job $job
            $exitCode = $execution.ExitCode
            $proxy = $execution.Proxy
            $output = $execution.Output
            $status = if ($exitCode -eq 0) { 'completed' } else { 'failed' }
            $failure = if ($exitCode -eq 0) { $null } else { 'dotnet_exit_nonzero' }
        }
        catch {
            $output = "worker_failure=$($_.Exception.Message)"
            $failure = 'worker_rejected_or_failed'
        }

        Set-Content -LiteralPath $logPath -Value $output -Encoding utf8NoBOM
        Write-JsonFile -Path $resultPath -Value ([ordered]@{
            Id = $jobId
            Kind = if ($null -eq $job) { $null } else { $job.Kind }
            Filter = if ($null -eq $job) { $null } else { $job.Filter }
            Project = $projectRelativePath
            Proxy = $proxy
            StartedAtUtc = $startedAtUtc
            FinishedAtUtc = [DateTime]::UtcNow.ToString('O')
            ExitCode = $exitCode
            Status = $status
            Failure = $failure
        })
        Set-WorkerState -State 'ready'
    }

    Start-Sleep -Seconds 1
}
