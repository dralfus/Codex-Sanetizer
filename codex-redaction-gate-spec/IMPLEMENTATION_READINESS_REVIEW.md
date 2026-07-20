# Implementation Readiness Review

Date: 2026-07-17

## Accepted Direction

The current direction is ready enough to turn into implementation tickets:

```text
Codex UserPromptSubmit guard
  -> redaction orchestrator
      -> Gitleaks pipe scanner for secrets
      -> built-in technical scanner
      -> dictionary scanner
      -> custom regex scanner
      -> span resolver
      -> policy engine
      -> HMAC mapping vault
      -> renderer/verifier
  -> local confirmation / sanitized prompt handoff UI
```

Gitleaks is the first external scanner backend. LlamaFirewall is not a replacement for redaction; it is a later adjacent layer for prompt injection and AI-security checks.

## Highest Test Seam

The highest stable seam for implementation is:

```text
sanitize(raw_text, context) -> SanitizationResult
```

This seam should be tested before Codex hook integration. The Codex hook adapter should be thin: call the sanitizer, enforce `allow | confirm | block`, and never decide sensitivity itself.

## Things Still To Clarify Before Coding

### 1. Runtime and Packaging

Decision:

- Rust, Go, Python, .NET or mixed runtime?

Recommendation:

- Use .NET for a Windows-first MVP orchestrator, vault and local handoff UI.
- Keep Gitleaks as a source-built external binary for MVP.
- Avoid making the MVP depend on a heavy Python NLP stack.
- If the first slice is headless CLI/library only, Rust is also acceptable.

Why it matters:

- Hook latency, packaging, Windows startup cost and scanner timeouts depend on this choice.

### 2. Gitleaks Distribution

Decision:

- Build Gitleaks from source in the project release process from a pinned tag/commit.

Recommendation:

- Record source revision, build command, Go version and resulting binary checksum.
- Ship the resulting `gitleaks.exe` in the MVP package.
- Do not build Gitleaks from source on user machines during normal install or runtime.
- Do not require Git or Go on user machines for normal operation.

Why it matters:

- The spike showed Go source builds can be fragile and cache-heavy in restricted Windows environments.

### 3. Scanner Timeouts and Fail-Closed Policy

Decision:

- What is the max scanner latency before blocking?

Recommendation:

- Target under 2 seconds for normal prompts.
- Use 10 seconds as a total hard cap, especially for text attachments and large pasted content.
- Give Gitleaks up to 5 seconds inside the total hard cap.
- Keep built-in/dictionary/regex scanners under 1 second total.
- Show progress after 500 ms so the UI does not look frozen.
- Treat scanner timeout, policy error, vault error and verification error as `block` for MVP.

Why it matters:

- A hook that hangs will be disabled. A hook that allows on scanner failure leaks.

### 4. Policy File Format

Decision:

- TOML only, CSV dictionaries plus TOML policy, or YAML?

Recommendation:

- MVP: CSV dictionaries plus TOML policy.
- Use CSV for tabular manual dictionaries and TOML for structured policy, scanner settings, allowlists, blocklists, precedence and defaults.
- If one format is required later, prefer TOML-only over CSV-only.

Why it matters:

- Users need easy manual additions, but regex and allow/block policy need structured metadata.

### 5. Mapping Vault Storage

Decision:

- Use a file-based vault for MVP, not a database.

Recommendation:

- Store mappings in `vault.json` or `vault.jsonl` with atomic write/replace.
- Build in-memory indexes at startup for lookup and reverse lookup.
- Protect the HMAC/encryption secret with DPAPI on Windows.
- Keep plaintext vault as explicit dev/diagnostic mode only.
- Defer SQLite unless file-based storage becomes unreliable or too slow.

Why it matters:

- Stable pseudonyms require durable lookup. The vault becomes a sensitive asset.

### 6. Clipboard and Local UI

Decision:

- MVP handoff is a local confirmation window with `Confirm sanitized prompt`.

Recommendation:

- The primary happy path is `Confirm sanitized prompt` -> adapter sends only `sanitized_text`.
- Replaced spans are highlighted inline.
- The window shows sanitized text, counts by type, scanner status, warnings and confirm/cancel actions.
- Clipboard writes and temp files are fallback/diagnostic only.

Why it matters:

- The desired UX requires an adapter that owns the submit action, because hook-only blocking cannot reliably implement confirm-and-send.

### 7. Audit Log Detail

Decision:

- What exact fields are allowed in audit?

Recommendation:

- Store timestamp, decision, entity counts, action counts, policy profile, scanner names/statuses, warning codes, durations/timeouts, spans as offsets/length/type and replacement pseudonyms.
- Store keyed fingerprints only when needed for debugging duplicates.
- Do not store raw prompt, sanitized prompt, raw entity values or normalized values by default.

Why it matters:

- The audit log must prove behavior without becoming a leak database.

### 8. Public Allowlist Scope

Decision needed:

- Which public URLs/domains are allowed by default?

Recommendation:

- Keep default allowlist small: common public docs/package registries only.
- Make project-specific allowlists explicit.

Why it matters:

- Broad allowlists are an easy accidental bypass.

### 9. Attachment and File Coverage

Decision:

- Does MVP block attachments entirely, warn, or ignore?

Recommendation:

- MVP must cover text attachments, large pasted text and file snippets when the adapter can access their text.
- Unsupported binary/PDF/Office/image attachments must be blocked or explicitly warned before cloud submission until dedicated parsers exist.

Why it matters:

- Prompt-only protection can create a false sense of coverage.
- Large logs/config dumps are often attached rather than typed directly.

### 10. Spike Artifact Cleanup

Decision:

- Keep or remove the heavy TruffleHog source/build caches from `spikes/tool-evaluation`.

Recommendation:

- Keep the written report and small corpus.
- Remove failed build caches and cloned source trees after review, or move them outside the durable spec package.

Why it matters:

- The spike directory is several GB and includes third-party source/build cache that is not needed for the architecture docs.

## Not Blocking Implementation

These can be deferred:

- Presidio packaging.
- TruffleHog binary/package decision.
- LlamaFirewall integration.
- Polished gateway/composer UX beyond the minimal confirm-and-send adapter.
- Enterprise policy signing.
- Full binary/PDF/Office/image parsing, OCR and recursive archive scanning.
- Mapping vault migration/export.

## First Implementation Ticket Shape

The first ticket should not be "build the UI". It should be:

```text
Build sanitizer orchestrator MVP around synthetic prompt fixtures.
```

Acceptance criteria:

- raw prompt with Gitleaks-detectable secret returns `confirm` or `block`;
- raw prompt with internal URL/IP/domain/customer term returns sanitized text with stable pseudonyms;
- secret is redacted as non-restorable;
- audit event contains counts/types only;
- scanner timeout fails closed;
- text attachment content goes through the same sanitizer path as prompt text;
- unsupported binary attachment does not silently allow;
- no raw values are written to persistent logs.
