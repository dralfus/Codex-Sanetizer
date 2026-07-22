# Codex Redaction Gate Specification

This directory documents Codex Redaction Gate: a local safety layer that intercepts user input before it is sent to Codex/OpenAI, replaces sensitive values with stable pseudonyms, stores the mapping table locally, and can restore sanitized responses on the user's machine.

## Documents

- `SPEC.md` - the main product specification.
- `MVP_IMPLEMENTATION_SPEC.md` - the first implementable slice produced from the specification.
- `POST_REFACTOR_IMPROVEMENT_SPEC.md` - the next improvement wave after the sanitizer pipeline split.
- `NEXT_IMPROVEMENT_SPEC.md` - product-readiness improvements around operations, packaging, audit, and smoke coverage.
- `PROJECT_FILE_WORKFLOW_SPEC.md` - product spec for protected coding-agent file reads, sanitized virtual files, and restore-aware local writes.
- `REQUIREMENTS.md` - functional, non-functional, and security requirements.
- `ARCHITECTURE.md` - target architecture, components, data flows, and integration options.
- `SANITIZER_DESIGN.md` - concrete sanitizer design: API, pipeline, policy, span replacement, and verification.
- `PROMPT_INTERCEPTION.md` - prompt interception modes, guard mode, gateway mode, and why hook-only blocking is not transparent replacement.
- `POLICY_MODEL.md` - sensitivity policy, policy layers, dictionaries, allowlists/blocklists, and manual additions.
- `EXISTING_SOLUTIONS_REVIEW.md` - review of reusable open-source sanitizer and secret-scanner projects.
- `IMPLEMENTATION_READINESS_REVIEW.md` - pre-implementation questions and non-blocking decisions.
- `IMPLEMENTATION_CLARIFICATIONS.md` - resolved decisions about runtime, Gitleaks packaging, timeouts, fail-closed behavior, CSV/TOML, vault/DPAPI, UI handoff, audit, attachments, and cleanup.
- `THREAT_MODEL.md` - protection boundaries, threats, and explicit non-goals.
- `GRILL_REVIEW.md` - design stress-test notes, risks, and decisions.
- `PROJECT_FILE_WORKFLOW_GRILL_REVIEW.md` - stress-test notes for coding-agent project file reads, sanitized virtual files, and restore-aware writes.
- `GLOSSARY.md` - domain model and terminology.
- `adr/` - architecture decision records.
- `spikes/tool-evaluation/` - small local spike fixtures and reproducible tool-evaluation notes.

## Core Idea

Users should not have to run separate scripts manually. In the normal workflow, input passes through a local redaction gate before it reaches the cloud:

1. The user writes a prompt as usual.
2. The gate analyzes the text before submission.
3. If sensitive data is found, the gate presents a sanitized version and a replacement summary.
4. Only sanitized text is sent to the cloud.
5. The response comes back in sanitized form.
6. The user can restore real values locally from the local mapping table.

## Important Limit

If the Codex integration point can only block a prompt and cannot rewrite it before submission, the product must use a layer in front of the composer: a local composer, desktop overlay, clipboard/keyboard fallback, or an official extension point that can change the submitted text. Protection is not complete if the original prompt has already been sent to the cloud.
