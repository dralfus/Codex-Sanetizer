# Post-Refactor Improvement Spec: Codex Redaction Gate

## Problem Statement

The MVP now has a working local sanitizer and an extracted internal pipeline. That gives the project a safer base, but the next development wave needs clearer internal typing, smaller test ownership, debug-safe diagnostics and more production-shaped verification before adding heavier scanners, richer adapters or managed deployment.

Without this improvement layer, future work can drift back toward stringly decisions, broad test files, hard-to-debug sanitizer outcomes and thin smoke checks that prove behavior only at a happy-path level.

## Solution

Keep the public sanitizer contract stable and improve the internals around it:

- split focused pipeline tests by behavior ownership;
- introduce typed internal representations for entity type, action, detector id and scanner status while preserving public string contracts;
- add raw-free decision trace metadata for debugging sanitizer outcomes;
- add a local audit sink that writes only safe audit metadata;
- validate runtime scanner configuration and packaged scanner artifacts without requiring Git, Go, source code or network at runtime;
- add smoke coverage for the confirm handoff and attachment ingestion seams;
- refresh architecture notes after the improvements land.

The highest test seam remains the public sanitizer API. New component seams are allowed only where the behavior is naturally local to a pipeline role and the tests remain raw-value-safe.

## User Stories

1. As a future maintainer, I want sanitizer tests split by behavior area, so that I can change one pipeline role without hunting through a giant test file.
2. As a future maintainer, I want internal sanitizer decisions to use typed values, so that invalid entity/action/status strings are harder to introduce.
3. As a security reviewer, I want the public API to remain string-compatible, so that adapters and audit consumers do not break during internal cleanup.
4. As a Codex user, I want sanitizer behavior to remain unchanged while internals improve, so that refactoring does not weaken protection.
5. As a maintainer debugging a blocked prompt, I want a raw-free decision trace, so that I can understand which stage decided to allow, confirm or block.
6. As a security reviewer, I want decision traces to omit raw prompt text and raw entity values, so that diagnostics do not become a leak channel.
7. As a user, I want local audit events to be persisted safely, so that I can later confirm the guard was active without exposing sensitive data.
8. As a maintainer, I want audit retention and write failure behavior tested, so that audit persistence does not break prompt safety.
9. As a packager, I want scanner path and provenance validation, so that runtime does not silently fall back to unsafe scanner behavior.
10. As a user, I want the confirm handoff path to be smoke-tested as an executable workflow, so that `Confirm sanitized prompt` remains more than a model object.
11. As a user, I want attachment ingestion boundaries tested, so that large pasted content and readable text attachments remain inside the sanitizer path.
12. As a maintainer, I want architecture notes updated after the improvement pass, so that future agents start from the real system, not from stale diagrams.

## Implementation Decisions

- Public contracts remain stable for this improvement wave.
- Typed values are internal first; public records and serialized audit fields keep their current string shape until an explicit external contract migration is approved.
- The decision trace records stages, detector ids, entity types, actions, counts, durations and reason codes, but not raw prompt text, raw entity values, normalized values or sanitized prompt text.
- Audit persistence is local-only and fail-closed only when the audit write is part of a cloud-bound sensitive decision that requires enforcement.
- Scanner validation checks configured runtime artifact existence, provenance metadata and packaging assumptions; it does not build Gitleaks at runtime.
- Confirm handoff smoke proves adapter-owned submission of sanitized text after approval; it does not claim unsupported Codex prompt rewriting.
- Attachment ingestion smoke covers text attachment and file snippet handoff into `ContentPart`; binary/PDF/Office/image parsing remains out of scope.

## Testing Decisions

- Keep the public sanitizer API as the highest end-to-end seam.
- Keep package smoke proving allow, confirm, scanner-backed secret redaction, guard block and local restore.
- Add focused component tests only for typed internal conversion, decision trace, audit sink, scanner config validation, handoff and attachment ingestion boundaries.
- Every new diagnostic/audit test must assert that raw sensitive values are absent.
- Every ticket must end with `dotnet build`, `dotnet test` and `--self-test` unless it is documentation-only.

## Out of Scope

- Changing public sanitizer contracts.
- Adding Presidio, TruffleHog or LlamaFirewall integrations.
- Adding PDF/Office/image/OCR/archive extraction.
- Enterprise policy signing or managed deployment.
- Claiming transparent Codex prompt rewriting without an official supported API.
- Storing non-restorable secrets for restoration.

## Further Notes

The current architecture is adequately described in the architecture and sanitizer design documents. The next backlog should therefore focus on strengthening the extracted pipeline and production safety rails, not on another broad rewrite.

## Implementation Status 2026-07-18

The improvement wave is implemented in the MVP codebase:

- focused sanitizer pipeline tests are physically split by behavior ownership instead of one catch-all pipeline test file;
- internal sanitizer candidates now carry typed entity type, detector id and action values, and scanner fatality checks use typed scanner status values;
- public `SanitizationResult`, `Replacement`, `SanitizedEntity`, `AuditEvent` and CLI output remain string-compatible;
- raw-free decision trace metadata is emitted through `AuditEvent.ScannerStatuses` with `trace.*` keys for stage status, reason code, candidate counts, detector counts, type counts, action counts and verification status;
- `FileAuditSink` writes local audit event JSON files under the configured audit directory using atomic writes and count-based retention;
- audit write failures produce raw-free warnings, and sensitive `confirm` decisions fail closed to `block` when mandatory audit persistence is configured and fails;
- scanner runtime validation checks local Gitleaks binary presence and source-build provenance without invoking Git, Go, source checkout or network;
- scanner configuration errors can be surfaced through a guarded scanner as `configuration_error`, which is fatal for cloud-bound sanitizer flow;
- package smoke now covers scanner config validation, adapter-owned confirm handoff and attachment ingestion boundary in addition to the original allow/confirm/scanner/guard/restore paths;
- attachment ingestion exposes explicit helpers for readable text attachments, file snippets and unsupported binary metadata. PDF, Office, image, OCR and archive parsing remain out of scope.
