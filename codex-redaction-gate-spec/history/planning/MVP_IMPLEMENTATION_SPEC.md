# MVP Implementation Spec: Codex Redaction Gate

## Problem Statement

Codex users paste real engineering context into prompts: logs, URLs, internal hostnames, private IPs, project names, customer names, file paths, emails, tokens and passwords. Manual cleanup is too slow and easy to forget. A local guard must stop sensitive prompts before cloud submission and provide a sanitized replacement that remains useful for engineering work.

The first implementation should not try to become a full enterprise DLP system. It should prove the core safety loop: detect sensitive prompt content locally, replace it safely, block raw submission in guard mode, and hand the sanitized prompt back to the user.

## Solution

Build a local MVP around a redaction orchestrator.

The Codex `UserPromptSubmit` hook is used as a safety guard: it receives the prompt before submission, sends it to the local sanitizer, and blocks the original prompt when sensitive content is detected. The primary MVP UX is not manual copy/paste; it is a local confirmation/handoff UI where the user clicks `Confirm sanitized prompt`, after which an adapter that owns the submit action sends only the sanitized prompt. Clipboard handoff remains a fallback, not the happy path.

Detection is composed from several scanner backends:

- Gitleaks for secrets and credentials;
- built-in technical patterns for URLs, internal domains, private IPs, CIDRs, file paths, emails and connection strings;
- local dictionaries for customer, product, project, supplier and internal system names;
- local custom regex rules for organization-specific identifiers.

The redaction orchestrator converts scanner findings into normalized spans, resolves overlaps, applies policy, allocates pseudonyms through a local HMAC mapping vault, renders the sanitized prompt, verifies that selected raw spans are absent, and returns `allow | confirm | block`.

MVP coverage includes prompt text, large pasted text and text attachments/file snippets that the adapter can read before cloud submission. Unsupported binary attachments must not be silently allowed; the MVP must block them or show an explicit local warning that requires conversion/extraction before submission.

## User Stories

1. As a Codex user, I want prompts to be scanned before submission, so that accidental sensitive data does not reach the cloud.
2. As a Codex user, I want Gitleaks-backed secret detection, so that common credentials are caught by a mature scanner.
3. As a Codex user, I want Gitleaks output to be redacted before logging, so that the scanner itself does not leak secrets.
4. As a Codex user, I want internal URLs and domains to be pseudonymized, so that infrastructure names remain local.
5. As a Codex user, I want private IPs and CIDRs to be pseudonymized, so that internal network topology remains local.
6. As a Codex user, I want Windows paths and usernames to be pseudonymized, so that local workstation context remains local.
7. As a Codex user, I want customer, project and product names to come from dictionaries, so that business-sensitive terms are protected without code changes.
8. As a Codex user, I want custom regex rules, so that local naming conventions can be protected.
9. As a Codex user, I want secrets to be non-restorable by default, so that tokens and passwords are not stored for later restoration.
10. As a Codex user, I want stable pseudonyms for restorable identifiers, so that Codex can reason consistently across a sanitized prompt.
11. As a Codex user, I want a local confirmation window, so that I can inspect the sanitized prompt before resubmitting it.
12. As a Codex user, I want a deliberate `Confirm sanitized prompt` action, so that the sanitized prompt is sent only after I approve it.
13. As a Codex user, I want fail-closed behavior when scanners fail, so that scanner errors do not become leaks.
14. As a security reviewer, I want raw values excluded from audit logs, so that the guard does not create a second leak channel.
15. As a Codex user, I want text attachments and large pasted file contents to be scanned, so that the largest leak path is covered by the same rules as prompt text.
16. As a Codex user, I want unsupported binary attachments to be blocked or explicitly warned, so that unreadable files are not treated as safe.
17. As a future maintainer, I want scanner backends to be pluggable, so that Presidio, TruffleHog or LlamaFirewall can be added later without rewriting the core engine.
18. As a future maintainer, I want the highest test seam to be the sanitizer API, so that behavior can be verified without automating the Codex UI.

## Implementation Decisions

- The first implementation is guard mode, not full transparent gateway mode.
- The core seam is the sanitizer API: it accepts raw text and context, and returns sanitized text, entity metadata, warnings, audit metadata and `allow | confirm | block`.
- Gitleaks is the first external scanner backend for secrets.
- Gitleaks must run in pipe mode for prompt text.
- Gitleaks must use JSON output and redaction mode.
- Gitleaks line/column findings must be converted to absolute offsets before span resolution.
- The built-in technical scanner covers non-secret identifiers that Gitleaks is not designed to catch.
- The dictionary scanner covers business terms that no generic scanner can know.
- The custom regex scanner is policy-controlled and validated before activation.
- The span resolver owns conflict handling across scanner backends.
- The policy engine owns final action decisions.
- The HMAC mapping vault owns stable restorable pseudonyms.
- Secrets are redacted as non-restorable unless a future explicit exception workflow is approved.
- LlamaFirewall is not part of the redaction MVP. It is a later adjacent AI-security guardrail layer for prompt injection, jailbreak and unsafe-code concerns.
- Presidio remains a later PII scanner candidate after a packaging spike.
- TruffleHog remains a later optional secret scanner candidate after a binary/package decision.
- The local confirmation/handoff UI is part of the MVP because raw prompt submission must stop until sanitized content is approved.
- The happy path is `Confirm sanitized prompt` followed by adapter-controlled submission of `sanitized_text`.
- Clipboard copy is fallback/diagnostic behavior, not the primary workflow.
- Text attachments and large pasted file contents are in MVP scope when the adapter can access their text before submission.
- Unsupported binary attachments are not considered safe by default; MVP must block or explicitly warn rather than ignore them.

Decision-rich contract:

```text
sanitize(raw_text, context) -> SanitizationResult

SanitizationResult
- decision: allow | confirm | block
- sanitized_text
- entities[]
- replacements[]
- warnings[]
- audit_event
```

For attachment-aware calls, `context` includes `content_source` and source metadata. The sanitizer may receive multiple text parts, but each part must pass through the same detector, span resolver, policy, renderer and verifier pipeline.

Adapter behavior:

```text
allow -> allow prompt when sanitized_text is equivalent to raw text
confirm -> block original prompt, show highlighted sanitized handoff UI, and send sanitized_text only after user confirms
block -> block original prompt and show local reason
```

## Testing Decisions

- Test the sanitizer API as the highest seam.
- Test external behavior: raw input to decision, sanitized text, entity types, warnings and audit metadata.
- Use synthetic sensitive-shaped samples only.
- Include fixtures for secrets, internal URLs, private IPs, CIDRs, Windows paths, emails, connection strings, JWTs, private key blocks, customer names, project names, product names and public allowlisted URLs.
- Include fixtures for text attachment content and large pasted file contents.
- Test that unsupported binary attachment metadata produces `block` or an explicit warning decision, never silent allow.
- Test that Gitleaks scanner output is parsed correctly and raw `Match`/`Secret` values are never persisted.
- Test line/column to offset conversion with CRLF and LF input.
- Test overlap resolution, including CIDR vs IP, bearer token vs nested API key, and connection string vs embedded password/hostname.
- Test fail-closed behavior for Gitleaks timeout, invalid JSON, scanner exit errors, policy load failure, vault failure and verification failure.
- Test dictionary and custom regex policy activation only after validation.
- Test that public allowlists do not override secrets or global blocklist terms.
- Test confirmation UI behavior at the adapter contract level: confirm sends `sanitized_text`, cancel sends nothing, and the original prompt is never submitted after sensitive data is detected.

## Out of Scope

- Transparent prompt replacement inside Codex without a verified prompt rewrite API.
- Polished full gateway/composer UX beyond the minimal confirm-and-send adapter.
- Full binary/PDF/Office/image parsing, OCR and recursive archive scanning.
- Browser-extension interception.
- Presidio production integration.
- TruffleHog production integration.
- LlamaFirewall integration.
- Enterprise-managed signed policy distribution.
- Secure migration/export of the mapping vault.
- Perfect detection of all sensitive data.

## Further Notes

The MVP should be small and sharp: prove that a raw prompt or text attachment with a token, internal URL, private IP and customer/project term is blocked before cloud submission, transformed into useful sanitized content, and handed back locally without logging raw sensitive values.

Before coding starts, implementation needs a few concrete choices recorded in `IMPLEMENTATION_READINESS_REVIEW.md`: target language/runtime, Gitleaks packaging mode, local storage format, policy file format, UI handoff mechanism and cleanup plan for spike artifacts.
