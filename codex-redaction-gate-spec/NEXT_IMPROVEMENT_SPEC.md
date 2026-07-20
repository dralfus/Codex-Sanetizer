# Next Improvement Spec: Gateway UX, Policy Ops, Audit Integrity and Packaging

## Problem Statement

The current architecture is documented and the MVP sanitizer pipeline is implemented: the project has a local fail-closed sanitizer, typed internal decisions, raw-free trace metadata, local audit persistence, scanner configuration validation, confirm handoff smoke coverage and attachment ingestion boundaries.

The next problem is not another sanitizer rewrite. The project now needs production-shaped operational workflows around the implemented core: users must be able to manage sensitive terms safely, validate local readiness, trust local audit history, package scanner artifacts reproducibly and use a submit-owning gateway path without manual copy as the happy path.

Without this next layer, the sanitizer can pass tests while still being awkward to operate: policy edits stay manual, audit files are not tamper-evident, scanner artifacts are only smoke-validated, attachment intake is limited to already-readable content parts, and the confirm flow is proven by tests but not yet pleasant as an executable user workflow.

## Solution

Keep the current architecture and improve the production seams around it:

- add a local readiness/doctor command that reports storage, scanner, policy, vault and audit status without raw data;
- add policy and dictionary management workflows for manually adding sensitive customer, project, product, system, domain, URL and regex rules;
- add staged policy activation with dry-run validation and rollback;
- strengthen audit persistence with tamper-evident hash chaining and local verification/reporting;
- enforce packaged Gitleaks binary checksum/provenance expectations at package smoke and runtime configuration boundaries;
- expand attachment intake to read plain text file inputs with size/type caps, while keeping PDF, Office, image, OCR and archive parsing out of scope;
- build a minimal submit-owning local gateway/composer shell that sends only approved `sanitized_text`;
- add release readiness smoke coverage that proves the operational loop end to end.

The current `ISanitizer` public contract remains the highest behavior seam. New features should prefer existing seams: sanitizer API, policy loader, storage layout, audit sink, package smoke, guard shell, confirmation model and submit-owning adapter.

## User Stories

1. As a Codex user, I want a local readiness check, so that I know whether the guard can safely sanitize before I submit cloud-bound prompts.
2. As a Codex user, I want readiness failures to use raw-free reason codes, so that diagnostics do not leak the very data the guard protects.
3. As a Codex user, I want to add a customer name to the sensitive dictionary from a local command, so that I do not edit CSV files by hand.
4. As a Codex user, I want to add internal domains and URL prefixes to policy from a local command, so that company-specific systems are consistently pseudonymized.
5. As a security reviewer, I want invalid policy or dictionary edits rejected before activation, so that a bad rule cannot silently weaken protection.
6. As a security reviewer, I want policy activation rollback, so that the last known good configuration remains available after a bad edit.
7. As a maintainer, I want policy precedence to be explicit, so that global, project and session rules do not behave unpredictably.
8. As a security reviewer, I want audit logs to be tamper-evident, so that local history can show whether entries were removed or modified.
9. As a Codex user, I want an audit summary command, so that I can confirm the guard was active without reading raw JSON files.
10. As a packager, I want packaged Gitleaks checksums enforced, so that the runtime scanner is the source-built binary I intended to ship.
11. As a packager, I want package smoke to validate the installed artifact shape, so that the user machine does not need Git, Go, Gitleaks source code or network access.
12. As a Codex user, I want readable text files to be ingested with clear size/type limits, so that large paste/file workflows are covered by the same sanitizer path.
13. As a Codex user, I want unsupported binary attachments to remain blocked with safe guidance, so that unreadable files are never treated as safe by default.
14. As a Codex user, I want a local gateway/composer shell with `Confirm sanitized prompt`, so that the happy path is approve-and-send rather than copy/paste.
15. As a Codex user, I want cancel and block paths to submit nothing, so that mistakes fail closed.
16. As a maintainer, I want release readiness smoke coverage, so that future changes prove policy, scanner, audit, attachment and gateway paths together.

## Implementation Decisions

- Do not redesign the sanitizer pipeline. The architecture documents already describe the current pipeline shape sufficiently.
- Keep `ISanitizer.Sanitize` as the highest sanitizer behavior seam and keep public result contracts string-compatible.
- Add operational commands around existing storage, policy, scanner and audit components rather than adding a database or remote service.
- Store user-managed policy and dictionary files under user-local storage by default; project-local policy can be supported only as an explicit input.
- Keep the mapping vault user-global and local, with HMAC/DPAPI constraints unchanged.
- Policy activation should use an expand-and-validate flow: write a candidate, validate it, atomically promote it, and retain the last known good configuration.
- Audit hash chaining must never include raw prompt text, raw entity values, normalized values, sanitized prompts or restored output. Hash only the persisted raw-free audit payload and previous entry hash.
- Audit verification should report counts, decision distribution, warning codes, broken-chain status and time ranges, not raw content.
- Scanner package validation should compare configured binary/provenance metadata and checksum expectations without building or downloading at runtime.
- The gateway/composer shell should own the submit action and should use existing confirmation and submit-owning adapter semantics. It must not claim transparent Codex prompt rewriting.
- Attachment intake can read plain text files and snippets with size/type caps. PDF, Office, image, OCR and archive extraction remain separate future work.

## Testing Decisions

- Continue using public sanitizer API tests for end-to-end sanitizer behavior.
- Add component tests only where behavior is naturally local: policy activation, audit hash verification, scanner package validation and plain-text attachment intake.
- Package smoke should remain the release-level seam for app artifact, scanner artifact, provenance, no Git/Go/network runtime requirement, sanitizer paths, gateway handoff and attachment boundary.
- CLI/readiness outputs must be tested for raw-value absence.
- Audit integrity tests must prove both valid chains and broken chains without relying on raw prompt values.
- Gateway tests should prove approve submits only `sanitized_text`, cancel submits nothing, block submits nothing and fallback clipboard remains non-happy-path.
- Every ticket should end with build, tests and self-test unless it is documentation-only.

## Out of Scope

- Replacing the sanitizer architecture.
- Changing public sanitizer contracts.
- Adding Presidio, TruffleHog, LlamaFirewall or other new scanner backends.
- Transparent Codex prompt rewriting without a verified supported API.
- PDF, Office, image, OCR or archive extraction.
- Remote telemetry, cloud audit logging or enterprise SIEM integration.
- Storing non-restorable secrets for restoration.
- Moving policy/vault/audit storage into a database before file-based storage proves insufficient.

## Further Notes

Architecture verification result: the current architecture is described in `ARCHITECTURE.md` and `SANITIZER_DESIGN.md`, and the post-refactor improvement state is recorded in `POST_REFACTOR_IMPROVEMENT_SPEC.md`. This spec is therefore a next-frontier operating plan, not a corrective architecture rewrite.

## Implementation Status 2026-07-18

The next-frontier production readiness slice is implemented in the MVP codebase:

- `ReadinessDoctor` reports raw-free storage, policy, vault, vault-secret, audit and scanner status.
- `ManagedSensitiveDictionary` supports user-local add/list/remove workflows with safe summaries and loads terms into sanitizer construction.
- `PolicyActivationStore` validates candidate TOML and dictionary CSV before activation and supports rollback to the previous active policy.
- `PolicyPrecedenceReporter` reports raw-free source order, active profile ids, rule counts and deterministic conflict semantics.
- `FileAuditSink` persists tamper-evident audit records with previous/current hashes and a chain-head file; retention rebuilds the retained chain.
- `AuditChainVerifier` detects modified, removed or reordered records.
- `AuditSummaryReporter` provides raw-free decision and warning-code summaries with chain status and first/last event timestamps.
- Scanner runtime validation now enforces Gitleaks binary checksum matches the source-build provenance.
- Package smoke includes checksum validation and a release manifest smoke pass.
- `PlainTextAttachmentIntake` reads supported UTF-8 plain-text files into sanitizer content parts with size/type caps and fail-closed unsupported metadata.
- `LocalComposerShell` provides a minimal submit-owning approve/send flow over the sanitizer and submit adapter.
- `SubmitOwningAdapter` reports raw-free confirmation and submit failure statuses instead of throwing through gateway flows.
- `RestorationHandoff` ties local restore to the restored-output resubmission guard.
- `ReleaseReadinessSmokeRunner` verifies policy, audit, scanner packaging, attachment intake, gateway handoff and restoration handoff in one matrix.

## Post-Implementation Architecture Review 2026-07-18

The implemented slice keeps the core sanitizer architecture sound: detection, policy activation, audit integrity, packaging validation and gateway handoff are separate components around the sanitizer pipeline rather than new roles added to one orchestrator.

Recommended next changes:

- Split the remaining broad `Tests.cs` legacy coverage into behavior-owned files when touching those areas; new operational coverage already lives in `SanitizerOperationalTests.cs`.
- Add CLI-level golden tests for the management commands once the command surface stabilizes.
- Add an installer/runtime configuration spec for where the source-built Gitleaks artifact, provenance file and user-local policy/vault/audit paths are discovered in packaged builds.
- Keep PDF/Office/OCR/archive attachment parsing out of the core sanitizer until a dedicated extractor boundary is specified and can fail closed with raw-free diagnostics.
