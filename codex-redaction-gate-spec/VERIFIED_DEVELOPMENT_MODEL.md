# Verified Protected Send Architecture and Development Model

## Problem Statement

Code Sanitizer can pass unit, product-smoke, reference-composer, and installer
checks while the installed ChatGPT Desktop path still fails between
confirmation, composer write, verification, and Send replay. The current code
contains the right safety mechanisms, but their correctness is distributed
across resident state, protected-Send orchestration, UI Automation adapters,
confirmation UI, trace publication, and release scripts. A locally green
change can therefore be reported as fixed before the user's original Windows
scenario has been proven.

This is a product-architecture and development-model problem. Future bug fixes
must converge on a measured failing transition, and the application must make
that transition testable through one production interface.

## Solution

Code Sanitizer will deepen the protected-Send path into one
`ProtectedSendTransaction` module. Its production interface accepts one
resident-admitted, generation-bound Send attempt and returns one immutable,
raw-free terminal outcome. The module owns stage ordering, target
revalidation, sanitization, confirmation, edit re-sanitization, composer
write, write verification, replay, the irreversible-side-effect lease, and
terminal trace publication. Windows hook, profile, UI Automation, overlay,
journal, tray, reference-composer, and live ChatGPT implementations remain
adapters around this seam and cannot publish independent Send outcomes.

Development and release work will use an evidence ladder. A change is never
called fixed merely because its code and isolated tests pass. The exact
original symptom must first have a red-capable reproduction, then pass the
highest available production seam, then pass the installed pinned-target
scenario when the defect depends on ChatGPT Desktop or Windows behavior.

The normative evidence states are:

```text
proposed
  -> reproduced_red
  -> implemented
  -> locally_verified
  -> live_verified
  -> released
```

`fixed` is a user-facing claim allowed only when the original reproduction is
green against the installed candidate. For defects that do not depend on a
live desktop target, `locally_verified` may satisfy the highest applicable
seam, but the ticket must explain why no live seam applies.

## User Stories

1. As a ChatGPT Desktop user, I want one protected Send attempt to have one owner, so that confirmation, write, and replay cannot disagree about its result.
2. As a ChatGPT Desktop user, I want an approved sensitive prompt to be reported as sent only after the sanitized text was written and verified.
3. As a ChatGPT Desktop user, I want every uncertain or incomplete attempt to keep the original Send blocked.
4. As a ChatGPT Desktop user, I want cancellation and failure to leave the next Send eligible for a fresh protected attempt.
5. As a tester, I want one guided acceptance action to exercise the same resident path as normal keyboard Send.
6. As a tester, I want the acceptance UI to show the current stage, terminal result, installed build, and next action.
7. As a tester, I want a failed acceptance to identify the exact raw-free transition that failed.
8. As a maintainer, I want a failing protected-Send trace to be replayable with deterministic adapters, so that future fixes do not begin from speculation.
9. As a maintainer, I want the transaction interface to be the highest test seam, so that tests exercise orchestration rather than private helpers.
10. As a maintainer, I want adapters to expose capabilities and results without deciding transaction state.
11. As a maintainer, I want each bug ticket to name its state owner, fail-closed result, legal transitions, reproduction command, and required evidence level.
12. As a maintainer, I want a ticket to remain in diagnosis while no red-capable reproduction exists.
13. As a maintainer, I want implementation, local verification, live verification, and release to be distinct recorded states.
14. As a maintainer, I want one behavior change per candidate build, so that a failed live check has a small hypothesis set.
15. As a maintainer, I want temporary diagnostics to be tagged and removed after the cause is proven.
16. As a release owner, I want installer identity, source commit, executable hashes, target compatibility fingerprint, and acceptance evidence to match.
17. As a release owner, I want release scripts to reject stale or mismatched acceptance evidence.
18. As a release owner, I want component tests and product smoke to remain useful without being misrepresented as live ChatGPT proof.
19. As a security reviewer, I want all diagnostic and acceptance artifacts to remain raw-free.
20. As a security reviewer, I want exactly one terminal outcome after any irreversible write or replay side effect.
21. As a developer extending file protection later, I want prompt protection to expose a stable transaction pattern that can be reused without coupling file ingress to tray state.
22. As a product owner, I want roadmap progress to reflect evidence, not only merged code, so that completion claims remain trustworthy.

## Implementation Decisions

- `ProtectedSendTransaction` is a deep module with one production execution
  interface. Dependencies are supplied when the module is constructed; a
  caller does not publish individual stages or manage its lease.
- The resident runtime remains the admission owner. It captures one immutable
  resident generation, selected profile evidence, and transient target before
  scheduling the transaction.
- The transaction owns one attempt identifier, state machine, target identity,
  continuity decision, side-effect lease, raw-free trace, and terminal outcome.
- A target-scoped `ProtectedComposerSession` internal seam owns UI Automation
  element reacquisition, COM/STA execution, focus restoration, exact write
  verification, and replay. It does not retain an `AutomationElement` as a
  durable cross-STA identity.
- A terminal outcome contains a typed status, whether text was applied, whether
  Send was submitted, the raw-free trace, a safe failure reason, and a safe
  next action. Callers do not reconstruct outcome from independent booleans.
- The original keyboard event is suppressed before deferred work. Only the
  transaction may authorize and perform write or replay through injected
  adapters.
- The irreversible-side-effect lease spans the first composer mutation through
  verified terminal publication. Cancellation or reload before admission
  blocks; cancellation after the linearization point observes the committed
  terminal outcome.
- The transaction state machine rejects missing, duplicate, stale, or
  out-of-order transitions and produces one fail-closed terminal outcome.
- The tray is a projection adapter. It renders the resident-published outcome
  and dispatches user intent; it never marks a transaction protected or fixed.
- The operation journal persists only typed safe tokens, durations, opaque
  fingerprints, build identity, evidence identifiers, and terminal outcomes.
- A raw-free trace replay adapter converts a captured failed transition
  sequence into a deterministic transaction fixture. It never contains prompt
  text or local identifiers.
- The reference composer and live ChatGPT acceptance use the same resident
  captured-gesture entry and transaction interface as production. A direct
  helper path is not acceptable release evidence.
- A successful reference-composer write must use the production
  `NativeVerifiedComposerTextAccess` path. Direct assignment to the fixture's
  text control may be used only by setup code, never as protected-Send proof.
- Live acceptance uses a generated harmless marker and a temporary local rule.
  It proves interception, overlay approval, sanitized composer write,
  verification, replay, terminal publication, and next-attempt readiness.
- Evidence is bound to source commit, build version, executable hashes,
  compatibility fingerprint, Send binding, scenario identifier, and terminal
  trace. Evidence from another build or target is stale.
- Release claims are typed: `implemented`, `locally_verified`,
  `live_verified`, and `released`. Documentation and UI must not collapse these
  into one generic completed state.
- Existing protected-Send code is migrated expand-contract: add the transaction
  beside the legacy pipeline only after a resident-owned live canary can expose
  the current production failure. Deepen target access into a composer session,
  migrate deterministic and reference acceptance, migrate the production
  keyboard path, prove live acceptance again, then remove the legacy
  orchestration. There is never more than one side-effect owner for a
  production attempt.
- `sent_safely` means that sanitized text was locally written and verified,
  the configured replay was successfully injected, and the terminal outcome
  was committed. It does not claim that OpenAI received or processed the
  message. `cloud_received` is not an observable product state.
- Mouse Send and project-file ingress remain separate capabilities and cannot
  be used as evidence for the keyboard protected-Send transaction.

## Testing Decisions

- The highest deterministic seam is the `ProtectedSendTransaction` production
  interface with deterministic adapters for target, reader, sanitizer,
  confirmation, writer, verifier, replay, clock, and trace sink.
- Transaction contract tests cover safe prompt, sensitive prompt, edited
  prompt, cancel, target change, continuity loss, write failure, verification
  mismatch, replay unavailable, partial replay, cancellation/reload races,
  repeated Send, and exactly-one terminal publication.
- The local reference-composer acceptance drives the real resident callback,
  Windows hook, overlay, UI Automation write, verification, and replay. It is
  not replaced by shallow adapter tests.
- The resident-owned live canary is built before the transaction migration and
  remains the red/green production-path signal throughout expand-contract.
- The guided live ChatGPT acceptance validates the pinned installed target and
  configured keyboard binding with harmless generated content. It records no
  cloud response content.
- Installer acceptance verifies that the running process and evidence belong
  to the same candidate build before a live result can be accepted.
- Every future bug fix starts with one already-run red-capable command or a
  structured human-in-the-loop scenario. The reproduction must assert the
  reported symptom, not a neighboring failure.
- If no correct test seam can reproduce the bug, the ticket remains diagnostic
  and records the missing seam as architecture work before implementation.
- Tests use deterministic barriers instead of sleeps for resident races. Live
  desktop timing is bounded and reported as evidence, not used in unit tests.
- Product smoke proves packaged capabilities and invariant presence. It does
  not prove live ChatGPT behavior.
- A fix is complete only when the original reproduction turns green, focused
  regression tests pass, applicable broader gates pass, and temporary probes
  are removed.

## Out of Scope

- Mouse Send interception.
- Project-file and attachment ingress protection.
- Supporting unpinned third-party AI desktop applications.
- Using cloud response content as acceptance evidence.
- Removing fail-closed behavior to make acceptance easier.
- Rewriting the sanitizer, vault, policy, or restoration modules.
- A broad rewrite of the resident runtime before the expand-contract migration
  proves the new transaction interface.

## Further Notes

The goal is convergence, not maximum test count. A small test that fails on the
user's exact symptom has more release value than many isolated green tests.
The evidence ladder applies to all future Windows integration bugs, not only
the current confirmation/write defect. New features may define a different
highest seam, but they must use the same claim discipline and raw-free evidence
contract.
