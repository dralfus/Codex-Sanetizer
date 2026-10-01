# Maintenance review — 2026-10-01

Scope: recheck accepted ticket 355, review/correct ticket 314, consolidate branches into master. No new features.

## Ticket 355

Implementation is already in master (`d8644fe`). Transaction/session composition, production native access, observed evidence, negative controls and reference-only physical exclusion remain intact. No source defect identified in this recheck.

Fresh interactive attempts failed before the transaction because Windows rejected fixture foreground activation. The existing focus diagnostic observed SetForeground request false, focus request true, actual foreground/focused target false, and cleanup true after 30 seconds. This is an environment precondition failure; it cannot establish current interactive acceptance. Historical 2026-09-16 acceptance remains historical evidence.

Receipts: `artifacts/review-355-20261001/355.trx` (1 pass, 2 fail), `artifacts/review-355-20261001/focus/355-focus.trx` (1 fail, raw-free focus projection attached). The passing targeted case is the deterministic negative-control eligibility test.

To repeat interactive revalidation from an active Windows desktop:

```powershell
dotnet test src/CodexRedactionGate/CodexRedactionGate.csproj --nologo -p:UseAppHost=false --filter 'FullyQualifiedName~ReferenceComposerReleaseAcceptance_RunsFullMatrixTwice|FullyQualifiedName~ReferenceComposerAcceptance_UnavailableProductionAccessFailsClosedWithoutFixtureBypass' --results-directory artifacts/recheck-355-interactive --logger 'trx;LogFileName=355.trx'
```

## Standards

Original fixed-point review `2ba224a..1e55547`: 3 findings, worst P1.

- P1: root HWND-only evidence confused Send with other controls — corrected by exact preverified reference point and owner/button keys.
- P1: stale classification depended on the last tray result — corrected by target profile and immutable runtime membership.
- P2: resident misses called live discovery — corrected by bounded admission and persistent reference-window mode after invalidation. Legacy reference discovery stays outside that admission seam and publishes no resident evidence.

Independent final review: **PASS; 0 remaining findings**.

## Spec

Original fixed-point review: 3 findings, worst P1.

- P1: Send/non-Send evidence conflation — corrected and tested within one window.
- P1: stale Send bypass / unrelated click suppression — corrected and tested independently of last-profile history.
- P2: highest-seam proof was a synthetic Submitted return — replaced by actual captured-target orchestration, sanitization, write verification, one replay and complete terminal trace.

Independent final review: **SCOPED_PASS; 0 remaining findings**. Production pointer activation/publication, geometry freshness and automatic lifecycle invalidation remain ticket 356.

Summary: initial Standards 3 (worst P1), Spec 3 (worst P1); all corrected, final findings 0 per axis.

## Verification

- Five new regression cases reproduced the old defects: `artifacts/review-314-20261001/red/314-red.trx`.
- Focused pointer evidence: 23/23, exit 0, `artifacts/review-314-20261001/final/314-final.trx`.
- Full non-interactive suite: 1997/1997, failed 0, skipped 0, exit 0, `artifacts/non-interactive/20261001094739382/non-interactive.trx`.
- Tray build: exit 0, warnings 0, errors 0.
- Non-interactive contract / source whitespace checks: PASS.
- Test housekeeping: removed 365 duplicate inherited test executions and replaced the obsolete `tickets.md` root marker with project/packaging markers. Interactive ProductSmoke failed during the original focused baseline because that filter included inherited tests; it remains covered by the interactive category and was not hidden or weakened.

## Branch disposition

Before mutation, fetched/pruned origin and created/verified a complete local Git bundle: `artifacts/branch-cleanup-20261001/before-cleanup.bundle`. It preserves all original refs and commit history; keep it for recovery. It is ignored local archival evidence and is not published.

- `feat/t314-pointer-evidence` and its sandbox twin: identical `1e55547` tips; merged into master by `308adc2a6d1d28b6e2214ea8884014531d8c8eda`, followed by reviewed corrective work.
- Ticket 355 design/sandbox branches and proofloop branches: ancestors of master; no unique implementation to integrate.
- `codex/clean-publish`: the existing master history root `90401dc`; obsolete publication branch.
- `codex/sandbox-tests`: obsolete worker iterations; the final patch is cherry-equivalent to master and earlier iterations are superseded by the current worker/bootstrap. Full obsolete history retained in the bundle.
- `backup/blocked-history-master`: historical pre-clean-publish repository snapshot, with 72 commits absent from current master ancestry. Archived in full; intentionally not merged into the clean-publish history.

Remote removal is performed only after the corrective master is published, using an atomic deletion push with an expected-tip lease for each audited branch. Final branch inventory is checked against origin after deletion.
