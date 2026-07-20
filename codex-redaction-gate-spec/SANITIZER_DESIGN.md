# Sanitizer Design

## Purpose

The sanitizer is the local transformation engine that runs before any prompt crosses the Codex/OpenAI cloud boundary. Its job is not only to find sensitive data, but to produce a replacement that is still useful for reasoning while proving that raw sensitive spans were not sent.

The core rule is strict:

```text
raw_prompt -> sanitize locally -> confirm if needed -> send sanitized_prompt only
```

If this rule cannot be enforced by the current integration surface, the adapter must fall back to guard mode and block the original prompt.

## Core API

The engine should expose one small stable API:

```text
sanitize(request) -> SanitizationResult
restore(request) -> RestorationResult
```

Suggested request:

```text
SanitizeRequest
- content_parts[]
  - content_source: prompt_text | clipboard | text_attachment | file_snippet | tool_output
  - raw_text
  - source_metadata
- context:
  - application
  - workspace_path
  - project_id
  - session_id
  - policy_profile
- options:
  - allow_session_aliases
  - allow_secret_storage
  - confirmation_mode
```

Suggested result:

```text
SanitizationResult
- decision: allow | confirm | block
- sanitized_text
- entities[]
- replacements[]
- warnings[]
- audit_event
- restore_handle
```

`raw_text` from any content part must never appear in logs, telemetry, crash reports, or cloud-bound payloads.

The MVP must process prompt text, large pasted text and text attachments/file snippets through the same pipeline. Unsupported binary/PDF/Office/image attachments are not treated as safe by default; they produce `block` or an explicit warning decision until text extraction exists.

## Processing Pipeline

### 1. Preflight

Validate input size, encoding, and context. Assign an internal request id. Do not log the raw prompt.

Timing rules:

- ordinary prompt target: under 2 seconds;
- total hard cap: 10 seconds;
- Gitleaks budget: up to 5 seconds inside the total hard cap;
- built-in, dictionary and regex scanners should finish within 1 second total;
- timeouts fail closed for cloud-bound content.

Fail closed when:

- the text cannot be decoded safely;
- policy cannot be loaded;
- the vault cannot be opened and sensitive-looking content exists;
- detector initialization fails.

### 2. Detection

Run detectors against the original text and return candidate spans. A candidate is a structured object, not a string replacement:

```text
Candidate
- span_start
- span_end
- local_fingerprint
- entity_type
- detector_id
- confidence
- normalized_value
- evidence
- default_action
```

`local_fingerprint` is optional and must be keyed, for example HMAC over entity type and normalized value. Do not store an unkeyed hash of short values such as domains, customer names, project names, or emails.

Initial detector families:

- secrets: Gitleaks plus built-in patterns for private keys, tokens, bearer values, JWTs, cookies, password assignments;
- network identifiers: URLs, domains, hostnames, IPs, CIDRs;
- identity: emails, usernames in sensitive contexts, personal names when dictionary-backed;
- local context: Windows and Unix paths, user profile paths, repository paths;
- business terms: customer, product, project, supplier, internal system names from dictionaries;
- custom policy regexes.

Secret detectors run first and have higher priority than URL/path/domain detectors because secrets are never allowed to be hidden inside larger low-risk spans.

### 3. Normalization

Normalize each candidate before lookup and HMAC:

- URLs: lowercase scheme/host, normalize default ports, preserve path structure only for policy matching;
- domains: lowercase and trim trailing dot;
- emails: lowercase domain, policy-controlled local-part casing;
- IP/CIDR: canonical network notation;
- paths: normalize separators and user-home aliases where possible;
- dictionary terms: normalize whitespace and configured case sensitivity;
- secrets: do not normalize into a reusable stable value unless explicitly allowed.

The normalized value is local-only. It can be stored encrypted in the mapping vault for restorable identifiers, but must not be written to audit logs.

### 4. Span Resolution

Detectors will overlap. The resolver must choose a non-overlapping set before rendering.

Resolution rules:

- higher risk wins over lower risk;
- longer span wins when risk is equal;
- explicit dictionary/global blocklist wins over inferred public allowlist;
- secrets inside URLs, headers, connection strings, or paths force the parent span to be blocked or split;
- ambiguous overlaps become `confirm` or `block`, never silent allow.

Example:

```text
postgres://user:password@db.internal.local:5432/app
```

The connection string detector should claim the full span. The password portion is separately marked as non-restorable secret metadata. The rendered replacement should preserve type and topology without exposing credentials:

```text
CONNECTION_STRING_91A2B03C
```

### 5. Policy Decision

The policy engine converts resolved candidates into actions:

```text
Action
- allow
- pseudonymize_restorable
- redact_non_restorable
- session_alias
- block
```

Default actions:

- public documentation URLs: allow;
- internal domains, URLs, IPs, CIDRs, emails, project names: restorable pseudonym;
- tokens, passwords, private keys, cookies: non-restorable redaction;
- uncertain high-confidence sensitive values: confirm;
- detector or vault failure with sensitive candidates: block.

### 6. Pseudonym Allocation

For restorable identifiers, the sanitizer asks the mapping vault for a stable pseudonym:

```text
pseudonym = vault.get_or_create(entity_type, normalized_value, policy)
```

Pseudonym format:

```text
TYPE_HMACPREFIX
USERNAME_adjective_name_HMACPREFIX
```

Examples:

```text
URL_8F3A21B9
DOMAIN_19C0E44A
EMAIL_BA1080F2
PROJECT_0D83A7AA
USERNAME_bright_turing_8F3A
```

Username pseudonyms intentionally use a Docker-container-name-like readable alias plus a short HMAC suffix. This keeps sanitized command prompts and paths understandable while preserving stable local mapping.

The HMAC input must include:

```text
entity_type || "\n" || normalized_value
```

Use a local secret from DPAPI, Windows Credential Manager, or an equivalent OS-protected store. Collision handling should extend the suffix length deterministically before adding arbitrary counters.

The MVP vault is file-based, not database-backed: `vault.json` or `vault.jsonl` with atomic temp-file replace and in-memory indexes for lookup/reverse lookup. Plaintext vault storage is allowed only as explicit dev/diagnostic mode with warning.

For non-restorable secrets, use a typed redaction that carries minimal diagnostic value:

```text
SECRET_REDACTED
TOKEN_REDACTED
PRIVATE_KEY_REDACTED
PASSWORD_REDACTED
```

### 7. Rendering

Render sanitized text from the original text plus the final replacement spans. Do not use repeated string replace. Use span offsets from the original input and apply replacements in a single pass.

Rendering requirements:

- preserve surrounding punctuation and line breaks;
- preserve enough type information for Codex to reason about architecture and logs;
- avoid leaking length, prefix, suffix, or original casing for secrets;
- include optional structured context only when it is sanitized.

Example prompt:

```text
Check why https://deploy.internal.example.local failed for customer ACME. Token: sk-live-abc123
```

Sanitized prompt:

```text
Check why URL_8F3A21B9 failed for customer CUSTOMER_81C04D2A. Token: TOKEN_REDACTED
```

### 8. Verification

Before returning `confirm` or `allow`, run a local verification pass:

- all selected sensitive raw spans must be absent from `sanitized_text`;
- all non-restorable secret spans must be absent;
- replacement count must equal the resolved span count;
- no replacement should overlap or corrupt Unicode boundaries;
- audit event must contain enough debug-safe metadata to explain the decision without raw values.

Verification failure is a hard `block`.

### 9. Audit Event

Audit logs record behavior, not secrets:

```text
AuditEvent
- timestamp
- request_id
- application
- workspace_hash
- policy_profile
- decision
- scanner_statuses
- entity_counts_by_type
- action_counts
- span_summaries: offset | length | type | detector_id
- replacement_summaries: pseudonym | type | action
- warnings
- adapter_mode
- durations_ms
```

Do not log raw prompt text, raw entity values, normalized values, sanitized prompt text by default, or restored output.

## Adapter Contract

Every adapter must implement the same safety contract:

```text
if result.decision == allow:
    submit(result.sanitized_text)
elif result.decision == confirm:
    show_confirmation(result)
    submit(result.sanitized_text) only after approval
else:
    block_original_submission()
```

In gateway/adapter mode, the adapter owns the submit action and can send the sanitized prompt directly after `Confirm sanitized prompt`.

In guard mode, the adapter may only block and present a sanitized replacement if prompt rewriting is not officially supported.

## Domain Model

```mermaid
classDiagram
    class Sanitizer {
      +sanitize(request) SanitizationResult
      +restore(request) RestorationResult
    }
    class DetectorRegistry {
      +detect(raw_text, context) Candidate[]
    }
    class SpanResolver {
      +resolve(candidates, policy) ResolvedEntity[]
    }
    class PolicyEngine {
      +decide(entity, context) Action
    }
    class MappingVault {
      +get_or_create(type, normalized_value, policy) Pseudonym
      +lookup(pseudonym) OriginalValue
    }
    class Renderer {
      +render(raw_text, replacements) sanitized_text
    }
    class Verifier {
      +verify(raw_text, sanitized_text, entities) VerificationResult
    }

    Sanitizer --> DetectorRegistry
    Sanitizer --> SpanResolver
    Sanitizer --> PolicyEngine
    Sanitizer --> MappingVault
    Sanitizer --> Renderer
    Sanitizer --> Verifier
```

## Current Extracted Pipeline

The MVP keeps one public sanitizer entry point, and the implementation now routes that entry point through focused internal pipeline components. The public API remains the highest behavior seam; component-level tests cover the local responsibilities that were extracted from the original sanitizer orchestrator.

Current internal roles:

- `ContentPartAssembler`: joins prompt text, large pasted text, text attachments and file snippets into one scan buffer, while keeping source-part offset mapping.
- `AttachmentGuard`: blocks unsupported binary/PDF/Office/image attachment metadata before any cloud-bound decision can be allowed.
- `PolicyBlockEvaluator`: evaluates explicit block rules and returns safe warning codes without raw values.
- `ExternalScannerOrchestrator`: owns total hard-cap math, scanner-specific budgets and fail-closed scanner status normalization.
- `DetectorRegistry`: runs detectors and returns one common candidate shape.
- `SyntheticDetector`: keeps MVP characterization markers isolated from production detectors.
- `BuiltInSecretDetector`: catches private keys, token assignments and password assignments that are not delegated to Gitleaks.
- `GitleaksFindingDetector`: converts scanner spans into non-restorable secret candidates without persisting `Secret` or `Match`.
- `TechnicalIdentifierDetector`: detects internal URLs/domains, private IP/CIDR, emails, file paths and credentialed connection strings.
- `DictionaryDetector`: applies validated CSV dictionary terms.
- `PublicAllowlistEvaluator`: applies host-boundary public allow rules without allowing internal lookalikes or nested secrets.
- `SpanResolver`: selects the final non-overlapping sensitive spans.
- `ReplacementPlanner`: chooses restorable pseudonyms vs non-restorable redactions and allocates vault pseudonyms.
- `SanitizedTextRenderer`: renders the final sanitized prompt in a single span-based pass.
- `SanitizedOutputVerifier`: proves selected raw spans are absent before returning `confirm` or `allow`.
- `SanitizerDecisionTrace`: records raw-free stage status, reason codes and detector/type/action counts for allow, confirm and block decisions.
- `AuditEventBuilder`: builds debug-safe audit metadata from decisions, spans, replacements, warnings and scanner statuses.
- `FileAuditSink`: optionally persists raw-free audit events under the local audit directory with atomic writes and count-based retention.
- `AuditChainVerifier`: verifies the raw-free tamper-evident audit hash chain and detects modified, removed or reordered audit records.
- `AuditSummaryReporter`: summarizes decisions, warning codes and audit chain status without exposing raw prompt or sanitized text.
- `ScannerRuntimeConfigurationValidator`: validates the configured Gitleaks binary/provenance package and checksum without Git, Go, source checkout or network at runtime.
- `AttachmentIngestion`: turns readable text attachments and file snippets into sanitizer content parts, while unsupported binary metadata stays fail-closed.
- `PlainTextAttachmentIntake`: reads supported UTF-8 plain-text files into sanitizer content parts with size/type caps.
- `ReadinessDoctor`: reports raw-free local readiness for storage, policy, vault, audit and scanner status.
- `ManagedSensitiveDictionary`: provides user-local sensitive-term add/list/remove workflows with safe summaries.
- `PolicyActivationStore`: validates and promotes candidate policy files atomically and can roll back to the previous active policy.
- `PolicyPrecedenceReporter`: emits raw-free diagnostics for active policy source order, profile ids and rule counts.
- `LocalComposerShell`: owns the minimal approve/send flow and submits only approved `sanitized_text`.
- `RestorationHandoff`: connects local restoration with the resubmission guard for local-sensitive output.
- `ReleaseReadinessSmokeRunner`: verifies policy, audit, scanner package, attachment, gateway and restoration paths together.

Future refactors must not change these invariants:

- `ISanitizer.Sanitize` remains the highest behavior seam.
- Cloud-bound adapters may submit only `SanitizationResult.SanitizedText`.
- Secrets stay non-restorable by default.
- Scanner, policy, vault, verifier and attachment failures fail closed.
- Configured audit persistence failures fail closed for sensitive confirm decisions and remain low-friction for non-sensitive allow decisions.
- Tamper-evident audit metadata hashes only raw-free persisted audit payloads.
- Audit, warnings, package smoke output and scanner status output remain raw-value-free.
- Guard mode remains honest about hook limits; confirm-and-send requires a submit-owning adapter.

## Implementation Slice

Build the sanitizer in this order:

1. Pure library API with deterministic unit tests.
2. Regex and parser detectors for secrets, URLs, domains, IP/CIDR, emails, paths.
3. Dictionary detector with local policy files.
4. Span resolver and renderer.
5. HMAC pseudonym allocator with an in-memory vault for tests.
6. DPAPI-protected file-based mapping vault.
7. CLI tester that prints sanitized text and counts only.
8. Codex guard adapter that blocks unsafe prompts.
9. Minimal confirmation adapter that highlights replacements and sends only `sanitized_text` after `Confirm sanitized prompt`.
10. Polished gateway composer or official rewriting adapter when an enforceable rewrite path exists.

The first useful milestone is: "given a prompt or text attachment with an internal URL and token, the library returns a sanitized prompt where the URL is a stable pseudonym, the token is non-restorable, and the original raw values are absent from audit output."
