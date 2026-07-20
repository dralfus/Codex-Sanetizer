# ADR-002: Policy-As-Data For Sensitive Rules

## Status

Accepted

## Context

The sanitizer cannot know every organization-specific sensitive value from code. Built-in detectors can catch tokens, keys, URLs, private IPs, emails, and connection strings, but customer names, product names, project names, supplier names, internal system names, and local naming conventions must be configurable.

Users also need a fast way to mark newly discovered values as sensitive during normal work.

## Decision

Represent sensitivity rules as local policy files and dictionaries, layered over built-in detectors:

```text
built-in defaults
  -> global user policy
  -> organization policy
  -> project policy
  -> temporary session policy
```

The MVP supports:

- built-in pattern detectors;
- CSV dictionaries for exact business terms;
- TOML policy for allowlists, blocklists, domain suffixes, URL prefixes, and custom regex rules;
- manual additions through CLI and later UI;
- strict conflict resolution where secrets and global blocklists override weaker allow rules.

## Consequences

Positive:

- Users can add sensitive values without code changes.
- Security teams can review policy separately from sanitizer code.
- Project-specific false positives can be tuned without weakening global secrets policy.
- Policy files become testable artifacts.

Negative:

- Policy files may themselves contain sensitive business names and must be treated as local sensitive artifacts.
- Bad allowlists can weaken protection if validation is poor.
- Regex rules require guardrails and testing.

## Guardrails

- Secrets cannot be allowed by broad allowlist rules.
- Global blocklist overrides project allowlist.
- Invalid policy does not activate.
- Last known good policy is retained.
- Policy audit logs avoid raw prompt text and raw detected values.
- Manual additions are audited by rule id, type, action, and scope.
