# Policy Model

## Purpose

The sanitizer needs a clear answer to three questions:

1. What counts as sensitive?
2. What replacement action should be used?
3. Who can add or override rules?

The answer should be policy-as-data. Built-in detectors catch common patterns, while local policy files and dictionaries define organization-specific values.

## Policy Layers

Policies are evaluated from broad to narrow, with stricter decisions winning:

```text
built-in defaults
  -> global user policy
  -> organization policy
  -> project policy
  -> temporary session policy
```

Suggested locations:

```text
%USERPROFILE%\.codex-redaction-gate\policy\defaults.toml
%USERPROFILE%\.codex-redaction-gate\policy\global.toml
%USERPROFILE%\.codex-redaction-gate\policy\dictionaries\
<project>\.codex-redaction-gate\policy.toml
<project>\.codex-redaction-gate\dictionaries\
```

Project-local policy is optional. Global policy must work without any git repository.

## Sensitivity Sources

The sanitizer should combine several sources of evidence:

### Pattern Detectors

Built-in code detects high-signal shapes:

- API keys, bearer tokens, JWTs, cookies;
- private keys and certificates;
- password assignments;
- URLs, domains, hostnames;
- private IPs, CIDRs, internal ports;
- emails and user identifiers;
- connection strings;
- Windows, Linux, and repository paths.

Pattern detectors are good for secrets and infrastructure identifiers, but they do not know company-specific names.

### Dictionaries

Dictionaries define exact or normalized business terms:

- company names;
- customer names;
- product names;
- project names;
- supplier names;
- internal system names;
- domain suffixes;
- internal repository names.

Dictionaries are the main mechanism for manual additions.

### Custom Regex Rules

Custom regex rules cover local naming conventions that generic detectors cannot know:

- ticket prefixes;
- environment names;
- internal host naming patterns;
- project code formats;
- tenant IDs.

Regex rules must be reviewed carefully because bad regexes can create either false positives or missed sensitive data.

### Context Rules

Some values are sensitive only in context:

- `admin` may be harmless text, but sensitive as `username=admin` in a connection string;
- a public domain may be safe alone, but sensitive inside a private callback URL;
- a file path may be sensitive when it exposes a user profile or internal project name.

Context rules should be represented as detector evidence plus policy conditions, not as ad hoc UI logic.

## Policy Actions

Each rule resolves to one of these actions:

```text
allow
pseudonymize_restorable
redact_non_restorable
session_alias
confirm
block
```

Default action matrix:

| Entity type | Default action | Restorable |
| --- | --- | --- |
| Public documentation URL | `allow` | No |
| Internal URL/domain/host | `pseudonymize_restorable` | Yes |
| Private IP/CIDR | `pseudonymize_restorable` | Yes |
| Email | `pseudonymize_restorable` | Yes |
| Customer/project/product name | `pseudonymize_restorable` | Yes |
| API token/password/private key/cookie | `redact_non_restorable` | No |
| Unknown high-risk match | `confirm` or `block` | Policy-defined |

Global blocklist rules override project allowlists for high-risk values.

## Example Policy File

```toml
version = 1
profile = "global"

[defaults]
unknown_high_risk = "confirm"
secret = "redact_non_restorable"
internal_identifier = "pseudonymize_restorable"

[[allow]]
type = "url"
match = "https://learn.microsoft.com/"
mode = "prefix"
reason = "public documentation"

[[sensitive]]
type = "domain"
match = "corp.example.local"
mode = "suffix"
action = "pseudonymize_restorable"
label = "internal domain"

[[sensitive]]
type = "customer"
match = "ACME Banking"
mode = "exact"
action = "pseudonymize_restorable"
label = "customer"

[[regex]]
type = "project"
pattern = "\\bPRJ-[0-9]{4,}\\b"
action = "pseudonymize_restorable"
label = "internal project code"

[[block]]
type = "secret"
pattern = "-----BEGIN (RSA |EC |OPENSSH )?PRIVATE KEY-----"
action = "redact_non_restorable"
label = "private key"
```

## Example Dictionary File

```csv
type,value,action,notes
customer,ACME Banking,pseudonymize_restorable,Known customer
product,Internal Risk Portal,pseudonymize_restorable,Internal product
project,Blue Falcon,pseudonymize_restorable,Internal project
domain,corp.example.local,pseudonymize_restorable,Internal DNS suffix
```

CSV is easy to edit manually. TOML is better for advanced metadata. The MVP uses CSV dictionaries plus one TOML policy file.

## Manual Additions

Manual additions are required. Users must be able to add sensitive values without code changes.

Required operations:

- add exact sensitive term;
- add domain suffix;
- add URL prefix;
- add customer/product/project name;
- add custom regex;
- add temporary session-only sensitive term;
- add public allowlist entry;
- test a rule against sample text before saving.

Suggested CLI:

```text
redaction-gate policy add customer "ACME Banking"
redaction-gate policy add domain "*.corp.example.local"
redaction-gate policy add project "Blue Falcon" --scope project
redaction-gate policy add-regex project "\bPRJ-[0-9]{4,}\b"
redaction-gate policy test "connect to https://deploy.corp.example.local"
```

Suggested UI:

- "Mark as sensitive" from confirmation screen;
- "Always allow this public URL";
- "Add dictionary term";
- "Add session-only alias";
- "Review policy changes".

Manual changes must be audited without recording raw prompt text.

## Rule Evaluation

Suggested evaluation order:

1. Detect hard secrets.
2. Detect explicit blocklist and dictionary terms.
3. Detect structured infrastructure identifiers.
4. Apply public allowlists.
5. Apply project overlays.
6. Resolve conflicts with strictest-action-wins.
7. Ask for confirmation only when policy says the user can decide.

Secrets should not become allowed through a broad allowlist.

## Policy Validation

Policy files should be validated before activation:

- schema version is supported;
- regexes compile;
- actions are known;
- duplicate exact terms are reported;
- broad allowlists are warned;
- blocklist conflicts are shown;
- sample test cases pass.

Invalid policy means the sanitizer should keep using the last known good policy. If no last known good policy exists and sensitive-looking content is detected, fail closed.

## Audit

Policy audit events should include:

- timestamp;
- actor: local user, managed policy, import, UI action;
- operation: add, update, remove, test, import;
- rule id;
- entity type;
- action;
- scope;
- keyed fingerprint of value when needed;
- no raw sensitive value by default.

For local usability, the UI may show the raw value during the edit session, but audit storage should not persist it in plain text unless the policy file itself is intentionally a local sensitive dictionary.

## Deferred Policy Questions

- Should organization-managed policy be signed?
- Should project-local policy be allowed to weaken global policy?
- How should policies be migrated between machines without exporting the mapping vault?

MVP decision: keep policy files editable and local, treat dictionary CSV and TOML policy files as sensitive local artifacts, and never export them automatically. Encrypted policy editing UI can be revisited after the sanitizer MVP works.
