# 07 — Migrate production keyboard Send to `ProtectedSendTransaction`

**Ticket ID:** 356

**What to build:** Route the supported desktop keyboard Send from resident admission into `ProtectedSendTransaction`. The hook captures, classifies, and suppresses only; the transaction owns write, replay, and terminal success.

**Blocked by:** 06 — Route reference acceptance through the production composer access path.

**Status:** ready-for-agent

**State owner:** Resident runtime owns immutable admission, the transaction owns every admitted attempt, and the session owns target-scoped mechanics.

**Fail-closed state:** An unavailable transaction or session, stale generation, uncertain target, write mismatch, or replay failure keeps the original Send suppressed and publishes one actionable raw-free outcome.

**Allowed transitions:** `captured selected Send -> suppressed -> admitted -> transaction terminal`; unrelated input passes through and cannot create a transaction.

**Deterministic proof:** Existing callback, reload, repeated-send, cancel/edit, target-change, formatting, and terminal-publication matrices pass through the new interface.

**Red-capable reproduction:** Reuse the unchanged failed installed resident canary and assertion from ticket 352; do not replace it with a more convenient scenario.

**Highest required seam:** Installed active resident on the supported desktop keyboard path.

**Evidence target:** `live_verified` for the exact commit, executable, installer, profile fingerprint, generation, and Send binding.

- [ ] Replace production keyboard orchestration with the transaction call.
- [ ] Remove independent write, replay, and terminal decisions from migrated callers.
- [ ] Prove one side effect and one terminal result across cancellation and reload.
- [ ] Turn the original installed resident canary from red to green without changing its assertion.
