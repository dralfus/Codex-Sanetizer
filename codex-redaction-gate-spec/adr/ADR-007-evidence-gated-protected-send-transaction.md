# ADR-007: Evidence-Gated Protected Send Transaction

## Status

Accepted

## Context

The resident prompt-protection path is currently coordinated across resident
snapshot ownership, `ProtectedSendPipeline`, `ResidentProtectedSendOperation`,
OS interaction orchestration, Windows UI Automation, confirmation UI, trace
publication, and release acceptance. Individual modules and tests can be
correct while the installed ChatGPT Desktop path fails between them.

Repeated bug-fix iterations showed that passing unit tests, self-test, product
smoke, and reference scenarios does not by itself prove the user's original
installed Windows symptom is fixed. The architecture also exposes too much
transaction ordering through the protected-Send host interface, reducing
locality and allowing callers to participate in state ownership.

## Decision

Protected Send will be owned by one deep `ProtectedSendTransaction` module.
Its production interface executes one resident-admitted attempt and returns
one immutable terminal outcome. Stage transitions, target revalidation,
continuity, sanitization, confirmation, edit re-sanitization, write,
verification, replay, the irreversible-side-effect lease, and trace
publication are implementation details behind that interface.

The resident runtime owns admission and supplies one immutable generation and
captured target. Windows hook, UI Automation, overlay, journal, tray,
reference-composer, and live ChatGPT implementations are adapters. They cannot
independently publish a successful or failed Send outcome.

A target-scoped composer-session seam owns UI Automation reacquisition,
COM/STA execution, focus restoration, write verification, and replay. An
`AutomationElement` is not a durable cross-STA identity. Stable opaque target
evidence is retained and the element is reacquired for each bounded operation.

Development uses an evidence ladder:

```text
proposed -> reproduced_red -> implemented -> locally_verified
         -> live_verified -> released
```

A fix claim requires the original reproduction to pass at its highest
applicable seam. Installed ChatGPT/Windows defects require matching installed
build and live-target evidence. Product smoke is supporting evidence, not a
substitute.

Migration follows expand-contract. The new transaction is added beside the
legacy orchestration only after a resident-owned live canary exposes the current
production path. Target access is deepened first, reference acceptance migrates
to production UI Automation, the production keyboard path migrates next, live
acceptance proves the new path, and only then is the legacy orchestration
removed. A production attempt always has exactly one side-effect owner.

`sent_safely` means local sanitized-text verification, successful configured
replay injection, and committed terminal publication. It does not mean that a
cloud service received or processed the message.

## Consequences

Positive:

- Locality: protected-Send bugs concentrate in one module.
- Leverage: one interface drives deterministic, reference, live, and release
  acceptance.
- Completion claims become evidence-backed and build-specific.
- Raw-free terminal outcomes identify the failed transition directly.
- Future bug fixes begin from a red-capable reproduction.

Negative:

- A controlled expand-contract migration is required.
- Live ChatGPT acceptance remains partly human-in-the-loop because focus and
  desktop interaction are external facts.
- Release candidates cannot be called fixed until matching live evidence is
  recorded when the defect depends on the live target.

## Guardrails

- Do not expose per-stage publication or lease management through the
  transaction's production interface.
- Do not permit a tray, adapter, smoke runner, or release script to reconstruct
  transaction success from local flags.
- Do not close a live Windows bug as fixed without a red-capable reproduction
  and matching installed-build proof.
- Do not store prompt text, mappings, paths, UI control names, or exception
  messages in evidence artifacts.
- Do not run legacy and new side-effect owners for one production attempt.
- Do not accept a reference-composer success that writes through direct fixture
  control assignment instead of the production UI Automation adapter.
- Do not delete the legacy path until reference, live, installer, and
  next-attempt readiness evidence pass through the new transaction.
