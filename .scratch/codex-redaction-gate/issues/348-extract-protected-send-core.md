# 04 — Выделить ядро protected Send из NativeSubmitInterception

**Ticket ID:** 348

**What to build:** Make one deep protected-Send operation own attempt lifecycle, target revalidation, sanitization, confirmation, local write, replay, and the raw-free terminal trace; adapters only provide evidence and effects.

**Blocked by:** 347 — Deepen the resident workflow interface; 323 — Make compatibility fingerprints opaque; 324 — Centralize the verified discovery fixture schema; 346 — Publish immutable resident admission evidence.

**Status:** ready-for-agent

**State owner:** The protected-Send operation owns the correlated attempt, target identity, stages, and terminal outcome. Input, profile, compatibility, persistence, and smoke adapters do not own submission decisions.

**Fail-closed state:** Missing stages, target change, foreground refusal, write failure, replay uncertainty, stale admission, or incomplete evidence suppresses the original Send and publishes one raw-free blocked outcome.

**Allowed transitions:** `send_detected -> target_matched -> composer_read -> sanitized -> overlay_decision -> text_written -> replayed -> sent_safely`, or one terminal `blocked(reason)`/cancelled result.

**Deterministic proof:** Reference and injected adapters cover safe and sensitive prompts, cancel, target changes, write and replay failures, repeated sends, unrelated input, raw-free traces, and no callback-time storage or cloud access.

**Red-capable reproduction:** Characterization tests expose stage ordering or duplicate terminal/side-effect ownership outside the proposed operation.

**Highest required seam:** The shared protected-Send operation through reference and injected Windows adapters.

**Evidence target:** Reclose only after deterministic, reference, live, and release evidence for the migrated chain are green.

- [x] Protected-Send stage ordering and terminal trace ownership move behind one deep operation interface.
- [x] Hook, UI Automation, profile, compatibility, and smoke code remain adapters with no independent submit or replay decisions.
- [x] Reference-composer and live compatibility evidence semantics are preserved.
- [x] The existing automated, self-test, product-smoke, and reference matrix evidence was retained.
- [x] One side-effect boundary spans write/replay through terminal publication and proves no duplicate-send ambiguity.
- [x] Adapter-stage normalization is centralized in the protected-Send operation.
- [ ] Close this ticket again only after the complete evidence-gated chain passes.
