<#
.SYNOPSIS
Static contract for the local non-interactive NUnit suite.
#>
[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$testSourcePath = Join-Path $repositoryRoot 'src\CodexRedactionGate\SanitizerNativeSubmitTests.cs'
$productSmokeTestSourcePath = Join-Path $repositoryRoot 'src\CodexRedactionGate\Tests.cs'
$runnerPath = Join-Path $PSScriptRoot 'Invoke-NonInteractiveSuite.ps1'
$source = (Get-Content -LiteralPath $testSourcePath -Raw) + [Environment]::NewLine + (Get-Content -LiteralPath $productSmokeTestSourcePath -Raw)

$interactiveMethods = @(
    'ReferenceComposerAcceptance_PublishesReferenceProductionAccessEvidenceLevel',
    'ReferenceComposerAcceptance_UnavailableProductionAccessFailsClosedWithoutFixtureBypass',
    'ReferenceComposerReleaseAcceptance_UnavailableProductionAccessCannotPublishReleaseScenario',
    'ReferenceComposerAcceptance_ReleaseAndDirectPathsRetainProductionAccessInSameProcess',
    'ReferenceComposerAcceptance_Run1SafePromptProjectsRawFreePredicateDetails',
    'ReferenceComposerAcceptance_SubmitDeliveryStabilityThreeAttempts',
    'ReferenceComposerAcceptance_ReplayFocusProjectsRawFreeState',
    'ReferenceComposerPersistentFixtureHost_StartupProjectsRawFreeFocusReadiness',
    'ReferenceComposerPersistentFixtureHost_ReusesOneStaAndControlAcrossRealScenarioLeases',
    'ReferenceComposerPersistentFixture_ThreeMixedLeasesReacquireFocusWithoutHumanInput',
    'ReferenceComposerPersistentFixture_CancelProjectsRawFreeCompletionEvidence',
    'ReferenceComposerAcceptance_SafePromptReplaysThroughResidentHookPath',
    'ReferenceComposerAcceptance_Run1MultilineSensitivePromptProjectsRawFreePredicateDetails',
    'ReferenceComposerAcceptance_SensitivePromptUsesProductionOverlayAndNeverSendsRawText',
    'ReferenceComposerAcceptance_ClipboardRestoreFailureIsRawFreeAndSuppressesReplay',
    'ReferenceComposerAcceptance_ForegroundRefusalBlocksSuppressedSend',
    'ReferenceComposerAcceptance_TargetChangeBlocksBeforeSideEffect',
    'ReferenceComposerAcceptance_UiAutomationWriteFailureBlocksAfterApproval',
    'ReferenceComposerAcceptance_ReplayFailurePublishesDistinctTerminalStatus',
    'ReferenceComposerAcceptance_CancelAndRepeatedRunLeaveNoSendOrLeakedCapability',
    'ReferenceComposerReleaseAcceptance_RunsFullMatrixTwice',
    'ReferenceComposerReleaseAcceptance_MatrixRequiresExpectedBlockedUnavailableProductionAccessInBothRuns',
    'ReferenceComposerAcceptance_ReleaseSmokeRunsAllAcceptanceCases',
    'Main_ProductSmokePrintsRawFreeEndToEndStatus',
    'ProductSmokeRunner_CoversApplyOnlyProductPathWithRawFreeReport'
)

foreach ($method in $interactiveMethods) {
    $pattern = '\[Category\("interactive-fixture"\)\]\s*(?:\[[^\]]+\]\s*)*public void ' + [regex]::Escape($method) + '\('
    if (-not [regex]::IsMatch($source, $pattern)) {
        throw "non_interactive_contract_missing_category:$method"
    }
}

if (-not (Test-Path -LiteralPath $runnerPath -PathType Leaf)) {
    throw 'non_interactive_contract_runner_missing'
}

$runner = Get-Content -LiteralPath $runnerPath -Raw
foreach ($required in @('Category!=interactive-fixture', '--logger', 'trx;LogFileName=', 'non_interactive_trx_total_zero')) {
    if ($runner.IndexOf($required, [StringComparison]::Ordinal) -lt 0) {
        throw "non_interactive_contract_runner_missing:$required"
    }
}

Write-Output 'non_interactive_suite_static_contract=passed'
