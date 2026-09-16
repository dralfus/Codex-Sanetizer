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
    [switch] $ValidateOnly,
    [IntPtr] $InteractiveHostHandle = [IntPtr]::Zero,
    [bool] $InteractiveHostPresent = $false,
    [bool] $InteractiveHostForegroundReady = $false,
    [string] $InteractiveHostAttemptedAtUtc = 'unavailable',
    [int] $InteractiveHostAttempt = 0,
    [string] $InteractiveSessionId = ''
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

class InteractiveHostReadiness {
    [bool] $Present
    [bool] $ForegroundReady
    [string] $AttemptedAtUtc
    [int] $Attempt

    InteractiveHostReadiness(
        [bool] $present,
        [bool] $foregroundReady,
        [string] $attemptedAtUtc,
        [int] $attempt) {
        $this.Present = $present
        $this.ForegroundReady = $foregroundReady
        $this.AttemptedAtUtc = $attemptedAtUtc
        $this.Attempt = $attempt
    }
}

$projectRelativePath = 'src\CodexRedactionGate\CodexRedactionGate.csproj'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$projectPath = Join-Path $repositoryRoot $projectRelativePath
$dotnetPath = 'C:\HostDotnet\dotnet.exe'
$jobsRoot = Join-Path $repositoryRoot '.sandbox-jobs'
$inboxPath = Join-Path $jobsRoot 'inbox'
$runningPath = Join-Path $jobsRoot 'running'
$resultsPath = Join-Path $jobsRoot 'results'
$interruptedPath = Join-Path $jobsRoot 'interrupted'
$interactiveLeasesPath = Join-Path $jobsRoot 'interactive-leases'
$permitsPath = Join-Path $jobsRoot 'permits'
$identityPath = Join-Path $jobsRoot 'identity'
$utf8NoBom = New-Object System.Text.UTF8Encoding($false)
$script:interactiveHostForegroundAttempt = $InteractiveHostAttempt
$script:interactiveTestLeaseAvailable = $false
$script:interactiveTestLeaseConsumedAtUtc = 'unavailable'
$script:interactiveTestLeaseConsumedJobId = 'none'

function Initialize-InteractiveHostNative {
    Add-Type -AssemblyName System.Windows.Forms

    if (-not ('SandboxInteractiveHostNative' -as [type])) {
        Add-Type @'
using System;
using System.Runtime.InteropServices;

public static class SandboxInteractiveHostNative
{
    [DllImport("user32.dll")]
    public static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetForegroundWindow(IntPtr hWnd);

}
'@
    }
}

function Request-InteractiveHostForeground {
    param(
        [Parameter(Mandatory)] [InteractiveHostReadiness] $Readiness,
        [Parameter(Mandatory)] [datetime] $DeadlineUtc
    )

    [void] ($script:interactiveHostForegroundAttempt++)
    $attemptedAtUtc = [DateTime]::UtcNow.ToString('O')
    if (-not $InteractiveHostPresent -or $InteractiveHostHandle -eq [IntPtr]::Zero) {
        $Readiness.Present = $false
        $Readiness.ForegroundReady = $false
        $Readiness.AttemptedAtUtc = $attemptedAtUtc
        $Readiness.Attempt = $script:interactiveHostForegroundAttempt
        return
    }

    $control = [System.Windows.Forms.Control]::FromHandle($InteractiveHostHandle)
    $supervisor = $control -as [System.Windows.Forms.Form]
    if ($null -eq $supervisor -or $supervisor.IsDisposed) {
        $Readiness.Present = $false
        $Readiness.ForegroundReady = $false
        $Readiness.AttemptedAtUtc = $attemptedAtUtc
        $Readiness.Attempt = $script:interactiveHostForegroundAttempt
        return
    }

    try {
        $activation = $supervisor.BeginInvoke([System.Windows.Forms.MethodInvoker]{
            $supervisor.Show()
            $supervisor.Activate()
            [void] [SandboxInteractiveHostNative]::SetForegroundWindow($supervisor.Handle)
        })
        $activationWait = $DeadlineUtc - [DateTime]::UtcNow
        if ($activationWait -le [TimeSpan]::Zero -or
            -not $activation.AsyncWaitHandle.WaitOne($activationWait)) {
            $Readiness.Present = $true
            $Readiness.ForegroundReady = $false
            $Readiness.AttemptedAtUtc = $attemptedAtUtc
            $Readiness.Attempt = $script:interactiveHostForegroundAttempt
            return
        }

        [void] $supervisor.EndInvoke($activation)
    }
    catch {
        $Readiness.Present = $true
        $Readiness.ForegroundReady = $false
        $Readiness.AttemptedAtUtc = $attemptedAtUtc
        $Readiness.Attempt = $script:interactiveHostForegroundAttempt
        return
    }

    do {
        if ([SandboxInteractiveHostNative]::GetForegroundWindow() -eq $InteractiveHostHandle) {
            $Readiness.Present = $true
            $Readiness.ForegroundReady = $true
            $Readiness.AttemptedAtUtc = $attemptedAtUtc
            $Readiness.Attempt = $script:interactiveHostForegroundAttempt
            return
        }

        Start-Sleep -Milliseconds 50
    } while ([DateTime]::UtcNow -lt $DeadlineUtc)

    $Readiness.Present = $true
    $Readiness.ForegroundReady = $false
    $Readiness.AttemptedAtUtc = $attemptedAtUtc
    $Readiness.Attempt = $script:interactiveHostForegroundAttempt
}

function Wait-InteractiveHostForeground {
    param(
        [Parameter(Mandatory)] [InteractiveHostReadiness] $Readiness,
        [Parameter(Mandatory)] $ForegroundWaitSeconds
    )

    if (($ForegroundWaitSeconds -isnot [int] -and $ForegroundWaitSeconds -isnot [long]) -or
        $ForegroundWaitSeconds -lt 1 -or $ForegroundWaitSeconds -gt 120) {
        throw 'foreground_wait_seconds_invalid'
    }

    $deadline = [DateTime]::UtcNow.AddSeconds([double] $ForegroundWaitSeconds)
    [void] (Request-InteractiveHostForeground -Readiness $Readiness -DeadlineUtc $deadline)
    return $Readiness.ForegroundReady
}

function Write-JsonFile {
    param(
        [Parameter(Mandatory)] [string] $Path,
        [Parameter(Mandatory)] $Value
    )

    $temporaryPath = "$Path.$PID.tmp"
    [System.IO.File]::WriteAllText($temporaryPath, ($Value | ConvertTo-Json -Depth 6), $utf8NoBom)
    if ([System.IO.File]::Exists($Path)) {
        try {
            [System.IO.File]::Replace($temporaryPath, $Path, $null)
        }
        catch {
            # Windows Sandbox mapped folders can reject File.Replace even though
            # an overwrite move is supported. Do not prevent worker startup or
            # result publication when the stronger replacement primitive is
            # unavailable on that mounted filesystem.
            Move-Item -LiteralPath $temporaryPath -Destination $Path -Force
        }
    }
    else {
        [System.IO.File]::Move($temporaryPath, $Path)
    }
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
    param(
        [Parameter(Mandatory)] [string] $State,
        [Parameter(Mandatory)] $InteractiveHostReadiness
    )

    Write-JsonFile -Path (Join-Path $jobsRoot 'worker.json') -Value ([ordered]@{
        State = $State
        StartedAtUtc = [DateTime]::UtcNow.ToString('O')
        LastHeartbeatAtUtc = [DateTime]::UtcNow.ToString('O')
        Project = $projectRelativePath
        InteractiveHostPresent = $InteractiveHostReadiness.Present
        InteractiveHostForegroundReady = $InteractiveHostReadiness.ForegroundReady
        InteractiveHostAttemptedAtUtc = $InteractiveHostReadiness.AttemptedAtUtc
        InteractiveHostAttempt = $InteractiveHostReadiness.Attempt
        InteractiveTestLeaseAvailable = $script:interactiveTestLeaseAvailable
        InteractiveTestLeaseConsumedAtUtc = $script:interactiveTestLeaseConsumedAtUtc
        InteractiveTestLeaseConsumedJobId = $script:interactiveTestLeaseConsumedJobId
        InteractiveSessionId = $InteractiveSessionId
    })
}

function Test-InteractiveSessionId {
    [Guid] $parsed = [Guid]::Empty
    return -not [string]::IsNullOrWhiteSpace($InteractiveSessionId) -and
        [Guid]::TryParseExact($InteractiveSessionId, 'D', [ref] $parsed) -and
        $InteractiveSessionId -ceq $parsed.ToString('D')
}

function Try-ConsumeInteractiveTestLease {
    param([Parameter(Mandatory)] [string] $JobId)

    if (-not $script:interactiveTestLeaseAvailable) {
        return $false
    }

    $claimPath = Join-Path $interactiveLeasesPath "$InteractiveSessionId.json"
    $claim = [ordered]@{
        SessionId = $InteractiveSessionId
        ConsumedAtUtc = [DateTime]::UtcNow.ToString('O')
        JobId = $JobId
    }
    try {
        $stream = [IO.File]::Open($claimPath, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
        try {
            $bytes = $utf8NoBom.GetBytes(($claim | ConvertTo-Json -Depth 3))
            $stream.Write($bytes, 0, $bytes.Length)
            $stream.Flush()
        }
        finally {
            $stream.Dispose()
        }
    }
    catch [IO.IOException] {
        $script:interactiveTestLeaseAvailable = $false
        return $false
    }

    $script:interactiveTestLeaseAvailable = $false
    $script:interactiveTestLeaseConsumedAtUtc = $claim.ConsumedAtUtc
    $script:interactiveTestLeaseConsumedJobId = $JobId
    return $true
}

function Test-Job {
    param([Parameter(Mandatory)] $Job)

    $allowedProperties = @('Id', 'Kind', 'Filter', 'ReceiptId', 'foreground_wait_seconds')
    foreach ($property in $Job.PSObject.Properties.Name) {
        if ($property -notin $allowedProperties) { throw 'job_contains_unsupported_property' }
    }

    if ($Job.Id -isnot [string] -or $Job.Id -notmatch '^[a-z0-9][a-z0-9-]{2,80}$') {
        throw 'job_id_invalid'
    }
    if ($Job.Kind -notin @('restore', 'test')) { throw 'job_kind_invalid' }

    $filter = Get-JobFilter -Job $Job
    $receiptId = Get-JobReceiptId -Job $Job
    $foregroundWaitSeconds = Get-ForegroundWaitSeconds -Job $Job
    if ($Job.Kind -eq 'restore' -and $null -ne $filter) { throw 'restore_filter_not_allowed' }
    if ($Job.Kind -eq 'restore' -and $null -ne $receiptId) { throw 'restore_receipt_not_allowed' }
    if ($Job.Kind -eq 'test' -and $null -ne $filter -and
        ($filter -isnot [string] -or $filter -notmatch '^FullyQualifiedName~[A-Za-z0-9_.]+$')) {
        throw 'test_filter_invalid'
    }
    if ($null -ne $receiptId -and ($receiptId -isnot [string] -or $receiptId -notmatch '^[a-z0-9][a-z0-9-]{2,80}$')) {
        throw 'receipt_id_invalid'
    }
    if ($Job.Kind -eq 'test' -and $null -eq $filter -and $null -eq $receiptId) {
        throw 'full_suite_receipt_required'
    }
    if ($null -ne $receiptId -and $null -ne $filter) { throw 'receipt_filter_not_allowed' }
    if ($null -eq $receiptId -and $null -ne $foregroundWaitSeconds) { throw 'foreground_wait_seconds_invalid' }
    if ($null -ne $receiptId -and
        (($foregroundWaitSeconds -isnot [int] -and $foregroundWaitSeconds -isnot [long]) -or
        $foregroundWaitSeconds -lt 1 -or $foregroundWaitSeconds -gt 120)) {
        throw 'foreground_wait_seconds_invalid'
    }
}

function Get-JobFilter {
    param([Parameter(Mandatory)] $Job)

    $filterProperty = $Job.PSObject.Properties['Filter']
    if ($null -eq $filterProperty) { return $null }
    return $filterProperty.Value
}

function Get-JobReceiptId {
    param([Parameter(Mandatory)] $Job)

    $receiptProperty = $Job.PSObject.Properties['ReceiptId']
    if ($null -eq $receiptProperty) { return $null }
    return $receiptProperty.Value
}

function Get-ForegroundWaitSeconds {
    param([Parameter(Mandatory)] $Job)

    $property = $Job.PSObject.Properties['foreground_wait_seconds']
    if ($null -eq $property) { return $null }
    return $property.Value
}

function Test-ReceiptSourceHashMap {
    param([Parameter(Mandatory)] $Expected, [Parameter(Mandatory)] $Actual)

    $expectedNames = @($Expected.PSObject.Properties.Name | Sort-Object)
    $actualNames = @($Actual.PSObject.Properties.Name | Sort-Object)
    if (($expectedNames -join "`n") -cne ($actualNames -join "`n")) { return $false }
    foreach ($name in $expectedNames) {
        $value = $Expected.PSObject.Properties[$name].Value
        if ($value -isnot [string] -or $value -notmatch '^[A-F0-9]{64}$' -or
            $value -cne $Actual.PSObject.Properties[$name].Value) {
            return $false
        }
    }

    return $true
}

function Get-ReceiptBoundTestAdmission {
    param([Parameter(Mandatory)] $Job)

    $receiptId = Get-JobReceiptId -Job $Job
    if ($null -eq $receiptId) { return $null }

    $permitPath = Join-Path $permitsPath "$($Job.Id).permit.json"
    $receiptPath = Join-Path $identityPath "$receiptId.json"
    if (-not (Test-Path -LiteralPath $permitPath -PathType Leaf) -or
        -not (Test-Path -LiteralPath $receiptPath -PathType Leaf)) {
        throw 'identity_permit_missing'
    }

    try {
        $permit = Get-Content -LiteralPath $permitPath -Raw | ConvertFrom-Json
        $receipt = Get-Content -LiteralPath $receiptPath -Raw | ConvertFrom-Json
        $sandboxDllPath = Join-Path $repositoryRoot 'src\CodexRedactionGate\bin\Debug\net10.0-windows\CodexRedactionGate.dll'
        if ($permit.Id -cne $Job.Id -or $permit.Kind -cne 'test' -or $permit.ReceiptId -cne $receiptId -or
            $permit.SandboxDllPath -cne $sandboxDllPath -or $receipt.SandboxDllPath -cne $sandboxDllPath -or
            $permit.Sha256 -isnot [string] -or $permit.Sha256 -notmatch '^[A-F0-9]{64}$' -or
            $permit.Sha256 -cne $receipt.Sha256 -or $permit.Mvid -cne $receipt.Mvid -or
            $permit.WorkerScriptSha256 -cne $receipt.WorkerScriptSha256 -or
            -not (Test-ReceiptSourceHashMap -Expected $permit.SourceFileSha256 -Actual $receipt.SourceFileSha256)) {
            throw 'identity_receipt_mismatch'
        }

        [Guid] $receiptMvid = [Guid]::Empty
        if (-not [Guid]::TryParseExact($receipt.Mvid, 'D', [ref] $receiptMvid) -or
            $receipt.Mvid -cne $receiptMvid.ToString('D')) {
            throw 'identity_receipt_mismatch'
        }

        return [pscustomobject]@{
            DllPath = $sandboxDllPath
            Sha256 = $receipt.Sha256
            Mvid = $receipt.Mvid
            WorkerScriptSha256 = $receipt.WorkerScriptSha256
        }
    }
    catch {
        throw 'identity_receipt_mismatch'
    }
}

function Test-ReceiptBoundArtifact {
    param([Parameter(Mandatory)] $ReceiptAdmission)

    try {
        if (-not (Test-Path -LiteralPath $ReceiptAdmission.DllPath -PathType Leaf) -or
            (Get-FileHash -LiteralPath $ReceiptAdmission.DllPath -Algorithm SHA256).Hash -cne $ReceiptAdmission.Sha256 -or
            ([Reflection.Assembly]::LoadFile([IO.Path]::GetFullPath($ReceiptAdmission.DllPath)).ManifestModule.ModuleVersionId.ToString('D')) -cne $ReceiptAdmission.Mvid -or
            (Get-FileHash -LiteralPath $PSCommandPath -Algorithm SHA256).Hash -cne $ReceiptAdmission.WorkerScriptSha256) {
            throw 'identity_receipt_mismatch'
        }
    }
    catch {
        throw 'identity_receipt_mismatch'
    }
}

function Invoke-Job {
    param(
        [Parameter(Mandatory)] $Job,
        $ReceiptAdmission = $null
    )

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
        elseif ($null -ne $ReceiptAdmission) {
            @('test', $ReceiptAdmission.DllPath, '--nologo', '-p:UseAppHost=false')
        }
        else {
            # The worker itself is single-job. dotnet test on SDK 10 forwards
            # --disable-parallel to MSBuild, where it is not a valid switch.
            $testArguments = @('test', $projectPath, '--nologo', '-p:UseAppHost=false')
            $filter = Get-JobFilter -Job $Job
            if ($null -ne $filter) { $testArguments += @('--filter', $filter) }
            $testArguments
        }

        # Native tools legitimately write build and test diagnostics to stderr.
        # Keep strict error handling for the worker, but do not turn that output
        # into a PowerShell exception before dotnet's own exit code is captured.
        $previousErrorActionPreference = $ErrorActionPreference
        try {
            $ErrorActionPreference = 'Continue'
            $output = & $dotnetPath @arguments 2>&1 | Out-String
            $exitCode = $LASTEXITCODE
        }
        finally {
            $ErrorActionPreference = $previousErrorActionPreference
        }

        return [ordered]@{ ExitCode = $exitCode; Output = $output; Proxy = $proxy }
    }
    finally {
        foreach ($name in $previousEnvironment.Keys) {
            [Environment]::SetEnvironmentVariable($name, $previousEnvironment[$name], 'Process')
        }
    }
}

function Archive-InterruptedJobs {
    # A Windows Sandbox reset terminates this process, but the mapped folder
    # preserves its lease files. They are not active work after a fresh worker
    # starts. Keep them for audit instead of letting them permanently block a
    # retried job with the same id in inbox.
    foreach ($runningFile in Get-ChildItem -LiteralPath $runningPath -Filter '*.json' -File) {
        $archiveName = '{0}.{1}.interrupted.json' -f $runningFile.BaseName, ([DateTime]::UtcNow.ToString('yyyyMMddHHmmssfff'))
        Move-Item -LiteralPath $runningFile.FullName -Destination (Join-Path $interruptedPath $archiveName) -ErrorAction Stop
    }
}

foreach ($directory in @($inboxPath, $runningPath, $resultsPath, $interruptedPath, $interactiveLeasesPath, $permitsPath, $identityPath)) {
    [System.IO.Directory]::CreateDirectory($directory) | Out-Null
}
if (-not (Test-Path -LiteralPath $projectPath -PathType Leaf)) { throw 'fixed_project_missing' }
if (-not (Test-Path -LiteralPath $dotnetPath -PathType Leaf)) { throw 'sandbox_dotnet_missing' }
if (-not (Test-InteractiveSessionId)) { throw 'interactive_session_id_invalid' }

$interactiveLeaseClaimPath = Join-Path $interactiveLeasesPath "$InteractiveSessionId.json"
$script:interactiveTestLeaseAvailable = -not (Test-Path -LiteralPath $interactiveLeaseClaimPath -PathType Leaf)
if (-not $script:interactiveTestLeaseAvailable) {
    try {
        $claim = Get-Content -LiteralPath $interactiveLeaseClaimPath -Raw | ConvertFrom-Json
        if ($claim.SessionId -cne $InteractiveSessionId -or
            $claim.JobId -isnot [string] -or $claim.JobId -notmatch '^[a-z0-9][a-z0-9-]{2,80}$' -or
            $claim.ConsumedAtUtc -isnot [datetime]) {
            throw 'interactive_lease_claim_invalid'
        }

        $script:interactiveTestLeaseConsumedAtUtc = $claim.ConsumedAtUtc.ToUniversalTime().ToString('O')
        $script:interactiveTestLeaseConsumedJobId = $claim.JobId
    }
    catch {
        throw 'interactive_lease_claim_invalid'
    }
}

Initialize-InteractiveHostNative
$initialInteractiveHostReadiness = [InteractiveHostReadiness]::new(
    $InteractiveHostPresent,
    $InteractiveHostForegroundReady,
    $InteractiveHostAttemptedAtUtc,
    $InteractiveHostAttempt)
Archive-InterruptedJobs
Set-WorkerState -State 'ready' -InteractiveHostReadiness $initialInteractiveHostReadiness
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
        $receiptAdmission = $null
        $executionStarted = $false
        $interactiveHostReadiness = $initialInteractiveHostReadiness

        try {
            $job = Get-Content -Raw -LiteralPath $runningFile | ConvertFrom-Json
            Test-Job -Job $job
            if ($job.Id -ne $jobId) { throw 'job_filename_id_mismatch' }

            if ($job.Kind -eq 'test') {
                if (-not $script:interactiveTestLeaseAvailable) {
                    $status = 'blocked'
                    $failure = 'fresh_interactive_session_required'
                    $output = 'environment_status=fresh_interactive_session_required'
                }
                else {
                    $receiptAdmission = Get-ReceiptBoundTestAdmission -Job $job
                    if ($null -ne $receiptAdmission) {
                        $foregroundWaitSeconds = Get-ForegroundWaitSeconds -Job $job
                        $foregroundReady = Wait-InteractiveHostForeground -Readiness $interactiveHostReadiness -ForegroundWaitSeconds $foregroundWaitSeconds
                    }
                    else {
                        [void] (Request-InteractiveHostForeground -Readiness $interactiveHostReadiness -DeadlineUtc ([DateTime]::UtcNow.AddSeconds(2)))
                        $foregroundReady = $interactiveHostReadiness.ForegroundReady
                    }
                    if (-not $foregroundReady) {
                        $status = 'blocked'
                        if ($null -ne $receiptAdmission) {
                            $failure = 'interactive_foreground_timeout'
                            $output = 'environment_status=interactive_foreground_timeout executed=0 claim=0'
                        }
                        else {
                            $failure = 'interactive_foreground_unavailable'
                            $output = 'environment_status=interactive_foreground_unavailable executed=0 claim=0'
                        }
                    }
                    elseif ($null -ne $receiptAdmission) {
                        Test-ReceiptBoundArtifact -ReceiptAdmission $receiptAdmission
                        if (-not (Try-ConsumeInteractiveTestLease -JobId $jobId)) {
                            $status = 'blocked'
                            $failure = 'fresh_interactive_session_required'
                            $output = 'environment_status=fresh_interactive_session_required'
                        }
                        else {
                            Set-WorkerState -State 'running' -InteractiveHostReadiness $interactiveHostReadiness
                            $executionStarted = $true
                            $execution = Invoke-Job -Job $job -ReceiptAdmission $receiptAdmission
                            $exitCode = $execution.ExitCode
                            $proxy = $execution.Proxy
                            $output = $execution.Output
                            $status = if ($exitCode -eq 0) { 'completed' } else { 'failed' }
                            $failure = if ($exitCode -eq 0) { $null } else { 'dotnet_exit_nonzero' }
                        }
                    }
                    elseif (-not (Try-ConsumeInteractiveTestLease -JobId $jobId)) {
                        $status = 'blocked'
                        $failure = 'fresh_interactive_session_required'
                        $output = 'environment_status=fresh_interactive_session_required'
                    }
                    else {
                        Set-WorkerState -State 'running' -InteractiveHostReadiness $interactiveHostReadiness
                        $executionStarted = $true
                        $execution = Invoke-Job -Job $job -ReceiptAdmission $receiptAdmission
                        $exitCode = $execution.ExitCode
                        $proxy = $execution.Proxy
                        $output = $execution.Output
                        $status = if ($exitCode -eq 0) { 'completed' } else { 'failed' }
                        $failure = if ($exitCode -eq 0) { $null } else { 'dotnet_exit_nonzero' }
                    }
                }
            }
            else {
                $executionStarted = $true
                $execution = Invoke-Job -Job $job
                $exitCode = $execution.ExitCode
                $proxy = $execution.Proxy
                $output = $execution.Output
                $status = if ($exitCode -eq 0) { 'completed' } else { 'failed' }
                $failure = if ($exitCode -eq 0) { $null } else { 'dotnet_exit_nonzero' }
            }
        }
        catch {
            $message = $_.Exception.Message
            if ([string]::IsNullOrWhiteSpace($message)) {
                $message = '<no exception message>'
            }
            $position = $_.InvocationInfo.PositionMessage
            $output = "worker_failure_type=$($_.Exception.GetType().FullName)`r`nworker_failure_message=$message`r`nworker_failure_position=$position"
            $failure = 'worker_rejected_or_failed'
        }

        [System.IO.File]::WriteAllText($logPath, $output, $utf8NoBom)
        Write-JsonFile -Path $resultPath -Value ([ordered]@{
            Id = $jobId
            Kind = if ($null -eq $job) { $null } else { $job.Kind }
            Filter = if ($null -eq $job) { $null } else { Get-JobFilter -Job $job }
            ReceiptId = if ($null -eq $job) { $null } else { Get-JobReceiptId -Job $job }
            ForegroundWaitSeconds = if ($null -eq $job) { $null } else { Get-ForegroundWaitSeconds -Job $job }
            Project = $projectRelativePath
            Proxy = $proxy
            StartedAtUtc = $startedAtUtc
            FinishedAtUtc = [DateTime]::UtcNow.ToString('O')
            ExitCode = $exitCode
            Status = $status
            Failure = $failure
            Executed = $executionStarted
            InteractiveHostPresent = $interactiveHostReadiness.Present
            InteractiveHostForegroundReady = $interactiveHostReadiness.ForegroundReady
            InteractiveHostAttemptedAtUtc = $interactiveHostReadiness.AttemptedAtUtc
            InteractiveHostAttempt = $interactiveHostReadiness.Attempt
            InteractiveTestLeaseAvailable = $script:interactiveTestLeaseAvailable
            InteractiveTestLeaseConsumedAtUtc = $script:interactiveTestLeaseConsumedAtUtc
            InteractiveTestLeaseConsumedJobId = $script:interactiveTestLeaseConsumedJobId
            InteractiveSessionId = $InteractiveSessionId
        })
        Set-WorkerState -State 'ready' -InteractiveHostReadiness $interactiveHostReadiness
    }

    Start-Sleep -Seconds 1
    Set-WorkerState -State 'ready' -InteractiveHostReadiness $initialInteractiveHostReadiness
}
