# 06 — Route reference acceptance through the production composer access path

**Ticket ID:** 355

**What to build:** Run reference-composer acceptance through `ProtectedSendTransaction` and `ProtectedComposerSession`, using production composer access behavior while keeping the reference input physically unable to target the real desktop client.

**Blocked by:** 05 — Introduce `ProtectedSendTransaction` beside legacy orchestration.

**Status:** done (2026-09-16)

**State owner:** The transaction owns each acceptance attempt; the reference fixture owns only its window and deterministic input stimuli.

**Fail-closed state:** A reference-adapter or production-access mismatch yields one raw-free blocked terminal outcome, no replay, and no passed release scenario. Fixture writes cannot substitute for production-access proof.

**Allowed transitions:** The transaction transitions through the same stages as ticket 05, with deterministic-session and production-access reference adapter modes.

**Deterministic proof:** Run every reference scenario twice and assert stable cleanup, multiline formatting, sanitized confirmation writes, cancel interception, target-change blocking, and terminal replay failures.

**Red-capable reproduction:** Disable production composer access while leaving the fixture writable. The old direct-fixture path would pass; the production-access path must fail closed.

**Highest required seam:** Reference composer through production access and the session contract.

**Evidence target:** `locally_verified` reference production-access proof bound to the executable build; it is not installed-ChatGPT proof.

**Current evidence:** Independent final review `SPEC PASS` / `CODE_QUALITY PASS`, local non-interactive receipt `1974/1974`, and current-build local interactive release matrix `1/1`. The historical Sandbox infrastructure blocker is not required for this approved local evidence channel.

**Recheck (2026-10-01):** Accepted implementation is present in master. Source review confirms transaction/session routing through production native composer access, observed access/evidence publication, the two required unavailable-access negative controls, and reference-only target exclusion. Current full non-interactive suite: 1997/1997, exit 0. Fresh interactive revalidation is **BLOCKED BY FIXTURE FOREGROUND READINESS**: both the matrix and unavailable-access test stop before Send admission; the existing startup diagnostic observes `set_foreground_request_succeeded=false`, foreground/focused target mismatch, and successful cleanup. Receipts: `artifacts/review-355-20261001/355.trx` and `artifacts/review-355-20261001/focus/355-focus.trx`. This does not revoke the historical acceptance or establish a transaction defect, and is not current interactive GREEN. Repeat the existing release matrix from an active foreground-capable Windows desktop; no acceptance checks were weakened.

- [x] Direct fixture writes are removed from acceptance evidence.
- [x] The production access adapter is used through the session contract.
- [x] Physical exclusion from OpenAI Desktop targets is preserved.
- [x] Evidence is published as reference production-access proof, not installed-application proof.
- [x] The bounded follow-up is linked to ticket 07 for production keyboard migration.
