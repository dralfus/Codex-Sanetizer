# Implementation Clarifications

Date: 2026-07-17

This document records implementation clarifications made after the readiness review. Its goal is to remove ambiguity that could lead to unnecessary complexity or a false sense of protection.

## 1. Runtime and Packaging Options

The local orchestrator, adapter, and handoff UI need a runtime choice.

Options:

| Option | Pros | Cons | Fit |
| --- | --- | --- | --- |
| .NET | Strong Windows desktop/runtime story, WPF/WinUI, DPAPI, file storage APIs, single-file publish | Less portable outside Windows | Best option for the Windows-first MVP |
| Rust | Fast single binary, good CLI/daemon fit, strong typing | UI and Windows integration require more work | Good if the first slice is a headless engine/CLI |
| Go | Simple deployment, aligns well with the Gitleaks ecosystem | Desktop UI and DPAPI are less convenient than .NET | Acceptable for a service/CLI, weaker for UI |
| Python | Fast prototype path, convenient for Presidio/NLP | Heavy packaging, dependencies, startup, virtualenv | Not recommended as the primary MVP runtime |

Decision: use .NET for the Windows-first MVP orchestrator, file-based vault, local UI, and gateway/adapter layer. Rust remains acceptable if the product is later split into a headless CLI/library first, but the current primary path is .NET.

## 2. What "Gitleaks Distribution" Means

This is not about installing Git. It is about how this product obtains `gitleaks.exe`.

Options:

- Require Gitleaks on `PATH`: simple, but breaks when the user has no Gitleaks or has a different version.
- Build from source during install: poor MVP fit because it requires Go, Git, network access, and build caches on the user's machine.
- Build from source in our release process: more reliable and reproducible; the user receives an already-built binary.
- Download upstream release during setup: convenient, but requires network access and integrity checks.

Decision: build Gitleaks from source in the controlled release/build process from a pinned tag/commit, record source revision and binary checksum, and place the resulting `gitleaks.exe` into the runtime package. Normal installation and runtime must not require Go, Git, or Gitleaks source code.

## 3. Scanner Timeout and Fail-Closed Behavior

A timeout is required because a hook cannot hang forever. If scanning hangs, users will disable protection or prompt submission will become unpredictable.

Fail-closed means that if the system cannot reliably inspect potentially sensitive cloud-bound content, the original prompt is not submitted.

A 10-second hard cap is acceptable for worst-case input but too slow as the normal expectation for short prompts. Separate the target latency from the hard cap.

Practical MVP policy:

- normal prompt target: under 2 seconds;
- total hard cap: 10 seconds;
- Gitleaks timeout: up to 5 seconds within the total hard cap;
- built-in/dictionary/regex scanners: under 1 second combined, because they are local and should be fast;
- text attachments / large paste: may use the full 10-second hard cap;
- UI shows scanning/progress after 500 ms so the application does not look frozen;
- policy/vault/verifier error: `block`;
- scanner timeout on text with sensitive-looking markers: `block`;
- timeout on any cloud-bound content in MVP: `block`, because silently allowing content after a scanner error is worse than delay.

## 4. Why Not Store Everything in CSV

CSV is good for tabular dictionaries:

```csv
type,value,action,notes
customer,Contoso,pseudonymize,manual
domain,corp.example,block,internal
```

CSV is a poor fit for general policy:

- regex rules contain commas, backslashes, and quotes;
- nested structures are needed: allowlists, blocklists, precedence, scanner timeouts, severity, restorable flags;
- profile-level defaults are needed;
- comments and readability matter;
- large column sets quickly become brittle.

Recommendation: use CSV for dictionaries and TOML for policy/config. If a single format is required, TOML-only is better than CSV-only. CSV-only is not recommended for non-tabular policy.

## 5. Do We Really Need Databases, and What Is DPAPI

Local storage is needed because of the mapping table:

```text
real internal URL -> URL_8F3A21B9
real customer name -> CUSTOMER_31AD9910
```

This table is required so that:

- the same original value always receives the same pseudonym;
- Codex responses can be restored locally;
- mappings survive across sessions;
- lookup and reverse lookup remain fast and do not corrupt the file after a crash.

A database is not required for the MVP. A file-based vault is simpler and more honest for a small local tool.

| Storage | Pros | Cons |
| --- | --- | --- |
| Plaintext JSON/CSV | Simplest and easy to inspect manually | Easy to paste accidentally, commit, sync, or include in a support bundle |
| DPAPI-protected JSON | No database, simple deployment, unreadable outside the user/machine context | Weaker for parallel writes and large volumes; requires atomic writes |
| SQLite | Reliable lookup/update, indexes, migrations, lower file-corruption risk | Subjectively heavier for a small desktop tool |

Decision: no database in the MVP. Use a file-based vault:

- `vault.json` or `vault.jsonl` for mappings;
- atomic write through temp file + replace;
- in-memory indexes at startup;
- DPAPI-protected encryption/HMAC secret;
- keep SQLite as a future upgrade if concurrency, migration, or performance needs appear.

Why a plaintext file next to the vault is bad even though the main threat is cloud leakage rather than the local user:

- the vault contains exactly the real values that must not be sent: internal URLs, customer names, IPs, paths, emails;
- a plaintext file is easy to paste into a prompt while debugging or attach as "config";
- backup/sync/indexing/support export may pick it up;
- if the HMAC secret is plaintext next to the vault, pseudonyms can be recomputed and linked across exports.

A DPAPI-protected secret is a local secret encrypted with the Windows Data Protection API. DPAPI allows storing an HMAC/encryption key so only the current Windows user/machine context can decrypt it. This is not meant to defend against the user; it is meant to prevent accidental plaintext leakage and simple vault copying to another machine.

Plaintext vault mode is acceptable only as an explicit dev/diagnostic mode with a strong warning, never as the default.

## 6. What UI Handoff Means

UI handoff is needed because the user must confirm the sanitized prompt before submission, and the original prompt must not reach the cloud.

UX decision: the primary button should be `Confirm sanitized prompt`, not `Copy sanitized prompt`.

This means the MVP adapter must be able to submit the sanitized prompt after confirmation. If a bare `UserPromptSubmit` hook cannot replace and submit the prompt, the UX needs a gateway/composer/desktop adapter that owns the submit action. Clipboard remains a fallback, not the happy path.

Flow:

1. The user presses Send in Codex.
2. The `UserPromptSubmit` hook receives the raw prompt before submission.
3. The local sanitizer detects sensitive data.
4. The hook returns `block` to Codex so the raw prompt does not reach the cloud.
5. The local Redaction Gate window shows the sanitized replacement.
6. The user reviews the summary and sanitized text.
7. The user presses `Confirm sanitized prompt`.
8. The adapter sends only the sanitized prompt to the cloud.

The window shows:

- sanitized prompt;
- inline highlights/diff for replaced spans;
- counts by type: secrets, URLs, IPs, domains, customer terms;
- warning when high-risk findings are present;
- scanner status;
- buttons: `Confirm sanitized prompt`, `Cancel`, later `Edit sanitized` / `Add rule`;
- for each replacement: type, pseudonym, action, risk level; raw value is hidden by default.

The window does not:

- write to the clipboard without an explicit fallback action;
- show raw secrets by default;
- put a long prompt in the hook block reason;
- store raw prompts in logs.

## 7. Audit Log Detail

The audit log is needed to verify that the gate works, but it must not become a leak database.

Allowed ordinary audit fields:

- timestamp;
- application / adapter;
- workspace or project fingerprint, not the full path by default;
- decision: `allow | confirm | block`;
- scanner names and statuses;
- counts by entity type;
- actions by type: `pseudonymized`, `redacted`, `blocked`;
- policy profile/version;
- warning codes;
- duration/timeout metadata;
- finding spans as offsets/length/type, without values;
- replacement IDs and pseudonyms, without originals;
- redacted scanner stderr/stdout metadata.

Forbidden by default:

- raw prompt;
- sanitized prompt;
- raw entity values;
- normalized values;
- Gitleaks `Secret`/`Match` values.

Keyed fingerprints may optionally be stored for duplicate debugging, but only if they are produced by a local HMAC and cannot restore the original value.

These fields are enough for most debugging: which scanner fired, where it fired, why policy selected an action, where a timeout/error happened, and why the verifier blocked the result. False negative/false positive analysis needs a separate local diagnostic flow where the user explicitly opens a sanitized/diff view and can export a redacted diagnostic bundle. Raw prompts are not written to persistent logs.

## 8. Attachments in MVP

Attachment coverage belongs in the MVP. Otherwise, a major risk remains: users can send large logs, config dumps, or reports as attachments or large pasted blocks.

MVP decision:

- text attachments and large pasted file contents must go through the same sanitizer pipeline as prompt text;
- adapter context must pass `content_source`: `prompt_text | clipboard | text_attachment | file_snippet | tool_output`;
- sanitizer result must return findings per source;
- unsupported binary attachments must not be silently allowed;
- unsupported binary/PDF/Office/image attachments are either blocked or require an explicit local warning and conversion/extraction before cloud submission.

Not required in the first MVP:

- full PDF/Office parser;
- OCR for images;
- recursive archive scanning;
- enterprise DLP classification for arbitrary documents.

Main rule: "cannot read the attachment" does not mean "safe to send".

## 9. Spike Artifact Cleanup

Large temporary spike artifacts do not belong in the durable spec package.

Keep:

- written spike report;
- small corpus and policy fixtures;
- scripts used to reproduce key checks;
- pinned `bin/gitleaks.exe` if it is intentionally used by future local tests.

Remove:

- `.venv`;
- Go build/module caches;
- cloned third-party source trees;
- failed build outputs.

The previous cleanup removed heavy caches/source trees. The spike directory keeps only small fixtures, scripts, report, and `bin` when intentionally pinned.
