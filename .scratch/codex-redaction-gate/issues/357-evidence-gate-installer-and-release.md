# 08 — Evidence-gate installer and release claims

**Ticket ID:** 357

**What to build:** Make installer packaging, release smoke, status UI, and release documentation consume one evidence contract. A candidate may claim protected keyboard Send only when deterministic, reference production-access, and installed-resident evidence matches that exact build.

**Blocked by:** 07 — Migrate production keyboard Send to `ProtectedSendTransaction`.

**Status:** blocked

**State owner:** The immutable release evidence manifest owns candidate proof; installer and UI are projections.

**Fail-closed state:** Missing or mismatched commit, version, executable hash, installer identity, compatibility fingerprint, or binding leaves the claim `not_verified` and never reuses older proof.

**Allowed transitions:** `locally_verified -> live_verified -> released` only after all required artifacts match; rebuild or profile/app drift invalidates the affected higher-level evidence.

**Deterministic proof:** Manifest validation rejects stale and cross-build proof; installer smoke checks embedded identity and evidence fields; UI tests project state without inventing readiness.

**Red-capable reproduction:** A fixture with a passing older installer proof or missing build identity must be rejected before release gating is complete.

**Highest required seam:** Installer smoke plus matching installed resident canary evidence.

**Evidence target:** `released` only when deterministic, reference, and live artifacts all match the packaged candidate.

- [ ] Bind every protected-Send evidence artifact to exact build identity.
- [ ] Gate release claims and installer smoke on applicable evidence levels.
- [ ] Show evidence state and next action without exposing prompt data.
- [ ] Update release documentation to prohibit unsupported fixed or released claims.
