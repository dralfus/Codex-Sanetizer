# Grill Review

This document records the hard questions the design must answer before implementation.

## 1. Where exactly is the last safe interception point?

If the prompt has already left the local machine, redaction is too late. The implementation must prove that interception happens before cloud submission.

Decision: require either a verified pre-submit rewriting API or a gateway/composer that owns the submit action. A blocking hook alone is only guard mode.

## 2. Can Codex hooks mutate user prompts?

If `UserPromptSubmit` can only allow or block, it cannot implement automatic replacement and confirmation by itself.

Decision: treat prompt mutation as unverified until proven by a working prototype or official API. Do not design around private behavior.

## 3. What happens when the user pastes a secret?

Hashing a password or token still creates a stable representation of a secret and may preserve too much.

Decision: classify secrets separately from identifiers. Redact secrets as non-restorable by default.

## 4. Does stable mapping create a new sensitive local asset?

Yes. The global mapping vault becomes a high-value local asset, even if it is implemented as files rather than a database.

Decision: encrypt it, keep it outside repos, avoid raw exports and log only counts/types.

## 5. Can pseudonyms leak meaning through type and repetition?

Yes. `CUSTOMER_A` appearing repeatedly can still reveal relationship structure.

Decision: accept this as a tradeoff for useful reasoning, but keep real names local. For very sensitive work, support less stable session-only aliases.

## 6. What if the model needs the real value to solve the task?

Usually it does not need real values. It needs type, consistency and relationships. If exact value is required, the user must make a conscious exception.

Decision: add explicit temporary reveal/allow workflow with audit trail.

## 7. How do we prevent restored output from being sent back?

Restored output is useful locally but dangerous if copied into a later prompt.

Decision: gateway should detect its own restored markers and block or re-sanitize before sending.

## 8. How do we handle attachments and files?

Prompt-only protection is incomplete if the user attaches logs, reports or configs.

Decision: MVP must cover text attachments, large pasted file contents and file snippets when the adapter can access the text. Unsupported binary/PDF/Office/image attachments must be blocked or explicitly warned until dedicated extraction/parsing exists.

## 9. How do we keep this from becoming annoying?

If the gate blocks too often, the user will disable it.

Decision: make common safe paths one click, provide allowlists and keep confirmations compact.

## 10. What is the first useful implementation slice?

The smallest useful slice is not the full UI. It is a tested redaction engine plus a guard hook that blocks obvious leaks and produces a sanitized replacement.

Decision: build engine and vault first, then adapter.

## 11. Is "replace sensitive data" a string replace problem?

No. Naive replacement can corrupt overlapping matches, miss secrets embedded inside URLs or connection strings, and accidentally leak through partial values.

Decision: sanitizer must be span-based. Detectors produce candidates with offsets, a resolver chooses a final non-overlapping set, and rendering happens in one pass from the original text.

## 12. What must be true before `send` is allowed?

The adapter must not decide this from UI state alone. It needs a structured sanitizer result.

Decision: only `allow` and approved `confirm` may submit `sanitized_text`; `block` prevents original submission. Verification failure is treated as `block`.

## 13. Where are sensitivity rules described?

If sensitivity rules live only in code, the system will miss organization-specific names and the user will need a developer for every new customer, project or domain.

Decision: use policy-as-data. Built-in detectors catch common technical patterns; local policy files and dictionaries define organization-specific sensitive terms.

## 14. Can the user manually add sensitive data?

They must be able to. Otherwise the first false negative becomes a product failure.

Decision: support manual additions through CLI in the MVP and through the confirmation UI later. Manual additions create policy or dictionary entries, not ad hoc one-off code changes.

## 15. Can a hook replace the prompt automatically?

Published Codex hooks behavior for `UserPromptSubmit` supports receiving the prompt, adding context and blocking. It does not document prompt rewriting for that event. Rewriting is documented for supported tool calls under `PreToolUse`.

Decision: treat hook-only interception as guard mode. Use gateway mode for automatic replace-confirm-send until an official prompt rewrite path is verified.

## 16. Is Gitleaks enough by itself?

No. Gitleaks is a good first backend for secrets, but this project also needs to protect internal URLs, domains, private IPs, CIDRs, file paths, emails, customer names, product names, project names and local policy terms.

Decision: use Gitleaks for secrets and keep a project-owned redaction orchestrator for policy, span resolution, pseudonymization, restoration and non-secret sensitive identifiers.

## 17. Should LlamaFirewall be part of the redaction MVP?

No. LlamaFirewall addresses prompt injection, jailbreak-style attacks and AI-security guardrails. It does not provide a privacy redaction vault, stable pseudonyms or local restoration.

Decision: defer LlamaFirewall as a later adjacent guardrail layer after the privacy redaction MVP works.

## 18. What still blocks coding?

The architecture direction is clear, but implementation still needs concrete choices for runtime, Gitleaks distribution, scanner timeouts, policy file format, mapping vault storage, local handoff UI and audit fields.

Decision: record these in `IMPLEMENTATION_READINESS_REVIEW.md` and treat them as pre-ticket clarifications.
