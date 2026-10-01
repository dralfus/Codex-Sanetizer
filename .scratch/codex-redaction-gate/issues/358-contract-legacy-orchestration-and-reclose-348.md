# 09 — Contract legacy protected-Send orchestration and reclose ticket 348

**Ticket ID:** 358

**What to build:** After the migrated production path and all evidence levels are green, remove the legacy protected-Send owner, broad host callbacks, duplicate orchestration, and direct acceptance writes. Keep unrelated apply-only behavior behind an explicit boundary if it remains needed.

**Blocked by:** 351–357 — all evidence-gated transaction and release work. Ticket 348 cannot reclose before this contraction.

**Status:** blocked

**State owner:** `ProtectedSendTransaction` remains the only admitted-attempt owner; resident, session, sanitizer, overlay, and tray retain only their documented responsibilities.

**Fail-closed state:** If any caller still owns replay or terminal publication, or any required evidence is missing, contraction and reclosure stop.

**Allowed transitions:** `dual implementation with one active owner -> all callers migrated -> legacy unreachable -> legacy deleted -> 348 reclosed`; there is never a state with two active side-effect owners.

**Deterministic proof:** Static dependency checks and tests prove the broad host and duplicate state machine are gone; full suite, self-test, product smoke, reference production-access matrix, and installer-matched resident canary pass for one candidate.

**Red-capable reproduction:** Static checks fail while the broad host, direct fixture write, or duplicate protected-Send stage owner remains reachable.

**Highest required seam:** Static architecture gate plus the installed resident canary for the contracted candidate.

**Evidence target:** `released`, followed by reclosure of ticket 348 with linked deterministic, reference, live, and release proof.

- [ ] Delete legacy transaction ownership and broad host callbacks.
- [ ] Keep apply-only behavior separate from cloud-bound Send semantics.
- [ ] Run all evidence levels on one candidate and record the artifacts.
- [ ] Reclose ticket 348 with linked proof and preserve its earlier history.
