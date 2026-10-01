# 10 — Preserve structural path suffixes during sensitive-term matching

**Ticket ID:** 362

**What to build:** When a sensitive term matches a host component inside a structured workspace or container path, replace only that host component and preserve the path syntax and suffix. A plain hostname remains independently configurable.

**Blocked by:** 352 — The protected keyboard acceptance gate must provide a raw-free installed failure artifact before matching behavior is extended.

**Status:** ready-for-agent

**State owner:** Sanitizer policy owns term classification and replacement boundaries; the path matcher owns structural token boundaries and never publishes raw values in diagnostics.

**Fail-closed state:** An ambiguous or malformed structured path is not partially rewritten; the existing safe failure result is returned and the original text stays out of diagnostics and cloud submission.

**Allowed transitions:** `input -> structured_match -> host_only_replaced -> sanitized_output`, or `input -> ambiguous -> failed_closed`.

**Deterministic proof:** Cover case-insensitive host matching, host/path separation, repeated separators, malformed paths, and plain hostname terms. Assert the replaced host and unchanged suffix without live cloud access.

**Red-capable reproduction:** Match a sensitive host in a structured path and assert that a host-only replacement preserves the suffix; malformed input must fail closed rather than partially rewrite.

**Highest required seam:** Sanitizer output consumed by the protected-Send transaction, followed by the unchanged installed keyboard acceptance path.

**Evidence target:** `locally_verified` sanitizer matrix, then `live_verified` only while the installed canary and protected-Send evidence remain green.

- [ ] Add structural matching that distinguishes host tokens from path suffixes.
- [ ] Preserve the suffix exactly after host replacement.
- [ ] Add regression coverage for structured paths and malformed input.
- [ ] Document that a plain hostname can be added as its own sensitive term.
