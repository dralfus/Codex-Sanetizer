# Requirements

## Functional Requirements

### Prompt Interception

- The system must inspect user input before it is sent to Codex/OpenAI.
- The system must support a global mode that applies across projects.
- The system must prevent sending the original prompt when sensitive data is detected and has not been approved in sanitized form.
- The system should integrate with Codex hooks when available.
- The system must support an integration strategy that can replace the submitted prompt, not only block it.
- The system must inspect text attachments, large pasted file contents and file snippets when the adapter can access their text before cloud submission.
- The system must not silently allow unsupported binary attachments as if they were scanned.

### Detection

The detector must identify at least:

- HTTP/HTTPS URLs.
- Internal and external domains.
- Email addresses.
- Private IPv4 addresses and CIDR ranges.
- IPv6 addresses where practical.
- Password assignments and password-like values.
- API keys, bearer tokens, JWTs and session cookies.
- Private keys and certificate blocks.
- Connection strings.
- Usernames where they appear in sensitive contexts.
- Customer names, product names, project names and company-specific terms from local dictionaries.
- File paths that expose user names or internal project structure.

### Redaction and Pseudonymization

- The system must replace each detected entity with a typed pseudonym.
- The same original value must map to the same pseudonym across projects.
- Different entity types must not collide in the visible pseudonym namespace.
- Pseudonyms must not reveal the original value.
- Pseudonyms should be short enough to keep prompts readable.
- The system must preserve enough type information for Codex to reason about logs and architectures.
- Secrets must default to non-restorable redaction.

### Mapping Vault

- The system must maintain a local global mapping table.
- The mapping table must store original value, pseudonym, entity type, first-seen timestamp, last-seen timestamp and policy metadata.
- The mapping table must be encrypted at rest.
- The encryption key or HMAC secret must not be stored in the same unprotected plaintext storage as the mappings.
- The system must support backup/export of configuration without exporting sensitive mappings by default.
- The system must support explicit secure export of mappings for migration, with user confirmation.

### Confirmation UX

- If sensitive data is detected, the system must show:
  - sanitized prompt;
  - highlighted replacement spans;
  - changed entity types;
  - counts by type;
  - high-risk findings such as secrets;
  - action buttons: confirm sanitized prompt, cancel, edit sanitized.
- The confirmation screen must not require the user to manually run commands.
- Confirming a sanitized prompt must submit only `sanitized_text`, never the original prompt.
- The system should avoid showing full original values in the confirmation by default.
- The system may provide a reveal control for local inspection, protected by an explicit action.

### Response Restoration

- After receiving a sanitized answer, the system must offer to restore pseudonyms to originals locally.
- Restoration must happen only on the local machine.
- Restored output must be visually marked as containing real values.
- The system must support copying either sanitized or restored output.
- The system must warn before sending restored output back into Codex.

### Policy Management

- The system must support global policies.
- The system should support per-project overlays.
- The system must support manually adding sensitive values without code changes.
- The system must support local dictionaries for customer, product, project, supplier, domain and system names.
- The system must support custom regex rules with validation before activation.
- The system must support a way to test policy rules against sample text before saving them.
- Global blocklists must override project allowlists for high-risk values.
- Public allowlists should reduce false positives for known safe domains, package registries and documentation URLs.
- Policy changes must be auditable.

### Audit

- The system must log redaction decisions without raw original values.
- Audit events must include timestamp, application, project context if available, detector types, scanner statuses, counts, policy decision, action taken, warning codes, duration/timeout metadata, span offsets/length/type and replacement pseudonyms.
- Audit logs must not include raw prompt text by default.
- Audit logs must not include raw entity values, normalized values, sanitized prompt text, restored output, or Gitleaks `Secret`/`Match` values by default.

## Non-Functional Requirements

- The system must be fast enough to run on every prompt without noticeable friction for ordinary prompts.
- The system should target under 2 seconds for ordinary prompts and must enforce a 10-second total sanitizer hard cap.
- The system must fail closed when detector execution fails.
- The system must fail closed when scanner execution times out.
- The system must work offline for detection, redaction and restoration.
- The system must not require network calls for redaction.
- Normal runtime must not require Git, Go, Gitleaks source code, or network access to build scanner dependencies.
- The system must not depend on a specific project repository.
- The system should work on Windows first.
- The system should be packaged so it can later become a Codex plugin, desktop companion app or enterprise-managed hook package.

## Security Requirements

- Raw sensitive values must not leave the local machine through the redaction system.
- HMAC must use a local secret with sufficient entropy.
- The vault must be encrypted at rest.
- Secrets such as passwords and tokens must not be restored by default.
- The system must avoid writing raw prompts to logs.
- The system must protect against accidental leakage through crash dumps where practical.
- The system must distinguish reversible identifiers from non-reversible secrets.
- The system must make bypass visible when operating in guard mode.

## Usability Requirements

- Normal operation must not require manual script execution.
- The user should be able to keep the system always on.
- The confirmation flow should be short and predictable.
- False positives must be tunable through allowlists.
- High-risk detections must be explained plainly.
- The user must be able to understand what was sent to the cloud.

## Acceptance Criteria

- A prompt containing an internal URL is blocked or replaced before cloud submission.
- The sanitized prompt contains a stable `URL_*` pseudonym instead of the real URL.
- The same URL receives the same pseudonym in a different project.
- The local vault can restore `URL_*` in the returned answer.
- A prompt containing an API key does not send the API key and does not restore it by default.
- A text attachment containing an API key or internal URL is blocked or sanitized before cloud submission.
- An unsupported binary attachment is blocked or explicitly warned before cloud submission, not silently allowed.
- Confirming a sanitized prompt sends the sanitized prompt and does not send the original prompt.
- Audit logs show that a URL and secret were detected without storing the raw URL or secret.
- If the redaction engine crashes, the original prompt is not sent.
