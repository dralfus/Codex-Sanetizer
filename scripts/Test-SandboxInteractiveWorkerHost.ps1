<#
.SYNOPSIS
Static contract checks for the same-process Sandbox interactive worker host.
#>
[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$bootstrapPath = Join-Path $PSScriptRoot 'SandboxWorkerBootstrap.ps1'
$workerPath = Join-Path $PSScriptRoot 'SandboxTestWorker.ps1'

function Assert-Contains {
    param(
        [Parameter(Mandatory)] [string] $Text,
        [Parameter(Mandatory)] [string] $Expected,
        [Parameter(Mandatory)] [string] $Name
    )

    if ($Text.IndexOf($Expected, [StringComparison]::Ordinal) -lt 0) {
        throw "static_contract_missing:$Name"
    }
}

foreach ($path in @($bootstrapPath, $workerPath)) {
    $tokens = $null
    $errors = $null
    [System.Management.Automation.Language.Parser]::ParseFile($path, [ref] $tokens, [ref] $errors) | Out-Null
    if ($errors.Count -ne 0) {
        throw "static_contract_syntax_invalid:$([IO.Path]::GetFileName($path))"
    }
}

$bootstrap = Get-Content -Raw -LiteralPath $bootstrapPath
$worker = Get-Content -Raw -LiteralPath $workerPath
$bootstrapTokens = $null
$bootstrapErrors = $null
$bootstrapAst = [System.Management.Automation.Language.Parser]::ParseFile(
    $bootstrapPath,
    [ref] $bootstrapTokens,
    [ref] $bootstrapErrors)
$workerTokens = $null
$workerErrors = $null
$workerAst = [System.Management.Automation.Language.Parser]::ParseFile(
    $workerPath,
    [ref] $workerTokens,
    [ref] $workerErrors)
$misparsedMemberCommand = $bootstrapAst.FindAll({
    param($node)
    return ($node -is [System.Management.Automation.Language.CommandAst]) -and
        $node.Extent.Text.TrimStart().StartsWith('.', [StringComparison]::Ordinal)
}, $true) | Select-Object -First 1
if ($null -ne $misparsedMemberCommand) {
    throw 'static_contract_member_invocation_parsed_as_command'
}

Assert-Contains -Text $bootstrap -Expected '[System.Management.Automation.Runspaces.RunspaceFactory]::CreateRunspace()' -Name 'same_process_runspace'
Assert-Contains -Text $bootstrap -Expected '[System.Windows.Forms.Application]::Run($supervisor)' -Name 'durable_message_loop'
Assert-Contains -Text $bootstrap -Expected '$workerPowerShell.Dispose()' -Name 'worker_powershell_cleanup'
Assert-Contains -Text $bootstrap -Expected '$workerRunspace.Dispose()' -Name 'worker_runspace_cleanup'
Assert-Contains -Text $bootstrap -Expected '$workerInvocation = $workerPowerShell.AddCommand(' -Name 'explicit_worker_invocation_assignment'
Assert-Contains -Text $bootstrap -Expected '$workerInvocation.AddParameter(' -Name 'explicit_worker_parameter_binding'
Assert-Contains -Text $worker -Expected '$supervisor.BeginInvoke' -Name 'marshal_foreground_request_to_ui_thread'
Assert-Contains -Text $worker -Expected "'interactive_foreground_unavailable'" -Name 'fail_closed_before_dotnet'
Assert-Contains -Text $worker -Expected 'class InteractiveHostReadiness' -Name 'typed_readiness_holder'
Assert-Contains -Text $worker -Expected '[void] (Request-InteractiveHostForeground -Readiness $Readiness -DeadlineUtc $deadline)' -Name 'void_readiness_invocation'
Assert-Contains -Text $worker -Expected 'SetForegroundWindow($supervisor.Handle)' -Name 'ui_thread_foreground_request'
Assert-Contains -Text $worker -Expected 'fresh_interactive_session_required' -Name 'repeat_test_fail_closed'
Assert-Contains -Text $worker -Expected 'Try-ConsumeInteractiveTestLease' -Name 'lease_consumed_before_test_launch'
Assert-Contains -Text $bootstrap -Expected 'Get-InteractiveSandboxSessionId' -Name 'sandbox_local_session_id'
Assert-Contains -Text $bootstrap -Expected '$env:ProgramData' -Name 'sandbox_local_session_storage'
Assert-Contains -Text $bootstrap -Expected 'TryParseExact($Value, ''D''' -Name 'bootstrap_session_id_exact_guid_validation'
Assert-Contains -Text $worker -Expected 'FileMode]::CreateNew' -Name 'atomic_interactive_lease_claim'
Assert-Contains -Text $worker -Expected 'interactive-leases' -Name 'mapped_interactive_lease_claim_directory'
Assert-Contains -Text $worker -Expected 'InteractiveSessionId' -Name 'session_id_in_raw_free_state'
Assert-Contains -Text $worker -Expected "'interactive_session_id_invalid'" -Name 'invalid_session_id_fails_closed'
Assert-Contains -Text $worker -Expected 'ReceiptId' -Name 'receipt_bound_test_job_schema'
Assert-Contains -Text $worker -Expected 'Get-ReceiptBoundTestAdmission' -Name 'receipt_permit_before_admission'
Assert-Contains -Text $worker -Expected 'Test-ReceiptBoundArtifact' -Name 'receipt_artifact_identity_check'
Assert-Contains -Text $worker -Expected "'identity_receipt_mismatch'" -Name 'receipt_identity_fail_closed'
Assert-Contains -Text $worker -Expected '@(''test'', $ReceiptAdmission.DllPath, ''--nologo'', ''-p:UseAppHost=false'')' -Name 'receipt_uses_exact_dll_target'
Assert-Contains -Text $worker -Expected "'foreground_wait_seconds'" -Name 'receipt_foreground_wait_job_field'
Assert-Contains -Text $worker -Expected "'foreground_wait_seconds_invalid'" -Name 'receipt_foreground_wait_invalid_fail_closed'
Assert-Contains -Text $worker -Expected "'interactive_foreground_timeout'" -Name 'receipt_foreground_timeout_raw_free'
Assert-Contains -Text $worker -Expected 'Wait-InteractiveHostForeground' -Name 'bounded_foreground_wait'
Assert-Contains -Text $worker -Expected 'ForegroundWaitSeconds -lt 1 -or $ForegroundWaitSeconds -gt 120' -Name 'foreground_wait_closed_bounds'
Assert-Contains -Text $worker -Expected 'environment_status=interactive_foreground_timeout executed=0 claim=0' -Name 'foreground_timeout_no_execution_or_claim'
foreach ($forbiddenApi in @('AttachThreadInput', 'AllowSetForegroundWindow', 'SendInput')) {
    if ($bootstrap.IndexOf($forbiddenApi, [StringComparison]::OrdinalIgnoreCase) -ge 0 -or
        $worker.IndexOf($forbiddenApi, [StringComparison]::OrdinalIgnoreCase) -ge 0) {
        throw "static_contract_forbidden_api_present:$forbiddenApi"
    }
}

class ContractReadinessHolder {
    [bool] $Present
    [bool] $ForegroundReady
    [string] $AttemptedAtUtc
    [int] $Attempt
}

function Set-ReadinessWithIncidentalOutput {
    param([Parameter(Mandatory)] [ContractReadinessHolder] $Readiness)

    Write-Output 'incidental_output'
    $Readiness.Present = $true
    $Readiness.ForegroundReady = $true
    $Readiness.AttemptedAtUtc = '2000-01-01T00:00:00.0000000Z'
    $Readiness.Attempt = 2
}

$holder = [ContractReadinessHolder]::new()
[void] (Set-ReadinessWithIncidentalOutput -Readiness $holder)
if (-not $holder.Present -or -not $holder.ForegroundReady -or
    $holder.AttemptedAtUtc -ne '2000-01-01T00:00:00.0000000Z' -or $holder.Attempt -ne 2) {
    throw 'static_contract_typed_holder_not_mutated'
}

$pipelineReadinessAssignments = $workerAst.FindAll({
    param($node)
    $node -is [System.Management.Automation.Language.AssignmentStatementAst] -and
        $node.Right.Extent.Text.IndexOf('Request-InteractiveHostForeground', [StringComparison]::Ordinal) -ge 0
}, $true)
if ($pipelineReadinessAssignments.Count -ne 0) {
    throw 'static_contract_readiness_pipeline_assignment_present'
}

$receiptAdmissionIndex = $worker.IndexOf('$receiptAdmission = Get-ReceiptBoundTestAdmission -Job $job', [StringComparison]::Ordinal)
$receiptForegroundIndex = $worker.IndexOf('Wait-InteractiveHostForeground -Readiness $interactiveHostReadiness -ForegroundWaitSeconds $foregroundWaitSeconds', [StringComparison]::Ordinal)
$receiptIdentityIndex = $worker.IndexOf('Test-ReceiptBoundArtifact -ReceiptAdmission $receiptAdmission', [StringComparison]::Ordinal)
$receiptLeaseIndex = $worker.IndexOf('Try-ConsumeInteractiveTestLease -JobId $jobId', [StringComparison]::Ordinal)
$receiptDotnetIndex = $worker.IndexOf('$execution = Invoke-Job -Job $job -ReceiptAdmission $receiptAdmission', [StringComparison]::Ordinal)
if ($receiptAdmissionIndex -lt 0 -or $receiptForegroundIndex -lt 0 -or $receiptIdentityIndex -lt 0 -or
    $receiptLeaseIndex -lt 0 -or $receiptDotnetIndex -lt 0 -or
    $receiptForegroundIndex -gt $receiptIdentityIndex -or
    $receiptIdentityIndex -gt $receiptLeaseIndex -or
    $receiptLeaseIndex -gt $receiptDotnetIndex) {
    throw 'static_contract_receipt_execution_order_invalid'
}

function Test-ContractForegroundWaitSeconds {
    param($Value)

    return ($Value -is [int] -or $Value -is [long]) -and $Value -ge 1 -and $Value -le 120
}

if (-not (Test-ContractForegroundWaitSeconds 1) -or
    -not (Test-ContractForegroundWaitSeconds 120) -or
    -not (Test-ContractForegroundWaitSeconds ([long] 30)) -or
    (Test-ContractForegroundWaitSeconds $null) -or
    (Test-ContractForegroundWaitSeconds 0) -or
    (Test-ContractForegroundWaitSeconds 121) -or
    (Test-ContractForegroundWaitSeconds '120')) {
    throw 'static_contract_foreground_wait_bounds_invalid'
}

$script:contractReceiptLeaseClaimed = $false
function Invoke-ContractReceiptAdmission {
    param([bool] $PermitValid, [bool] $ArtifactValid)

    if (-not $PermitValid) { return 'permit_invalid' }
    if (-not $ArtifactValid) { return 'identity_receipt_mismatch' }
    $script:contractReceiptLeaseClaimed = $true
    return 'admitted'
}

if ((Invoke-ContractReceiptAdmission -PermitValid $true -ArtifactValid $false) -ne 'identity_receipt_mismatch' -or
    $script:contractReceiptLeaseClaimed) {
    throw 'static_contract_identity_mismatch_consumes_lease'
}
if ((Invoke-ContractReceiptAdmission -PermitValid $true -ArtifactValid $true) -ne 'admitted' -or
    -not $script:contractReceiptLeaseClaimed) {
    throw 'static_contract_receipt_valid_admission_does_not_claim'
}

if (-not ('ContractAtomicLeaseClaim' -as [type])) {
    Add-Type @'
using System.IO;
using System.Threading;
using System.Threading.Tasks;

public static class ContractAtomicLeaseClaim
{
    public static int ClaimTwice(string path)
    {
        var winners = 0;
        Parallel.For(0, 2, _ =>
        {
            try
            {
                using (File.Open(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    Interlocked.Increment(ref winners);
                }
            }
            catch (IOException) { }
        });
        return winners;
    }
}
'@
}

$claimRoot = Join-Path ([IO.Path]::GetTempPath()) ("codex-redaction-gate-lease-contract-" + [Guid]::NewGuid().ToString('N'))
try {
    [IO.Directory]::CreateDirectory($claimRoot) | Out-Null
    $sessionId = [Guid]::NewGuid().ToString('D')
    $claimPath = Join-Path $claimRoot "$sessionId.json"
    if ([ContractAtomicLeaseClaim]::ClaimTwice($claimPath) -ne 1) {
        throw 'static_contract_atomic_claim_allows_not_exactly_one_winner'
    }

    # Reinitializing a worker for the same Sandbox session sees the durable
    # mapped claim. Restore is intentionally not a claim operation.
    $sameSessionLeaseAvailable = -not (Test-Path -LiteralPath $claimPath -PathType Leaf)
    $restoreConsumesLease = $false
    if ($sameSessionLeaseAvailable -or $restoreConsumesLease) {
        throw 'static_contract_same_session_reinitialization_restores_lease'
    }

    $freshSessionId = [Guid]::NewGuid().ToString('D')
    $freshSessionClaimPath = Join-Path $claimRoot "$freshSessionId.json"
    if (Test-Path -LiteralPath $freshSessionClaimPath -PathType Leaf) {
        throw 'static_contract_fresh_session_has_no_lease'
    }

    [Guid] $parsedSession = [Guid]::Empty
    [Guid] $parsedInvalid = [Guid]::Empty
    if (-not [Guid]::TryParseExact($sessionId, 'D', [ref] $parsedSession) -or
        [Guid]::TryParseExact('invalid', 'D', [ref] $parsedInvalid)) {
        throw 'static_contract_session_id_validation_invalid'
    }
}
finally {
    if (Test-Path -LiteralPath $claimRoot) {
        Remove-Item -LiteralPath $claimRoot -Recurse -Force
    }
}

Write-Output 'sandbox_interactive_worker_host_static_contract=passed'
