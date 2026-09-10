# Ticket 355 checkpoint: blocked for design

## Fixed point and preserved work

- Fixed point: `7e97d5f69c07c7f111f879ae38fcf55f9006e1c5`.
- Current partial diff is intentionally preserved; do not reset or treat it as
  accepted.
- Changed implementation files: `ReferenceComposerAcceptance.cs`,
  `ReferenceComposerReleaseAcceptance.cs`, and
  `SanitizerNativeSubmitTests.cs`.

## Completed controller decisions

- The agreed target composition and scope are in
  `TICKET_355_RECONSTRUCTION.md`.
- The first implementation slice statically routes reference acceptance through
  `ProtectedSendTransaction` and a fresh captured-target production-shaped
  composer session. It removes the previous direct fixture write/replay and
  shared multi-role reference adapter.
- Build evidence reported by Implementer: `dotnet build` exit `0`, warnings
  `0`, errors `0`. This is not UI acceptance evidence.

## Stop gate

Status: `BLOCKED_FOR_DESIGN`.

The same root cause remained after one scoped repair and fresh re-review:
the required unavailable-native-access RED case is not part of the actual
release-acceptance execution/report, and `composer_access`/evidence level are
derived from requested mode rather than an observed transaction/session result.

Before any next implementation round, approve a design that defines:

1. how expected blocked scenarios are represented in the two-run release
   matrix and CLI/raw-free report without allowing them to make release proof
   pass;
2. one observed, raw-free access/evidence result produced by the transaction
   environment and consumed by release projection; and
3. the required Windows Sandbox worker recovery and the exact focused
   compatibility command.

## Approved clarification and focused-loop authorization (2026-09-08)

The requirements owner approved the following in-scope clarification:

1. `production_access_unavailable` is a mandatory negative control in both
   release-matrix runs.
2. Its only successful observation is `expected_blocked`: one raw-free blocked
   terminal, no write, and no replay.
3. `expected_blocked` proves the negative control only. It is never release
   proof and cannot count as a passed release scenario.
4. `composer_access` and `evidence_level` must project an observed
   transaction/session outcome, not the requested access mode.
5. Release acceptance requires every normal release scenario to pass and both
   negative controls to be `expected_blocked`; neither negative control can
   hide missing release proof.

Controller authorization is limited to one focused TDD loop: record a
schema-valid Sandbox `TEST_PERMIT`, prove worker freshness with one safe
existing targeted test, add one regression test and preserve its RED result,
then dispatch one minimal-scope Implementer and one independent Reviewer. Do
not run Qwen, a Verifier, or a full suite before a complete static PASS and a
closed acceptance ledger.

## Focused RED evidence (2026-09-08)

- The dedicated Windows Sandbox was restarted and published a fresh worker
  marker with `StartedAtUtc=2026-09-08T16:03:04.4801997Z`.
- `TEST_PERMIT` `t355-worker-freshness-001` ran the existing reference-access
  test. Its result JSON proves worker execution; its test assertion failed on
  the current partial diff because observed evidence was `Unavailable` rather
  than `ReferenceProductionAccess`. This is ticket evidence, not acceptance.
- `TEST_PERMIT` `t355-negative-control-matrix-red-001` ran the new focused
  regression test
  `ReferenceComposerReleaseAcceptance_MatrixRequiresExpectedBlockedUnavailableProductionAccessInBothRuns`.
  It exited `1` with the expected RED: the assertion required two negative
  controls, while the current matrix contained `0`.
- After independent static review returned `SPEC: PASS` and `CODE_QUALITY:
  PASS`, `TEST_PERMIT` `t355-negative-control-matrix-green-001` ran the same
  targeted regression against the scoped implementation. It completed with
  exit `0`: `1 passed, 0 failed, 0 skipped` in 21 seconds. This is focused
  GREEN evidence only, not a full-suite or live-Desktop claim.
- Sandbox evidence is stored under the dedicated worktree's ignored
  `.sandbox-jobs/permits` and `.sandbox-jobs/results` directories. No full
  suite or Qwen execution occurred in this loop.

## Independent review and verification (2026-09-08)

- Fresh independent Reviewer: `SPEC: PASS`; `CODE_QUALITY: PASS`. The static
  ledger marked the two-run controls, exact blocked semantics, non-proof
  eligibility, observed access/evidence seam, and normal-proof gating `ready`.
- Independent Verifier received the controller-submitted focused result
  `t355-release-matrix-targeted-001`. The one two-run release-matrix test
  executed in fresh Sandbox and exited `1` (`0 passed, 1 failed`):
  `ReferenceComposerReleaseAcceptance_RunsFullMatrixTwice` observed
  `report.Passed == false`.
- Full suite and live/Desktop evidence were not run. No Qwen role was used.

### FAILURE_SUMMARY

PRIMARY_FAILURE: `UNKNOWN` — the result artifact contains only the failing
top-level `report.Passed` assertion and no raw-free per-scenario projection;
it cannot establish whether normal evidence, an unavailable-access control,
cleanup, or raw-free validation caused the result.

CASCADE_FAILURES: `UNKNOWN`.

IN_SCOPE: `unknown` until a focused diagnostic test exposes the first failed
matrix scenario and its raw-free projection.

NEXT_LOOP: add one test-only focused diagnostic assertion that reports/isolates
the first failing `ReferenceComposerReleaseScenarioResult` using raw-free
fields, run it through one new `TEST_PERMIT` in the fresh Sandbox, then decide
whether a scoped repair is authorized. Do not run a new Implementer, Verifier,
or full suite before that RED-capable loop exists.

### TOKEN_USAGE

Status: `REJECTED`.

Source: `NOT_AVAILABLE`.

Implementation: `NOT_AVAILABLE`.

Acceptance/control: `NOT_AVAILABLE`.

Ticket total: `NOT_AVAILABLE`.

Coverage: `NOT_AVAILABLE`.

Missing values: provider input, cached input, output, and reasoning counters
for Controller, Implementer, Reviewer, and Verifier.

## D019 diagnostic record (2026-09-08)

Purpose: `diagnostic`. Ticket status remains `REJECTED`; this record neither
changes the acceptance ledger nor authorizes repair. The existing Implementer
made one test-only patch to pass the already-existing raw-free
`RenderRawFree(report)` projection as the message for the unchanged two-run
matrix `report.Passed` assertion. One ordinary targeted `TEST_PERMIT` will run
that test once. No additional role, repair, review, verification, full suite,
live/Desktop, or Qwen action is authorized before its result is recorded.

### D019 result

`TEST_PERMIT` `t355-d019-failure-projection-001` ran exactly once in fresh
Sandbox with the existing two-run matrix filter. It exited `1` before test
execution because the test-only diagnostic patch attempted to pass a
`NativeSubmitProductSmokeReport` to
`ReferenceComposerReleaseAcceptanceRunner.RenderRawFree`, which accepts a
`ReferenceComposerReleaseAcceptanceReport` (`CS1503`). No raw-free
`FAILURE_PROJECTION` was emitted.

Status: `BLOCKED: DIAGNOSTIC_EVIDENCE_INCOMPLETE`. Ticket status remains
`REJECTED`; no repair, additional diagnostic run, review, verification, full
suite, live/Desktop, or Qwen action was taken.

## Unavailable external evidence

Sandbox job `t355-red-transaction-001` is still unconsumed in
`.sandbox-jobs/inbox`; `worker.json` has not updated since 2026-09-02. No
targeted UI, release matrix, or full-suite Sandbox evidence exists for the
current diff.

## Rescue corrective diagnostic record (2026-09-09)

The one authorized correction moved the existing raw-free assertion message
from the unrelated `NativeSubmitProductSmokeReport` assertion to the
`ReferenceComposerReleaseAcceptanceReport` created by
`ReferenceComposerReleaseAcceptance_RunsFullMatrixTwice`. The affected project
then built locally with exit code `0`, warnings `0`, and errors `0`.

Schema-valid targeted `TEST_PERMIT`
`t355-rescue-failure-projection-001` ran exactly one Sandbox job with filter
`FullyQualifiedName~NativeSubmitBindingScopeTests.ReferenceComposerReleaseAcceptance_RunsFullMatrixTwice`.
The job reached test execution and exited `1` (`0 passed, 1 failed`) after
emitting the raw-free release projection.

`FAILURE_PROJECTION: scenario_id=run1.safe_prompt; status=failed_closed;
terminal_status=failed_closed; raw_free=true; cleanup=true;
evidence_level=reference_production_access;
build_id=57f728110b194536a2066cd596cc6b1b`

`PRIMARY_FAILURE: UNKNOWN`. The projection localizes the first failure to the
normal `run1.safe_prompt` production-access scenario, but its generic
`failed_closed` terminal does not expose the transaction failure reason or the
exact trace stage sequence. It therefore cannot distinguish the primary
write, trace-publication, continuation, or replay fail-closed branch. Under the
rescue authorization, a production patch would be speculative.

Status: `BLOCKED: PRIMARY_CAUSE_NOT_LOCALIZED`. No production file was changed,
no local focused test was run, and no second Sandbox/GREEN job was created.

## Final test-only safe-prompt diagnostic (2026-09-09)

The separately authorized test-only diagnostic reproduces the exact
`run1.safe_prompt` inputs directly through `ReferenceComposerAcceptanceRunner.Run`.
Its assertion projection is restricted to predicate booleans, `sent_count`,
allow-listed evidence/access tokens, and allow-listed trace stage/result-code
tokens; unknown tokens project as `unavailable`.

The affected project built locally with exit code `0`, warnings `0`, and errors
`0`. Schema-valid targeted `TEST_PERMIT`
`t355-final-safe-prompt-diagnostic-001` then ran exactly one Sandbox job with
filter
`FullyQualifiedName~NativeSubmitBindingScopeTests.ReferenceComposerAcceptance_Run1SafePromptProjectsRawFreePredicateDetails`.
The job exited `1` (`0 passed, 1 failed`) after reaching the focused test.

The first false predicate condition, in assertion order, is
`Submitted=false`. The complete raw-free diagnostic projection is:

```text
hook_started: true
original_input_suppressed: true
submitted: false
cleanup: true
trace_complete: false
sent_count_one: false
sent_count: 0
evidence_level: unavailable
composer_access: unavailable
trace: send_detected=checking_prompt,target_matched=target_verified,terminal_blocked=failed_closed
```

Status remains `BLOCKED`. This was a diagnostic result only: no production
patch, matrix rerun, acceptance attempt, or additional Sandbox job followed.

## Final paired acceptance-seam diagnostic (2026-09-09)

Only the existing paired diagnostic test was changed. It retained the
matrix/direct composer-access and evidence-level equality assertions and added
one aggregate acceptance assertion covering the required matrix, release, and
direct safe-send predicates. Every failure assertion uses the same bounded
raw-free projection; `TestContext.Progress` is not used.

The affected project built locally with exit code `0`, warnings `0`, and errors
`0`. Schema-valid targeted `TEST_PERMIT`
`t355-paired-acceptance-final-001` ran exactly one Sandbox job with filter
`FullyQualifiedName~NativeSubmitBindingScopeTests.ReferenceComposerAcceptance_PairedSameProcessComparesMatrixAndDirectEvidence`.
The job exited `1` (`0 passed, 1 failed`).

Executed Sandbox binary:

- path: `C:\SandboxWorkspaces\CodexRedactionGate-355\src\CodexRedactionGate\bin\Debug\net10.0-windows\CodexRedactionGate.dll`
- MVID: `a2ada0a8de6546b09ebc1645644ca74b`
- SHA-256: `D987139F9CA44A9139789C54E97107E01D9BD81B994F238D592F5917B4196983`

Raw-free projection:

```text
matrix_status: failed_closed
matrix_terminal_status: failed_closed
matrix_composer_access: unavailable
matrix_evidence_level: unavailable
direct_hook_started: true
direct_original_input_suppressed: true
direct_submitted: false
direct_cleanup: true
direct_trace_complete: false
direct_sent_count: 0
direct_trace: send_detected,target_matched,terminal_blocked
```

Verdict: `PRIMARY_FAILURE_LOCALIZED`. The first false aggregate condition is
the required matrix composer access (`unavailable` instead of
`native_verified_composer_text_access`). In the same process, the direct run
also fails before submission: `Submitted=false`, `trace_complete=false`, and
`sent_count=0`, with the safe stage sequence ending in `terminal_blocked`
immediately after `target_matched`. No production patch or further job was run.
