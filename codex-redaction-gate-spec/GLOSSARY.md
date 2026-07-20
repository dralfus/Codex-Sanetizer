# Glossary

## Adapter

The integration layer that receives a prompt event from Codex, a local composer, browser, desktop overlay, or hook. It does not decide redaction policy itself; it enforces the sanitizer result.

## Audit Event

A local record of a sanitizer decision. It contains timestamps, counts, entity types, policy profile, and action taken. It must not contain raw prompts, original values, normalized values, or secrets.

## Candidate

A detector output that points to a span in the original text and describes a possible sensitive entity. Candidates may overlap and are not rendered directly.

## Cloud Boundary

The point after which prompt content leaves the local machine for Codex/OpenAI. The sanitizer must run before this boundary.

## Confirmation UI

The local screen that shows the sanitized prompt, counts by entity type, warnings, and send/cancel/edit actions. It should not reveal raw original values by default.

## Detector

A local module that finds one category of sensitive data, such as URLs, domains, secrets, emails, IP addresses, file paths, or dictionary terms.

## Dictionary

A local policy file containing exact or normalized sensitive terms such as customers, projects, products, domains, suppliers and internal systems.

## Guard Mode

An integration mode that can inspect and block unsafe prompts but cannot necessarily replace the prompt programmatically. It is useful as a baseline but is not the full target UX.

## Gateway Mode

An integration mode where the local system owns the submit action. It can sanitize, confirm, and submit only the sanitized prompt.

## Mapping Vault

The encrypted local store that maps normalized original identifiers to stable pseudonyms and can restore restorable pseudonyms locally.

## Non-Restorable Redaction

A replacement for secrets that should not be restored by default, such as `TOKEN_REDACTED` or `PASSWORD_REDACTED`.

## Normalized Value

The canonical local representation used for policy lookup and pseudonym generation. For example, a domain may be lowercased and an IP range may be canonicalized. Normalized values remain local.

## Policy Engine

The component that decides whether an entity should be allowed, pseudonymized, redacted, aliased for the session, confirmed, or blocked.

## Policy Layer

A scope at which rules are defined, such as built-in defaults, global user policy, organization policy, project policy, or temporary session policy.

## Pseudonym

A stable typed placeholder such as `URL_8F3A21B9` or `USERNAME_bright_turing_8F3A`. It preserves entity type and cross-prompt consistency without revealing the original value.

## Restoration

The local process that replaces restorable pseudonyms in a model answer with original values from the mapping vault. Restored output is sensitive local output.

## Sanitized Prompt

The prompt text after all approved replacements have been applied and verified. This is the only text that may cross the cloud boundary when sensitive data is detected.

## Sanitizer

The local engine that detects sensitive spans, resolves overlaps, applies policy, allocates replacements, renders sanitized text, verifies the result, and returns an allow/confirm/block decision.

## Span Resolver

The component that converts overlapping detector candidates into one final non-overlapping set of entities to replace or allow.

## Stable Pseudonymization

Replacement strategy where the same original value maps to the same pseudonym across sessions and projects, using a local HMAC secret and encrypted mapping vault.
