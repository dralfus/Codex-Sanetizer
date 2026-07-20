# ADR-003: MVP Scanner Composition

## Status

Accepted

## Context

The project needs a practical first implementation that protects Codex prompts before cloud submission without rebuilding every detector from scratch.

The tool evaluation spike showed:

- Gitleaks is practical as a lightweight secret scanner and supports pipe mode plus redacted JSON output.
- Gitleaks alone is not enough because it is focused on credentials, not internal URLs, domains, IP/CIDR, file paths, emails, customer names, project names, product names or restoration.
- llm-redactor is the closest conceptual reference, but it should not be adopted wholesale for the MVP because its storage model, maturity and dependency weight do not match the current target.
- LlamaFirewall is useful for prompt-injection and AI security guardrails, but it is not a privacy redaction vault.
- TruffleHog and Presidio remain useful later candidates, but they are too heavy or unresolved for the first local MVP path.

## Decision

Build the MVP as a local redaction orchestrator with reusable scanner backends:

```text
Codex UserPromptSubmit guard
  -> redaction orchestrator
      -> Gitleaks pipe scanner for secrets
      -> built-in technical pattern scanner for URL/IP/CIDR/path/email/connection strings
      -> local dictionary scanner for customer/project/product/supplier/system names
      -> local custom regex scanner
      -> span resolver
      -> policy engine
      -> HMAC mapping vault
      -> renderer/verifier
  -> local confirmation / sanitized prompt handoff UI
```

Use Gitleaks as the first external scanner backend with strict operational constraints:

- built from source in the project release process from a pinned tag/commit;
- source revision, build command, Go version and binary checksum recorded;
- runtime package ships the resulting `gitleaks.exe`;
- normal user install/runtime does not require Git, Go or Gitleaks source code;
- stdin/pipe mode only for prompt text;
- JSON output;
- `--redact` always enabled;
- Gitleaks timeout up to 5 seconds inside a 10-second total sanitizer hard cap;
- no raw secret values in logs;
- line/column findings converted to absolute spans before rendering.

Use llm-redactor as a design reference for typed placeholders, scrub/restore flow and regex taxonomy, not as a required MVP dependency.

Keep LlamaFirewall out of the redaction MVP. Consider it later as a separate guardrail layer after privacy redaction works.

The MVP also includes:

- .NET as the Windows-first runtime for the orchestrator, file-based vault, confirmation UI and adapter layer;
- file-based mapping vault, not a database, with DPAPI-protected HMAC/encryption secret;
- text attachment and large paste scanning through the same sanitizer pipeline;
- `Confirm sanitized prompt` UX where a submit-owning adapter sends only `sanitized_text` after approval.

## Consequences

Positive:

- Avoids rewriting mature secret detection rules from scratch.
- Keeps the privacy boundary and mapping vault under local project control.
- Allows adding more scanner backends later without changing the adapter contract.
- Keeps the first implementation small enough to test and operate.

Negative:

- Gitleaks output must be adapted from line/column to spans.
- Non-secret sensitivity still needs project-owned detectors and policy files.
- Scanner orchestration adds failure-mode complexity.
- Source-building Gitleaks shifts build reproducibility into our release process.
- Confirm-and-send requires an adapter that owns submission; hook-only blocking remains fallback/guard mode.
- LlamaFirewall and Presidio value is deferred.

## Guardrails

- External scanner failures must not silently allow sensitive prompts.
- Sanitizer timeout must fail closed at the 10-second total hard cap.
- Scanner output is untrusted input and must be normalized before span resolution.
- Broad detector backends cannot write raw values to persistent logs.
- The orchestrator, not any scanner backend, owns the final `allow | confirm | block` decision.
