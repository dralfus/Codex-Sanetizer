# ADR-001: Local Sanitizer Before Cloud Boundary

## Status

Accepted

## Context

The project must prevent accidental disclosure of internal URLs, domains, IPs, customer names, product names, paths, tokens, passwords, keys, and similar sensitive data to Codex/OpenAI.

The risky design temptation is to treat redaction as a helper script that the user runs manually, or as a post-processing step after the prompt has already entered Codex. Both fail the core security goal. The last safe point is before the prompt crosses the local-to-cloud boundary.

Current Codex prompt rewriting capability is not assumed. If the available hook surface can only block a prompt, then it cannot provide transparent replacement by itself. The architecture must remain valid under that limitation.

## Decision

Build the sanitizer as a deterministic local engine that runs before cloud submission and returns a structured decision:

```text
allow | confirm | block
```

The engine returns `sanitized_text`, entity metadata, replacement metadata, warnings, and a raw-value-free audit event. Every adapter must enforce this contract:

- `allow`: submit the sanitized text, which may be identical to the raw text when no sensitive data is detected;
- `confirm`: show local confirmation and submit only the sanitized text after approval;
- `block`: prevent original submission.

Stable identifiers such as URLs, domains, IPs, emails, products, projects, and customer names are replaced with typed HMAC-backed pseudonyms. Secrets such as tokens, passwords, private keys, cookies, and bearer credentials are non-restorable by default.

## Consequences

Positive:

- The redaction logic is testable without Codex UI internals.
- The same sanitizer can serve a hook guard, local composer, desktop overlay, browser adapter, or future official Codex rewriting API.
- The architecture stays honest when hooks can block but not mutate prompts.
- Audit logs can prove decisions without storing raw prompt content.

Negative:

- Full low-friction UX requires a gateway or confirmed prompt rewriting API.
- Stable pseudonyms create a sensitive local mapping vault that must be encrypted and protected.
- False negatives remain possible unless dictionaries and policies are maintained.
- Restored output becomes a sensitive local artifact and must be marked clearly.

## Guardrails

- Never log raw prompts or normalized original values.
- Never send mapping vault contents, HMAC secrets, or restored output to Codex automatically.
- Fail closed on detector, vault, policy, or verification failure.
- Treat hook-only integration as guard mode unless official prompt rewriting is verified.
- Keep secret redaction non-restorable by default.
