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

- [x] Direct fixture writes are removed from acceptance evidence.
- [x] The production access adapter is used through the session contract.
- [x] Physical exclusion from OpenAI Desktop targets is preserved.
- [x] Evidence is published as reference production-access proof, not installed-application proof.
- [x] The bounded follow-up is linked to ticket 07 for production keyboard migration.
