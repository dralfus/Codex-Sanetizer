# Ticket 355: agreed reconstruction requirements

## Scope and state ownership

This document records the agreed design for ticket 355 before implementation.
It supplements `tickets.md`; when a requirement conflicts, the ticket's
fail-closed and evidence requirements prevail.

`ProtectedSendTransaction` is the sole state owner for one admitted reference
composer attempt. The reference fixture owns only its window, verified target,
and deterministic input stimuli. It cannot become a fallback owner for capture,
write, replay, or terminal success.

The reference runtime invokes `ProtectedSendTransaction.Execute()` directly.
It does not use `OsInteractionOrchestrator` on the ticket-355 acceptance path.

## Required composition

Each transaction attempt creates a fresh `IProtectedComposerSession` through
the production-shaped composition:

`CapturedTargetSurfaceDiscovery -> NativeVerifiedComposerTextAccess ->
WindowsVerifiedComposerSurfaceAdapter -> ProtectedComposerSession`.

One transaction-facing session adapter translates this session's read,
revalidate, write-and-verify, and replay results to
`IProtectedSendTransactionSession`. Native-access failure details may be
published only as raw-free diagnostics; the transaction interface remains
small and reports its existing typed failure classes.

The implementation must not retain a shared mutable session factory, a
five-role reference adapter, a direct fixture `TextBox.Text` assignment, or a
fixture-owned direct replay path in acceptance evidence.

The fixture must receive the initial prompt through a reference-only,
deterministic keyboard input adapter. That adapter remains physically unable to
target Codex or ChatGPT.

## Fail-closed evidence

The only publishable evidence level is
`reference_production_access`. Missing, wrong, stronger, live, or
installed-application levels fail closed and cannot pass release acceptance.
The raw-free release report must bind that level to the executable build
identifier.

The mandatory RED case leaves the fixture TextBox writable while native
production access (or its session operation) is unavailable. It must produce
one raw-free blocked terminal outcome, no write, no replay, and no passed
release scenario.

All reference scenarios run twice in Windows Sandbox and prove cleanup,
multiline formatting, sanitized confirmation writes, cancellation,
target-change blocking, and terminal replay failures.

### Negative-control reporting

The unavailable-native-access case is an explicit negative control in both
release-matrix runs. It has one of three mutually exclusive raw-free scenario
statuses:

- `passed`: a normal production-access scenario completed and may be release
  proof;
- `expected_blocked`: the negative control produced its exact required blocked
  terminal outcome; it is a successful test observation, but never release
  proof; or
- `failed_closed`: an unexpected failure or a violated negative-control
  invariant.

`expected_blocked` carries `EvidenceLevel=unavailable` and
`ReleaseProofEligible=false`. It must report exactly one blocked terminal, no
write, no replay, and no publishable release scenario. The suite may succeed
only when every normal scenario is `passed` and both negative-control runs are
`expected_blocked`; evidence publication includes only proof-eligible normal
scenarios.

## Related edit-loop and delivery rules

The user-facing edit loop in `ProtectedSendTransaction` needs a bounded,
fail-closed design. Ticket 355 records this as an explicit linked requirement;
the implementation belongs in the separately tracked follow-up selected by the
Controller, not silently in production routing.

Agent repair-loop policy is distinct from that product behavior. Codex/OpenAI
delivery follows the numeric budgets and stop gates of `finish-ticket`.
Qwen Code follows the `QWEN_CONVERGENT` ledger policy only when its trusted
runtime capability preflight succeeds.

## Non-goals

- No claim of installed or live OpenAI Desktop evidence.
- No migration of the production keyboard Send path; that remains ticket 356.
- No change to unrelated resident, tray, or file-ingress subsystems.
