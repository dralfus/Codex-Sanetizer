# 05 — Introduce `ProtectedSendTransaction` beside legacy orchestration

**Ticket ID:** 354

**What to build:** Add a deep transaction interface for an admitted protected Send. It owns attempt identity, generation, raw prompt lifetime, sanitization, confirmation/edit/cancel, target revalidation, write verification, replay, side-effect linearization, raw-free trace ordering, and exactly one terminal publication; production remains on the legacy owner initially.

**Blocked by:** 353 — Establish the protected composer session contract.

**Status:** done

**State owner:** `ProtectedSendTransaction` owns the admitted attempt and terminal outcome; resident runtime owns admission and session/UI adapters return effects only.

**Fail-closed state:** Missing or duplicate stages, stale generation, target change, cancellation, write mismatch, replay uncertainty, trace failure, or exception yields one blocked/cancelled result with no independent adapter replay.

**Allowed transitions:** `admitted -> read -> sanitized -> safe_path | confirmation -> write -> verify -> replay -> sent_safely`, or one terminal blocked/cancelled result.

**Deterministic proof:** The matrix covers safe and sensitive prompts, edit, cancel, confirmation, target and generation changes, write mismatch, replay failures, cancellation races, repeated sends, and exactly one terminal result without timers, UI Automation, or cloud access.

**Red-capable reproduction:** Characterization tests show that legacy callers can observe or own stage ordering and that duplicate side-effect ownership is representable.

**Highest required seam:** Deterministic `ProtectedSendTransaction` matrix.

**Evidence target:** `locally_verified`; production remains on the legacy owner until the later migration ticket.

- [x] The compact request/result contract and explicit state machine exist.
- [x] Lease, trace, side-effect, and terminal publication ownership are inside the transaction.
- [x] The legacy production path remains active without dual side effects.
- [x] Raw prompt data is not retained in terminal evidence.
