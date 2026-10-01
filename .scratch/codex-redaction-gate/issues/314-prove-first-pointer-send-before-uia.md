# 03 — Prove the first pointer Send before UI Automation classification

**Ticket ID:** 314

**What to build:** Decide the first pointer Send from resident, precomputed target evidence without waiting for UI Automation in the low-level callback, while allowing unrelated clicks through.

**Blocked by:** 297 — Make deferred and pointer Send decisions atomic and fail-closed; 309 — Trace protected pointer Send through the resident operation.

**Status:** done (2026-09-27, scoped: locally_verified deterministic proof; production activation of the resident branch is the ticket 356 migration contract)

**State owner:** A resident pointer-target evidence owner publishes the selected/unrelated verdict and verified Send-control identity; the callback performs only bounded lookup by normalized identity.

**Fail-closed state:** Missing or stale evidence suppresses a selected-client Send as `trace_unavailable`; unrelated clicks pass through. The callback never performs live window, process, or UI Automation lookup.

**Allowed transitions:** `evidence_ready -> selected_send_suppressed -> protected_operation`, or `evidence_missing -> trace_unavailable`; unrelated input remains pass-through.

**Deterministic proof:** A target-evidence fixture covers focus changes, child-to-root transitions, slow UI Automation, unrelated clicks, and runtime replacement without timers, cloud access, or raw prompts.

**Red-capable reproduction:** Reproduce the first click before target evidence is cached. The old global click classification must not suppress navigation or other non-Send controls.

**Highest required seam:** Resident pointer admission and the actual protected pointer operation.

**Evidence target:** `locally_verified` pointer acceptance proving the actual Send boundary while `Ctrl+C`, navigation, skill controls, and unrelated clicks remain unaffected.

- [x] The first pointer Send is decided from resident evidence before the callback returns.
- [x] Slow or unavailable UI Automation cannot pass a selected-client Send or consume an unrelated click.
- [x] Tests prove evidence generation and normalized target identity reach the resident pointer operation.

**Current evidence (2026-09-28):** Branch `feat/t314-pointer-evidence` (commits ce5dc97..1e55547 on master 2ba224a). Independent review `SPEC: SCOPED_PASS` / `CODE_QUALITY: PASS`; independent verification `EXECUTABLE_VERIFICATION: PASS / ACCEPTED`; two operator-review rounds fixed in 5cedbaa and 1e55547. Final design: the low-level callback decides from resident evidence only (bounded store lookup plus the immutable snapshot — live `SendControlDiscovery` is never consulted inside classification); windows without evidence keep the legacy live-discovery classification until ticket 356 attaches out-of-band publication in production; only owner-verified identities are published, and unverified outcomes (`SurfaceUnverified`, `TraceUnavailable`) are never upgraded to Send evidence; an unidentified click is never classified or suppressed (SPEC pass-through for unknown input, including the no-adapter case). Tests: the FIRST callback gesture is decided from reference-composer-published evidence with zero live UIA calls (`VerifiedSendEvidenceFromReferenceComposerDrivesFirstCallbackSend`), and the resident verdict drives the actual protected pointer operation through the registered hook callback. The store is not snapshot state; the generation-check rationale is documented on `ProtectionSnapshot` (SPEC one-snapshot rule). Focused `PointerTargetEvidenceTests` 13/13, full non-interactive suite 2351/2351 exit 0 (trx 20260928061043106), build warning-free. Ticket 356 items: production out-of-band publication, focus-change/runtime-replacement invalidation, TTL, and child→root normalization at lookup. Live installed-desktop canary: NOT_RUN (out of this ticket's evidence target).
