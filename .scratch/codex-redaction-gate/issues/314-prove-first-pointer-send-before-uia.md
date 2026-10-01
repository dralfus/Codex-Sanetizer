# 03 — Prove the first pointer Send before UI Automation classification

**Ticket ID:** 314

**What to build:** Decide the first pointer Send from resident, precomputed target evidence without waiting for UI Automation in the low-level callback, while allowing unrelated clicks through.

**Blocked by:** 297 — Make deferred and pointer Send decisions atomic and fail-closed; 309 — Trace protected pointer Send through the resident operation.

**Status:** done (2026-10-01, integrated into master, scoped: locally_verified deterministic proof; production activation of the resident branch is the ticket 356 migration contract)

**State owner:** A resident pointer-target evidence owner publishes the selected/unrelated verdict and verified Send-control identity; the callback performs only bounded lookup by normalized identity.

**Fail-closed state:** Stale evidence for a previously identified selected-client Send suppresses it as `trace_unavailable`; unrelated and unidentified points pass through. Resident admission never performs live window, process, or UI Automation lookup; global production pointer interception remains disabled until migration.

**Allowed transitions:** `evidence_ready -> selected_send_suppressed -> protected_operation`, or `evidence_missing -> trace_unavailable`; unrelated input remains pass-through.

**Deterministic proof:** A target-evidence fixture covers focus changes, child-to-root transitions, slow UI Automation, unrelated clicks, and runtime replacement without timers, cloud access, or raw prompts.

**Red-capable reproduction:** Reproduce the first click before target evidence is cached. The old global click classification must not suppress navigation or other non-Send controls.

**Highest required seam:** Resident pointer admission and the actual protected pointer operation.

**Evidence target:** `locally_verified` pointer acceptance proving the actual Send boundary while `Ctrl+C`, navigation, skill controls, and unrelated clicks remain unaffected.

- [x] The first pointer Send is decided from resident evidence before the callback returns.
- [x] Slow or unavailable UI Automation cannot pass a selected-client Send or consume an unrelated click.
- [x] Tests prove evidence generation and normalized target identity reach the resident pointer operation.

**Current evidence (2026-10-01):** Original branch range `2ba224a..1e55547` reviewed independently against Standards and Spec, integrated into master, and corrected. Initial findings: Standards 3, Spec 3; final review: Standards PASS, Spec SCOPED_PASS, no open findings. Evidence keys the verified captured HWND/PID/X/Y/button, preserves the normalized composer root for deferred execution, checks exact generation and target-profile membership without tray-history dependence, and resolves one immutable entry per resident decision. Resident misses/invalidation stay bounded; only explicit out-of-band reference discovery publishes. Unknown points and other owners pass through. The FIRST registered callback Send reaches the actual captured-target sanitize/confirm/write/verify/replay flow, with one submit and a complete generation-bound terminal trace. Focused 23/23; full non-interactive 1997/1997, exit 0; tray build warning-free. Production geometry/publication/TTL/lifecycle activation remains ticket 356; global native mouse interception stays disabled. See `../checkpoints/314-checkpoint-20260927.md` for exact receipts and scope.
