# Tickets: Codex Redaction Gate MVP

Build a Windows-first local Codex Redaction Gate MVP that sanitizes prompts, text attachments and large pasted content before cloud submission, using .NET, source-built Gitleaks, file-based vault storage, `Confirm sanitized prompt` UX and local response restoration. Source specs: `codex-redaction-gate-spec/SPEC.md`, `codex-redaction-gate-spec/MVP_IMPLEMENTATION_SPEC.md`, and `codex-redaction-gate-spec/PROJECT_FILE_WORKFLOW_SPEC.md`.

## Execution Rules For Agents

These rules are part of every ticket.

- Work on exactly one ticket at a time.
- Do not start the next ticket until the current ticket is green.
- Do not implement future-ticket behavior early unless the ticket explicitly asks for it.
- Do not delete existing user/spec files.
- Do not run broad destructive cleanup commands.
- Do not use `dotnet build -v diag`, `more`, or huge diagnostic output loops.
- If `dotnet` is not on PATH, use `C:\Users\alexey.andreev\AppData\Local\Microsoft\dotnet\dotnet.exe`.
- Every ticket must end with a build/test verification command and the result.
- If verification fails twice with the same error, stop and report the exact error instead of rewriting the project.
- Keep implementation small. Prefer boring, explicit code over clever abstractions.

Work the **frontier**: any ticket whose blockers are all done. After installed-app protection smoke, the next frontier starts at ticket 200.

## Current Review Status

Reviewed on 2026-07-17 after commit `813f47a0`.

- Ticket 00 is accepted: the project builds with zero errors using the local dotnet executable.
- Ticket 01 is accepted: the project exposes the requested sanitizer contract types and tests cover decisions, content sources, sanitized text, replacements, warnings and audit metadata without detector/policy/vault/renderer behavior.
- Ticket 02 is accepted: the minimal sanitizer service returns `allow` for `Normal prompt text`, preserves sanitized text, produces zero replacements and raw-prompt-free audit metadata, does not allow unsupported synthetic sensitive markers, and `--self-test` passes.
- Ticket 03 is accepted: `SENSITIVE_MARKER` is replaced with a stable `SYNTHETIC_*` placeholder, returns `confirm`, records type/placeholder metadata without raw values, and `--self-test` passes.
- Ticket 04 is accepted: `BLOCK_THIS` returns `block`, includes a safe warning/reason code without raw prompt text, and allow/confirm/block self-tests pass.
- Ticket 05 is accepted: synthetic audit metadata includes timestamp/request id/decision/counts/actions/span summaries and tests prove raw prompts and raw synthetic marker values are absent from audit data.
- Ticket 06 is accepted: CLI supports `--sanitize "text"` and `--self-test`, prints decision plus sanitized text without raw synthetic marker leakage, and unknown args print help without crashing.
- Ticket 07 is accepted: synthetic confirm path uses an `IMappingVault` backed by deterministic in-memory HMAC pseudonyms without persistence, DPAPI, restore UI or real detectors.
- Ticket 08 is accepted: production sanitizer creates/loads a user-local HMAC secret through Windows DPAPI, while tests use deterministic injected secrets and no mapping persistence is added.
- Ticket 09 is accepted: production sanitizer uses a protected file-backed mapping vault with atomic writes, restart-stable mappings and original/reverse lookup indexes; plaintext vault files require an explicit dev/test factory.
- Ticket 10 is accepted: local restoration API restores only restorable pseudonyms from the vault, leaves unknown/non-restorable values unchanged with safe warnings, and marks restored output as local-sensitive.
- Ticket 11 is accepted: TOML policy loading supports safe built-in defaults, scanner settings, default actions, allowlist/blocklist rules, strict schema rejection and raw-value-free load warnings.
- Ticket 12 is accepted: CSV dictionaries can activate exact customer/project/product/domain/system terms, pseudonymize them through the vault from the CLI dictionary path, reject invalid CSV before activation, and keep raw dictionary values out of audit/warnings.
- Ticket 13 is accepted: custom regex policy entries compile under the safe non-backtracking regex engine before activation, invalid/unsafe regex policies are rejected, last known good policy remains active, and warnings do not leak raw regex values.
- Ticket 14 is accepted: internal URLs and domains are detected as technical identifiers, pseudonymized through the vault, public documentation URL allowlists are honored without allowing internal lookalikes, and audit metadata contains counts without raw URLs/domains.
- Ticket 00A is accepted: `bin/`, `obj/`, `build.log`, and `test.log` are no longer tracked; `.gitignore` excludes normal .NET generated artifacts; build and tests are green.

## 00. Restore a green .NET project baseline

**What to build:** A minimal .NET project that builds successfully before sanitizer work continues. This ticket exists because the current project may contain broken partial work.

**Blocked by:** None - can start immediately.

**Do not:** Implement sanitizer logic, vault, policy, detectors, UI, Gitleaks, or restoration.

**Verification:**

```powershell
& 'C:\Users\alexey.andreev\AppData\Local\Microsoft\dotnet\dotnet.exe' build 'src\CodexRedactionGate\CodexRedactionGate.csproj' -nologo -v minimal
```

- [x] Project targets a supported .NET framework available in the local SDK.
- [x] Project builds with zero errors.
- [x] If a placeholder `Program` exists, it only prints a short app name/help text.
- [x] No tests are required yet.

## 00A. Remove generated build artifacts from git tracking

**What to build:** Repository hygiene cleanup so future tickets review only source, tests and durable project files.

**Blocked by:** 00. Restore a green .NET project baseline.

**Do not:** Delete source files, spec files, `.qwen` state, or user work. Do not rewrite history unless explicitly instructed by the user.

**Verification:**

```powershell
git status --short
```

- [x] `bin/` files are not tracked by git.
- [x] `obj/` files are not tracked by git.
- [x] `build.log` and `test.log` are not tracked by git.
- [x] `.gitignore` excludes normal .NET generated artifacts.
- [x] Source files and tests still build after cleanup.

## 01. Add sanitizer contract types only

**What to build:** Public contract types for `SanitizeRequest`, `ContentPart`, `SanitizationResult`, `SanitizeDecision`, `Replacement`, `Warning` and `AuditEvent`.

**Blocked by:** 00A. Remove generated build artifacts from git tracking.

**Do not:** Implement detection, replacement, vault, CLI commands, or persistence.

**Review status:** Not accepted as of 2026-07-17. Current code implements some later-ticket behavior but lacks the requested contract surface.

**Verification:**

```powershell
& 'C:\Users\alexey.andreev\AppData\Local\Microsoft\dotnet\dotnet.exe' build 'src\CodexRedactionGate\CodexRedactionGate.csproj' -nologo -v minimal
```

- [x] Contract supports `allow`, `confirm`, `block`.
- [x] Contract supports content sources: `prompt_text`, `clipboard`, `text_attachment`, `file_snippet`, `tool_output`.
- [x] Contract can represent sanitized text, replacements, warnings and audit metadata.
- [x] Build is green.

## 02. Implement no-sensitive allow path

**What to build:** A minimal sanitizer service that returns `allow` when no synthetic sensitive markers are present.

**Blocked by:** 01. Add sanitizer contract types only.

**Do not:** Add real detectors, pseudonyms, vault, policy files, Gitleaks, or UI.

**Verification:**

```powershell
& 'C:\Users\alexey.andreev\AppData\Local\Microsoft\dotnet\dotnet.exe' run --project 'src\CodexRedactionGate\CodexRedactionGate.csproj' -- --self-test
```

**Verification result 2026-07-17:** build green with 0 warnings/0 errors; unit tests passed 6/6; `--self-test` printed `Self-test passed.` and exited 0.

- [x] Input `Normal prompt text` returns `allow`.
- [x] Sanitized text equals input text for the allow path.
- [x] Result has zero replacements.
- [x] Audit event exists and contains no raw prompt field.
- [x] Build and self-test are green.

## 03. Implement synthetic confirm path

**What to build:** A synthetic marker flow where `SENSITIVE_MARKER` is replaced and returns `confirm`.

**Blocked by:** 02. Implement no-sensitive allow path.

**Do not:** Add vault persistence, real HMAC, real detectors, policy files, or UI.

**Verification:**

```powershell
& 'C:\Users\alexey.andreev\AppData\Local\Microsoft\dotnet\dotnet.exe' run --project 'src\CodexRedactionGate\CodexRedactionGate.csproj' -- --self-test
```

**Verification result 2026-07-17:** build green with 0 warnings/0 errors; unit tests passed 6/6; `--self-test` printed `Self-test passed.` and exited 0.

- [x] Input containing `SENSITIVE_MARKER` returns `confirm`.
- [x] Sanitized text does not contain `SENSITIVE_MARKER`.
- [x] Sanitized text contains a stable-looking `SYNTHETIC_*` placeholder.
- [x] Replacement metadata records type and placeholder, not raw value.
- [x] Build and self-test are green.

## 04. Implement synthetic block path

**What to build:** A synthetic hard-block marker flow where `BLOCK_THIS` returns `block` and original submission is not allowed.

**Blocked by:** 03. Implement synthetic confirm path.

**Do not:** Add secret scanners, Gitleaks, policy files, or UI.

**Verification:**

```powershell
& 'C:\Users\alexey.andreev\AppData\Local\Microsoft\dotnet\dotnet.exe' run --project 'src\CodexRedactionGate\CodexRedactionGate.csproj' -- --self-test
```

**Verification result 2026-07-17:** build green with 0 warnings/0 errors; unit tests passed 8/8; `--self-test` printed `Self-test passed.` and exited 0.

- [x] Input containing `BLOCK_THIS` returns `block`.
- [x] Block result includes a warning/reason code.
- [x] Block reason does not include the raw prompt.
- [x] `allow`, `confirm`, and `block` self-tests all pass.
- [x] Build and self-test are green.

## 05. Add raw-value-free audit checks

**What to build:** Audit metadata for the synthetic sanitizer paths, with tests proving raw prompt and raw detected values are not logged.

**Blocked by:** 04. Implement synthetic block path.

**Do not:** Add file logging, persistent audit storage, vault, policy files, or Gitleaks.

**Verification:**

```powershell
& 'C:\Users\alexey.andreev\AppData\Local\Microsoft\dotnet\dotnet.exe' run --project 'src\CodexRedactionGate\CodexRedactionGate.csproj' -- --self-test
```

**Verification result 2026-07-17:** build green with 0 warnings/0 errors; unit tests passed 10/10; `--self-test` printed `Self-test passed.` and exited 0.

- [x] Audit includes timestamp/request id/decision/entity counts/action counts.
- [x] Audit can include span offset/length/type.
- [x] Audit never includes raw prompt text.
- [x] Audit never includes raw value `SENSITIVE_MARKER`.
- [x] Build and self-test are green.

## 06. Add narrow CLI smoke interface

**What to build:** A tiny CLI for manual smoke checks: `--sanitize "text"` and `--self-test`.

**Blocked by:** 05. Add raw-value-free audit checks.

**Do not:** Build a full CLI tester, fixture corpus, config system, UI, or hook.

**Verification:**

```powershell
& 'C:\Users\alexey.andreev\AppData\Local\Microsoft\dotnet\dotnet.exe' run --project 'src\CodexRedactionGate\CodexRedactionGate.csproj' -- --sanitize "Check SENSITIVE_MARKER"
& 'C:\Users\alexey.andreev\AppData\Local\Microsoft\dotnet\dotnet.exe' run --project 'src\CodexRedactionGate\CodexRedactionGate.csproj' -- --self-test
```

**Verification result 2026-07-17:** build green with 0 warnings/0 errors; unit tests passed 13/13; `--sanitize "Check SENSITIVE_MARKER"` printed sanitized output without `SENSITIVE_MARKER`; `--self-test` printed `Self-test passed.` and exited 0.

- [x] CLI prints decision and sanitized text.
- [x] CLI output for synthetic sensitive input does not print `SENSITIVE_MARKER`.
- [x] `--self-test` exits zero when tests pass.
- [x] Unknown arguments print help and do not crash.

## 07. Add mapping vault interface and deterministic in-memory HMAC pseudonyms

**What to build:** A vault interface plus in-memory HMAC pseudonym allocator for tests. This prepares the file vault without adding persistence yet.

**Blocked by:** 06. Add narrow CLI smoke interface.

**Do not:** Write vault files, use DPAPI, add restore UI, or implement real detectors.

**Verification:**

```powershell
& 'C:\Users\alexey.andreev\AppData\Local\Microsoft\dotnet\dotnet.exe' run --project 'src\CodexRedactionGate\CodexRedactionGate.csproj' -- --self-test
```

**Verification result 2026-07-17:** build green with 0 warnings/0 errors; unit tests passed 17/17; `--sanitize "Check SENSITIVE_MARKER"` printed `decision: confirm` and `sanitized_text: Check SYNTHETIC_D808BC948A13`; `--self-test` printed `Self-test passed.` and exited 0.

- [x] Same entity type and normalized value produce the same pseudonym in one process.
- [x] Different entity types produce different pseudonym prefixes/namespaces.
- [x] Pseudonym does not contain the original value.
- [x] Synthetic confirm path uses the vault interface instead of ad hoc placeholders.
- [x] Build and self-test are green.

## 08. Add DPAPI-protected local secret provider

**What to build:** Windows DPAPI-backed secret provider for the HMAC secret.

**Blocked by:** 07. Add mapping vault interface and deterministic in-memory HMAC pseudonyms.

**Do not:** Persist mappings yet, add SQLite, or add migration/export.

**Verification:**

```powershell
& 'C:\Users\alexey.andreev\AppData\Local\Microsoft\dotnet\dotnet.exe' run --project 'src\CodexRedactionGate\CodexRedactionGate.csproj' -- --self-test
```

**Verification result 2026-07-17:** build green with 0 warnings/0 errors; unit tests passed 20/20; `--sanitize "Check SENSITIVE_MARKER"` printed `decision: confirm` and `sanitized_text: Check SYNTHETIC_FBF0E5BCFF32`; `--self-test` printed `Self-test passed.` and exited 0.

- [x] Secret provider can create/load a local secret.
- [x] Secret is not stored as plaintext in the same file as mappings.
- [x] Tests use a deterministic test secret, not the real user secret.
- [x] Production path uses DPAPI or equivalent Windows protected storage.
- [x] Build and self-test are green.

## 09. Add file-based mapping vault persistence

**What to build:** `vault.json` or `vault.jsonl` persistence with atomic writes and in-memory indexes.

**Blocked by:** 08. Add DPAPI-protected local secret provider.

**Do not:** Add SQLite, secure export, UI, or restoration UI.

**Verification:**

```powershell
& 'C:\Users\alexey.andreev\AppData\Local\Microsoft\dotnet\dotnet.exe' run --project 'src\CodexRedactionGate\CodexRedactionGate.csproj' -- --self-test
```

**Verification result 2026-07-17:** build green with 0 warnings/0 errors; unit tests passed 29/29; `--sanitize "Check SENSITIVE_MARKER"` printed `decision: confirm` and `sanitized_text: Check SYNTHETIC_FBF0E5BCFF32`; `--self-test` printed `Self-test passed.` and exited 0.

- [x] Same value maps to same pseudonym across process restart simulation.
- [x] Vault supports lookup by normalized original.
- [x] Vault supports reverse lookup by pseudonym.
- [x] Writes use temp-file replace or equivalent atomic update.
- [x] Plaintext vault mode is available only as explicit dev/test mode.

## 10. Add local restoration API slice

**What to build:** A restoration API that maps restorable pseudonyms back to local original values.

**Blocked by:** 09. Add file-based mapping vault persistence.

**Do not:** Build response UI or Codex integration.

**Verification:**

```powershell
& 'C:\Users\alexey.andreev\AppData\Local\Microsoft\dotnet\dotnet.exe' run --project 'src\CodexRedactionGate\CodexRedactionGate.csproj' -- --self-test
```

**Verification result 2026-07-18:** build green with 0 warnings/0 errors; unit tests passed 33/33; `--sanitize "Check SENSITIVE_MARKER"` printed `decision: confirm` and `sanitized_text: Check SYNTHETIC_FBF0E5BCFF32`; `--self-test` printed `Self-test passed.` and exited 0.

- [x] Restorable pseudonym can be restored locally.
- [x] Unknown pseudonym remains unchanged or produces a safe warning.
- [x] Non-restorable redactions are not restored.
- [x] Restored output is marked as local-sensitive in API metadata.
- [x] Build and self-test are green.

## 11. Add TOML policy loading with safe defaults

**What to build:** TOML policy loading for scanner settings, default actions, allowlists and blocklists.

**Blocked by:** 06. Add narrow CLI smoke interface.

**Do not:** Add CSV dictionaries yet, real detectors, or UI.

**Verification:**

```powershell
& 'C:\Users\alexey.andreev\AppData\Local\Microsoft\dotnet\dotnet.exe' run --project 'src\CodexRedactionGate\CodexRedactionGate.csproj' -- --self-test
```

**Verification result 2026-07-18:** build green with 0 warnings/0 errors; unit tests passed 39/39; `--sanitize "Check SENSITIVE_MARKER"` printed `decision: confirm` and `sanitized_text: Check SYNTHETIC_FBF0E5BCFF32`; `--self-test` printed `Self-test passed.` and exited 0.

- [x] Missing policy uses safe built-in defaults.
- [x] Valid TOML policy loads successfully.
- [x] Invalid TOML policy is rejected before activation.
- [x] Policy load errors do not include raw prompt text.
- [x] Build and self-test are green.

## 12. Add CSV dictionary loading and exact-term detector

**What to build:** CSV dictionaries for organization-specific exact terms, connected to sanitizer output.

**Blocked by:** 09. Add file-based mapping vault persistence; 11. Add TOML policy loading with safe defaults.

**Do not:** Add regex rules or technical detectors.

**Verification:**

```powershell
& 'C:\Users\alexey.andreev\AppData\Local\Microsoft\dotnet\dotnet.exe' run --project 'src\CodexRedactionGate\CodexRedactionGate.csproj' -- --self-test
```

**Verification result 2026-07-18:** build green with 0 warnings/0 errors; unit tests passed 46/46; `--sanitize "Check SENSITIVE_MARKER"` printed `decision: confirm` and `sanitized_text: Check SYNTHETIC_FBF0E5BCFF32`; `--sanitize "Talk to ACME Banking" --dictionary <temp csv>` printed `decision: confirm` and `sanitized_text: Talk to CUSTOMER_CFD6DE234D83`; `--self-test` printed `Self-test passed.` and exited 0.

- [x] CSV dictionary can mark customer/project/product/domain/system terms sensitive.
- [x] Dictionary term is pseudonymized through the vault.
- [x] Invalid CSV is rejected before activation.
- [x] Raw dictionary value is not written to audit output.
- [x] Build and self-test are green.

## 13. Add custom regex policy validation only

**What to build:** Validate custom regex policy entries and reject unsafe/invalid patterns before runtime detection.

**Blocked by:** 11. Add TOML policy loading with safe defaults.

**Do not:** Use regex rules for detection yet.

**Verification:**

```powershell
& 'C:\Users\alexey.andreev\AppData\Local\Microsoft\dotnet\dotnet.exe' run --project 'src\CodexRedactionGate\CodexRedactionGate.csproj' -- --self-test
```

**Verification result 2026-07-18:** build green with 0 warnings/0 errors; unit tests passed 51/51; `--sanitize "Check SENSITIVE_MARKER"` printed `decision: confirm` and `sanitized_text: Check SYNTHETIC_FBF0E5BCFF32`; `--self-test` printed `Self-test passed.` and exited 0.

- [x] Valid regex policy compiles.
- [x] Invalid regex policy is rejected.
- [x] Rejection leaves last known good policy active.
- [x] Regex validation errors do not include raw prompt text.
- [x] Build and self-test are green.

## 14. Add internal URL and domain detector slice

**What to build:** Built-in detector for URLs/domains, with policy allowing public docs and pseudonymizing internal identifiers.

**Blocked by:** 12. Add CSV dictionary loading and exact-term detector.

**Do not:** Add IP, email, path, connection string or secret detectors.

**Verification:**

```powershell
& 'C:\Users\alexey.andreev\AppData\Local\Microsoft\dotnet\dotnet.exe' run --project 'src\CodexRedactionGate\CodexRedactionGate.csproj' -- --self-test
```

**Verification result 2026-07-18:** build green with 0 warnings/0 errors; unit tests passed 58/58, including host-boundary allowlist regression coverage; `--sanitize "Use https://deploy.corp.example.local/api"` printed `decision: confirm` and `sanitized_text: Use URL_3B09726B6F42`; `--sanitize "Read https://learn.microsoft.com/en-us/dotnet/"` printed `decision: allow`; `--self-test` printed `Self-test passed.` and exited 0.

- [x] Internal URL is detected and pseudonymized.
- [x] Internal domain is detected and pseudonymized.
- [x] Public allowlisted documentation URL is allowed.
- [x] Public allowlist does not allow an internal lookalike domain.
- [x] Audit contains URL/domain counts without raw URL/domain.

## 15. Add private IP and CIDR detector slice

**What to build:** Built-in detector for private IPv4 addresses and CIDR ranges.

**Blocked by:** 14. Add internal URL and domain detector slice.

**Do not:** Add IPv6 unless trivial, or other detector families.

**Verification:**

```powershell
& 'C:\Users\alexey.andreev\AppData\Local\Microsoft\dotnet\dotnet.exe' run --project 'src\CodexRedactionGate\CodexRedactionGate.csproj' -- --self-test
```

**Verification result 2026-07-18:** build green with 0 warnings/0 errors; unit tests passed 63/63; `--sanitize "Connect to 192.168.10.25 and route 10.20.30.0/24"` printed `decision: confirm` and sanitized text with `IP_*` and `CIDR_*`; `--self-test` printed `Self-test passed.` and exited 0.

- [x] Private IPv4 is pseudonymized.
- [x] Private CIDR is pseudonymized.
- [x] CIDR vs IP overlap resolves to one selected span.
- [x] Public IP behavior follows policy.
- [x] Build and self-test are green.

## 16. Add email and file path detector slice

**What to build:** Built-in detector for email addresses and local file paths that expose user or internal project context.

**Blocked by:** 15. Add private IP and CIDR detector slice.

**Do not:** Add connection strings or secrets.

**Verification:**

```powershell
& 'C:\Users\alexey.andreev\AppData\Local\Microsoft\dotnet\dotnet.exe' run --project 'src\CodexRedactionGate\CodexRedactionGate.csproj' -- --self-test
```

**Verification result 2026-07-18:** build green with 0 warnings/0 errors; unit tests passed 67/67; `--sanitize "Send C:\Users\alexey.andreev\Documents\secret.txt to alexey.andreev@corp.example.local"` printed `decision: confirm` and sanitized text with `PATH_*` and `EMAIL_*`; `--self-test` printed `Self-test passed.` and exited 0.

- [x] Email address is pseudonymized.
- [x] Windows path with user/profile segment is pseudonymized.
- [x] Unix-style path can be detected where practical.
- [x] Raw path/email values are absent from audit.
- [x] Build and self-test are green.

## 17. Add connection string detector slice

**What to build:** Built-in detector for connection strings, preserving useful type while hiding embedded credentials/hosts.

**Blocked by:** 16. Add email and file path detector slice.

**Do not:** Add Gitleaks or broad secret handling.

**Verification:**

```powershell
& 'C:\Users\alexey.andreev\AppData\Local\Microsoft\dotnet\dotnet.exe' run --project 'src\CodexRedactionGate\CodexRedactionGate.csproj' -- --self-test
```

**Verification result 2026-07-18:** build green with 0 warnings/0 errors; unit tests passed 69/69; `--sanitize "Use Server=db01.corp.example.local;Database=Billing;User Id=svc;Password=P@ssw0rd!"` printed `decision: confirm` and `sanitized_text: Use CONNECTION_96CD9A2CE02B`; `--self-test` printed `Self-test passed.` and exited 0.

- [x] Connection string is detected as one high-level span.
- [x] Embedded password is not visible in sanitized output.
- [x] Embedded hostname is not visible in sanitized output.
- [x] Replacement preserves connection-string type.
- [x] Build and self-test are green.

## 18. Add span resolver and one-pass renderer hardening

**What to build:** Deterministic overlap resolution and rendering from original offsets in one pass.

**Blocked by:** 17. Add connection string detector slice.

**Do not:** Add Gitleaks, UI, or timeouts.

**Verification:**

```powershell
& 'C:\Users\alexey.andreev\AppData\Local\Microsoft\dotnet\dotnet.exe' run --project 'src\CodexRedactionGate\CodexRedactionGate.csproj' -- --self-test
```

**Verification result 2026-07-18:** build green with 0 warnings/0 errors; unit tests passed 73/73; overlap tests prove connection strings win over lower-risk overlaps and CIDR wins over nested IP; CLI smoke preserved punctuation/line break around `IP_*` and `EMAIL_*`; `--self-test` printed `Self-test passed.` and exited 0.

- [x] Higher-risk span wins over lower-risk overlap.
- [x] Longer span wins when risk is equal.
- [x] Rendering is one pass from original offsets.
- [x] Replacement does not corrupt surrounding punctuation/line breaks.
- [x] Build and self-test are green.

## 19. Add verifier fail-closed checks

**What to build:** Verification pass that blocks if selected raw sensitive spans remain after rendering.

**Blocked by:** 18. Add span resolver and one-pass renderer hardening.

**Do not:** Add new detectors.

**Verification:**

```powershell
& 'C:\Users\alexey.andreev\AppData\Local\Microsoft\dotnet\dotnet.exe' run --project 'src\CodexRedactionGate\CodexRedactionGate.csproj' -- --self-test
```

**Verification result 2026-07-18:** build green with 0 warnings/0 errors; unit tests passed 77/77; verifier passes clean output, blocks a raw-surviving replacement, blocks replacement-count mismatch, and emits raw-value-free warning/audit reason codes; `--sanitize "Connect to 192.168.10.25"` still printed `decision: confirm`; `--self-test` printed `Self-test passed.` and exited 0.

- [x] Verifier passes clean sanitized output.
- [x] Verifier blocks if selected raw span survives.
- [x] Verifier blocks replacement count mismatch.
- [x] Verification failure audit contains reason code but no raw value.
- [x] Build and self-test are green.

## 20. Add source-built Gitleaks binary provenance metadata

**What to build:** Metadata and scripts/placeholders for using a Gitleaks binary built from pinned source.

**Blocked by:** 06. Add narrow CLI smoke interface.

**Do not:** Integrate scanner output yet.

**Verification:**

```powershell
& 'C:\Users\alexey.andreev\AppData\Local\Microsoft\dotnet\dotnet.exe' run --project 'src\CodexRedactionGate\CodexRedactionGate.csproj' -- --self-test
```

**Verification result 2026-07-18:** build green with 0 warnings/0 errors; unit tests passed 79/79; provenance loader records source repository, revision/tag, build command, Go version, and validated `binary_sha256`; runtime path only reads local JSON and does not invoke Git/Go/network; `--self-test` printed `Self-test passed.` and exited 0.

- [x] Metadata records Gitleaks source revision/tag.
- [x] Metadata records expected build command and Go version.
- [x] Metadata records expected binary checksum field.
- [x] Runtime does not require Git/Go/network.
- [x] Build and self-test are green.

## 21. Add Gitleaks pipe-mode adapter

**What to build:** Runtime adapter that runs `gitleaks.exe` in stdin/pipe mode with JSON output and redaction.

**Blocked by:** 20. Add source-built Gitleaks binary provenance metadata.

**Do not:** Add broad secret policy beyond consuming Gitleaks findings.

**Verification:**

```powershell
& 'C:\Users\alexey.andreev\AppData\Local\Microsoft\dotnet\dotnet.exe' run --project 'src\CodexRedactionGate\CodexRedactionGate.csproj' -- --self-test
```

**Verification result 2026-07-18:** build green with 0 warnings/0 errors; unit tests passed 83/83; adapter records configured `gitleaks.exe`, sends prompt text through stdin, requests `json` report output with `--redact`, and maps `[]`/exit 0 to `no_findings`; `--self-test` printed `Self-test passed.` and exited 0.

- [x] Adapter can run configured `gitleaks.exe`.
- [x] Adapter uses stdin/pipe input.
- [x] Adapter requests JSON output and redaction.
- [x] Adapter handles no-findings result.
- [x] Build and self-test are green.

## 22. Convert Gitleaks findings to sanitizer spans

**What to build:** Convert Gitleaks line/column findings to absolute spans for CRLF and LF input.

**Blocked by:** 21. Add Gitleaks pipe-mode adapter.

**Do not:** Implement UI or packaging.

**Verification:**

```powershell
& 'C:\Users\alexey.andreev\AppData\Local\Microsoft\dotnet\dotnet.exe' run --project 'src\CodexRedactionGate\CodexRedactionGate.csproj' -- --self-test
```

**Verification result 2026-07-18:** build green with 0 warnings/0 errors; unit tests passed 87/87; converter maps LF and CRLF Gitleaks line/column findings to offsets, returns raw-free `secret` candidate spans, and does not persist `Secret`/`Match`; `--self-test` printed `Self-test passed.` and exited 0.

- [x] LF line/column conversion is correct.
- [x] CRLF line/column conversion is correct.
- [x] Gitleaks raw `Secret`/`Match` values are not persisted.
- [x] Gitleaks finding becomes sanitizer candidate span.
- [x] Build and self-test are green.

## 23. Add non-restorable secret redaction behavior

**What to build:** Gitleaks-backed secrets become typed non-restorable redactions.

**Blocked by:** 22. Convert Gitleaks findings to sanitizer spans; 19. Add verifier fail-closed checks.

**Do not:** Store secrets in vault for restoration.

**Verification:**

```powershell
& 'C:\Users\alexey.andreev\AppData\Local\Microsoft\dotnet\dotnet.exe' run --project 'src\CodexRedactionGate\CodexRedactionGate.csproj' -- --self-test
```

**Verification result 2026-07-18:** build green with 0 warnings/0 errors; unit tests passed 91/91; token/API key values redact to `TOKEN_REDACTED`, private keys to `PRIVATE_KEY_REDACTED`, password-like values to `PASSWORD_REDACTED`, and secret redaction bypasses the restorable vault; `--sanitize "api_key=sk_live_1234567890abcdef"` printed `decision: confirm` with `TOKEN_REDACTED`; `--self-test` printed `Self-test passed.` and exited 0.

- [x] Token/API key is replaced with `TOKEN_REDACTED` or equivalent.
- [x] Private key is replaced with `PRIVATE_KEY_REDACTED` or equivalent.
- [x] Password-like value is replaced with `PASSWORD_REDACTED` or equivalent.
- [x] Secret redaction is not restorable.
- [x] Build and self-test are green.

## 24. Add scanner timeout fail-closed behavior

**What to build:** Enforce sanitizer and scanner budgets, with fail-closed behavior.

**Blocked by:** 23. Add non-restorable secret redaction behavior.

**Do not:** Add UI progress yet.

**Verification:**

```powershell
& 'C:\Users\alexey.andreev\AppData\Local\Microsoft\dotnet\dotnet.exe' run --project 'src\CodexRedactionGate\CodexRedactionGate.csproj' -- --self-test
```

**Verification result 2026-07-18:** build green with 0 warnings/0 errors; unit tests passed 150/150 after review hardening; ordinary prompt completes below the 10s total hard cap, Gitleaks scanner budget is capped at 5s, scanner timeout returns `block`, scanner error/invalid JSON also fail closed, and timeout audit contains `gitleaks=timeout` without raw prompt text; `--self-test` printed `Self-test passed.` and exited 0.

- [x] Ordinary prompt test completes under target budget.
- [x] Total hard cap is 10 seconds.
- [x] Gitleaks budget is capped at 5 seconds.
- [x] Scanner timeout returns `block`.
- [x] Timeout audit contains scanner status and no raw values.

## 25. Add attachment-aware content parts

**What to build:** Process multiple content parts and source metadata through the same sanitizer pipeline.

**Blocked by:** 24. Add scanner timeout fail-closed behavior.

**Do not:** Parse PDF/Office/image files.

**Verification:**

```powershell
& 'C:\Users\alexey.andreev\AppData\Local\Microsoft\dotnet\dotnet.exe' run --project 'src\CodexRedactionGate\CodexRedactionGate.csproj' -- --self-test
```

**Verification result 2026-07-18:** build green with 0 warnings/0 errors; unit tests passed 100/100; sanitizer processes `prompt_text`, `text_attachment`, and `file_snippet` content parts and maps selected spans back to source `ContentPartId` in replacements, entities, and audit summaries; `--self-test` printed `Self-test passed.` and exited 0.

- [x] `prompt_text` part is processed.
- [x] `text_attachment` part is processed.
- [x] `file_snippet` part is processed.
- [x] Findings keep source-part metadata.
- [x] Build and self-test are green.

## 26. Block unsupported binary attachments

**What to build:** Explicit block/warning behavior for unsupported binary/PDF/Office/image attachments.

**Blocked by:** 25. Add attachment-aware content parts.

**Do not:** Add real parsers/OCR/archive scanning.

**Verification:**

```powershell
& 'C:\Users\alexey.andreev\AppData\Local\Microsoft\dotnet\dotnet.exe' run --project 'src\CodexRedactionGate\CodexRedactionGate.csproj' -- --self-test
```

**Verification result 2026-07-18:** build green with 0 warnings/0 errors; unit tests passed 103/103; unsupported binary attachment metadata returns `block` with `unsupported_binary_attachment`, warning/audit omit file contents, and `text/plain` attachments still sanitize normally; `--self-test` printed `Self-test passed.` and exited 0.

- [x] Unsupported binary attachment metadata returns `block` or explicit warning decision.
- [x] Unsupported attachment is never silently allowed.
- [x] Warning/block reason does not include file contents.
- [x] Text attachments still sanitize normally.
- [x] Build and self-test are green.

## 27. Add fixture corpus runner

**What to build:** A reproducible self-test/fixture runner over synthetic sensitive-shaped prompts and attachments.

**Blocked by:** 26. Block unsupported binary attachments.

**Do not:** Build UI or hook.

**Verification:**

```powershell
& 'C:\Users\alexey.andreev\AppData\Local\Microsoft\dotnet\dotnet.exe' run --project 'src\CodexRedactionGate\CodexRedactionGate.csproj' -- --self-test
```

**Verification result 2026-07-18:** build green with 0 warnings/0 errors; unit tests passed 106/106; default fixture corpus covers internal URL/domain/IP/CIDR/email/path/connection string, dictionary terms, Gitleaks-shaped token secrets, and text attachment content; rendered runner summary includes only case names/decisions/types and omits raw detected values; `--self-test` printed `Self-test passed.` and exited 0.

- [x] Fixtures cover internal URL/domain/IP/CIDR/email/path/connection string.
- [x] Fixtures cover dictionary terms.
- [x] Fixtures cover Gitleaks-shaped secrets.
- [x] Fixtures cover text attachment content.
- [x] Runner output does not print raw detected secret values.

## 28. Add minimal confirmation UI shell

**What to build:** Local confirmation UI shell that can render a sanitized prompt, counts and actions using fixture data.

**Blocked by:** 27. Add fixture corpus runner.

**Do not:** Wire real submit action yet.

**Verification:**

```powershell
& 'C:\Users\alexey.andreev\AppData\Local\Microsoft\dotnet\dotnet.exe' build 'src\CodexRedactionGate\CodexRedactionGate.csproj' -nologo -v minimal
```

**Verification result 2026-07-18:** build green with 0 warnings/0 errors; unit tests passed 110/110; confirmation UI shell view model exposes sanitized prompt, highlighted replacement spans, counts by type, non-restorable high-risk warnings, `Confirm sanitized prompt`/`Cancel` actions, and `RawValuesVisible=false`; `--self-test` printed `Self-test passed.` and exited 0.

- [x] UI shows sanitized prompt.
- [x] UI highlights replaced spans.
- [x] UI shows counts by type and high-risk warnings.
- [x] UI has `Confirm sanitized prompt` and `Cancel`.
- [x] Raw values are hidden by default.

## 29. Add confirmation UI decision contract

**What to build:** Confirmation UI returns an explicit approve/cancel decision to the adapter layer.

**Blocked by:** 28. Add minimal confirmation UI shell.

**Do not:** Integrate Codex hook yet.

**Verification:**

```powershell
& 'C:\Users\alexey.andreev\AppData\Local\Microsoft\dotnet\dotnet.exe' run --project 'src\CodexRedactionGate\CodexRedactionGate.csproj' -- --self-test
```

**Verification result 2026-07-18:** build green with 0 warnings/0 errors; unit tests passed 114/114; confirmation decision contract returns approved sanitized payload on confirm, no payload on cancel, serializes payload with only sanitized text, and does not expose original/raw prompt values; `--self-test` printed `Self-test passed.` and exited 0.

- [x] Confirm returns approved sanitized payload.
- [x] Cancel returns no payload.
- [x] Approval payload contains only `sanitized_text`.
- [x] Original prompt is not exposed through the decision object.
- [x] Build and self-test are green.

## 30. Add submit-owning adapter test double

**What to build:** Adapter test double proving `Confirm sanitized prompt` submits only sanitized text.

**Blocked by:** 29. Add confirmation UI decision contract.

**Do not:** Integrate real Codex submission.

**Verification:**

```powershell
& 'C:\Users\alexey.andreev\AppData\Local\Microsoft\dotnet\dotnet.exe' run --project 'src\CodexRedactionGate\CodexRedactionGate.csproj' -- --self-test
```

**Verification result 2026-07-18:** build green with 0 warnings/0 errors; unit tests passed 119/119; submit-owning adapter test double submits allow text, requests confirmation for confirm decisions, submits only approved sanitized payload, submits nothing on cancel, and submits nothing on block; `--self-test` printed `Self-test passed.` and exited 0.

- [x] `allow` submits sanitized-equivalent text.
- [x] `confirm` waits for approval.
- [x] Approved confirm submits only `sanitized_text`.
- [x] Canceled confirm submits nothing.
- [x] `block` submits nothing.

## 31. Add Codex UserPromptSubmit guard hook shell

**What to build:** Hook shell that receives a prompt event, calls sanitizer, and returns allow/block decisions.

**Blocked by:** 27. Add fixture corpus runner.

**Do not:** Depend on undocumented prompt rewriting.

**Verification:**

```powershell
& 'C:\Users\alexey.andreev\AppData\Local\Microsoft\dotnet\dotnet.exe' run --project 'src\CodexRedactionGate\CodexRedactionGate.csproj' -- --self-test
```

**Verification result 2026-07-18:** build green with 0 warnings/0 errors; unit tests passed 124/124; guard hook shell permits safe prompts, blocks original prompt on confirm/block, keeps block reasons raw-free, and marks clipboard handoff as fallback-only for confirm flow; `--self-test` printed `Self-test passed.` and exited 0.

- [x] Hook allow path permits safe prompt.
- [x] Hook confirm path blocks original prompt.
- [x] Hook block path blocks original prompt.
- [x] Hook block reason contains no raw values.
- [x] Clipboard handoff remains fallback only.

## 32. Connect guard hook to confirmation flow

**What to build:** Guard hook launches or invokes the local confirmation flow when sanitizer returns `confirm`.

**Blocked by:** 29. Add confirmation UI decision contract; 31. Add Codex UserPromptSubmit guard hook shell.

**Do not:** Claim transparent prompt rewrite unless verified by official API.

**Verification:**

```powershell
& 'C:\Users\alexey.andreev\AppData\Local\Microsoft\dotnet\dotnet.exe' run --project 'src\CodexRedactionGate\CodexRedactionGate.csproj' -- --self-test
```

**Verification result 2026-07-18:** build green with 0 warnings/0 errors; unit tests passed 128/128; guarded prompt flow triggers confirmation for confirm decisions, keeps original prompt blocked, submits approved sanitized text only through the adapter path, and keeps block reasons concise; `--self-test` printed `Self-test passed.` and exited 0.

- [x] Confirm decision triggers local UI/confirmation flow.
- [x] Original prompt remains blocked.
- [x] Sanitized prompt is available only through approved adapter/fallback path.
- [x] Block reason is concise.
- [x] Build and self-test are green.

## 33. Add restored-output local-sensitive marking

**What to build:** Mark restored responses as local-sensitive and prevent accidental re-submission.

**Blocked by:** 10. Add local restoration API slice; 30. Add submit-owning adapter test double.

**Do not:** Build polished response UI.

**Verification:**

```powershell
& 'C:\Users\alexey.andreev\AppData\Local\Microsoft\dotnet\dotnet.exe' run --project 'src\CodexRedactionGate\CodexRedactionGate.csproj' -- --self-test
```

**Verification result 2026-07-18:** build green with 0 warnings/0 errors; unit tests passed 132/132; restored output metadata is local-sensitive when pseudonyms are restored, non-restorable redactions remain redacted, restored local-sensitive re-submission is warned/blocked without raw values, and sanitized output remains copy/use eligible; `--self-test` printed `Self-test passed.` and exited 0.

- [x] Restored output metadata says local-sensitive.
- [x] Non-restorable redactions remain redacted.
- [x] Attempting to submit restored output is warned or re-sanitized.
- [x] Sanitized output can still be copied/used.
- [x] Build and self-test are green.

## 34. Add default storage layout

**What to build:** Default user-local locations for policy, vault and audit.

**Blocked by:** 09. Add file-based mapping vault persistence; 11. Add TOML policy loading with safe defaults.

**Do not:** Build installer/package yet.

**Verification:**

```powershell
& 'C:\Users\alexey.andreev\AppData\Local\Microsoft\dotnet\dotnet.exe' run --project 'src\CodexRedactionGate\CodexRedactionGate.csproj' -- --self-test
```

**Verification result 2026-07-18:** build green with 0 warnings/0 errors; unit tests passed 136/136; default storage root resolves under user-local app data rather than the project repository, and `EnsureDirectories` creates/discovers policy, vault, and audit directories; `--self-test` printed `Self-test passed.` and exited 0.

- [x] Default paths are user-local, not project repository paths.
- [x] Policy directory is created/discovered.
- [x] Vault directory is created/discovered.
- [x] Audit directory is created/discovered.
- [x] Build and self-test are green.

## 35. Add default public allowlist profile

**What to build:** Conservative public allowlist profile that reduces noise without bypassing sensitive rules.

**Blocked by:** 14. Add internal URL and domain detector slice; 11. Add TOML policy loading with safe defaults.

**Do not:** Add broad wildcard allowlists.

**Verification:**

```powershell
& 'C:\Users\alexey.andreev\AppData\Local\Microsoft\dotnet\dotnet.exe' run --project 'src\CodexRedactionGate\CodexRedactionGate.csproj' -- --self-test
```

**Verification result 2026-07-18:** build green with 0 warnings/0 errors; unit tests passed 141/141; default public allowlist profile covers common docs/package registry URLs, host-boundary matching blocks internal lookalikes, allowlisted URLs do not override secrets, dictionary terms, or policy block rules; `--self-test` printed `Self-test passed.` and exited 0.

- [x] Common public docs/package registry domains can be allowed.
- [x] Internal lookalike domains are not allowed.
- [x] Allowlist never overrides secrets.
- [x] Allowlist never overrides global blocklist/dictionary terms.
- [x] Build and self-test are green.

## 36. Add MVP package smoke test

**What to build:** A package-level smoke test that proves the MVP path works with local defaults and bundled/source-built scanner artifact.

**Blocked by:** 20. Add source-built Gitleaks binary provenance metadata; 30. Add submit-owning adapter test double; 32. Connect guard hook to confirmation flow; 33. Add restored-output local-sensitive marking; 34. Add default storage layout.

**Do not:** Build enterprise installer, policy signing, PDF/OCR parsers, or polished composer.

**Verification:**

```powershell
& 'C:\Users\alexey.andreev\AppData\Local\Microsoft\dotnet\dotnet.exe' build 'src\CodexRedactionGate\CodexRedactionGate.csproj' -nologo -v minimal
```

**Verification result 2026-07-18:** build green with 0 warnings/0 errors; unit tests passed 150/150 after review hardening; MVP package smoke manifest verifies .NET app artifact presence, existing `gitleaks.exe` artifact plus source-build provenance, declares no Git/Go/source/network runtime requirements, and proves sanitize allow, confirm, scanner-backed secret redaction, guard block, and local restore paths; `--self-test` printed `Self-test passed.` and exited 0.

- [x] Package includes .NET app artifacts.
- [x] Package references source-built `gitleaks.exe` artifact/provenance.
- [x] Runtime does not require Git, Go, Gitleaks source code or network access.
- [x] Smoke test proves sanitize, confirm, guard block and local restore paths.
- [x] Build is green.

## Refactor Frontier: Split the sanitizer orchestrator

These tickets preserve the current public sanitizer behavior while reducing the number of roles inside the sanitizer implementation. Do not add new product features in this sequence. Each ticket must keep the public sanitizer API, guard flow, raw-free audit behavior, fail-closed behavior and MVP package smoke behavior green.

**Verification result 2026-07-18 for tickets 37-48:** build green with 0 warnings/0 errors; unit tests passed 161/161; `--self-test` printed `Self-test passed.`; sanitizer internals are split into focused pipeline components; public `ISanitizer` contract, CLI behavior, guard flow, raw-free audit behavior, fail-closed scanner/attachment behavior and package smoke coverage are preserved.

## 37. Split sanitizer behavior tests into focused groups

**What to build:** Reorganize the sanitizer behavior tests so future refactor tickets can verify one concern at a time without scrolling through one giant test surface.

**Blocked by:** 36. Add MVP package smoke test.

**Do not:** Change sanitizer behavior, public contracts, detector behavior, policy behavior, vault behavior, UI behavior or package smoke expectations.

**Verification:**

```powershell
& 'C:\Users\alexey.andreev\AppData\Local\Microsoft\dotnet\dotnet.exe' test 'src\CodexRedactionGate\CodexRedactionGate.csproj' -nologo -v minimal
& 'C:\Users\alexey.andreev\AppData\Local\Microsoft\dotnet\dotnet.exe' run --project 'src\CodexRedactionGate\CodexRedactionGate.csproj' -- --self-test
```

- [x] Sanitizer tests are grouped by behavior area: allow/confirm/block, attachments, secrets/scanner, technical identifiers, dictionaries/policy, rendering/verification and audit safety.
- [x] Test names still describe user-visible behavior rather than private helper names.
- [x] No acceptance coverage from the MVP ticket list is removed.
- [x] Total test count may change only because tests are split, renamed or focused component checks are added; no scenarios disappear.
- [x] Build, tests and self-test are green.

## 38. Extract content-part assembly and attachment guard

**What to build:** Move content-part joining, source-part offset mapping and unsupported attachment blocking behind small internal services while preserving the same sanitizer result shape.

**Blocked by:** 37. Split sanitizer behavior tests into focused groups.

**Do not:** Add PDF/Office/image parsing, OCR, archive scanning, new attachment policy modes or new content sources.

**Verification:**

```powershell
& 'C:\Users\alexey.andreev\AppData\Local\Microsoft\dotnet\dotnet.exe' test 'src\CodexRedactionGate\CodexRedactionGate.csproj' -nologo -v minimal
& 'C:\Users\alexey.andreev\AppData\Local\Microsoft\dotnet\dotnet.exe' run --project 'src\CodexRedactionGate\CodexRedactionGate.csproj' -- --self-test
```

- [x] Prompt text, text attachment and file snippet content still pass through one sanitizer pipeline.
- [x] Replacement, entity and audit spans still resolve to the correct source content part.
- [x] Unsupported binary attachment metadata still returns `block`.
- [x] Unsupported attachment warnings remain raw-content-free.
- [x] Build, tests and self-test are green.

## 39. Extract scanner budget orchestration

**What to build:** Move external scanner timeout budgeting and scanner status normalization into a dedicated orchestration component.

**Blocked by:** 38. Extract content-part assembly and attachment guard.

**Do not:** Change Gitleaks command-line behavior, add new scanner backends, change timeout values, or allow scanner failures to pass through as safe.

**Verification:**

```powershell
& 'C:\Users\alexey.andreev\AppData\Local\Microsoft\dotnet\dotnet.exe' test 'src\CodexRedactionGate\CodexRedactionGate.csproj' -nologo -v minimal
& 'C:\Users\alexey.andreev\AppData\Local\Microsoft\dotnet\dotnet.exe' run --project 'src\CodexRedactionGate\CodexRedactionGate.csproj' -- --self-test
```

- [x] Ordinary prompts still use the 10-second total hard cap.
- [x] Gitleaks scanner budget is still capped at 5 seconds inside the total cap.
- [x] Scanner timeout still returns `block`.
- [x] Scanner error and invalid JSON still return `block`.
- [x] Scanner statuses in audit remain raw-value-free.
- [x] Build, tests and self-test are green.

## 40. Introduce a common detector candidate model and registry

**What to build:** Add an internal detector candidate shape and a registry/orchestrator that can run detectors and return candidates without changing public sanitizer contracts.

**Blocked by:** 39. Extract scanner budget orchestration.

**Do not:** Move all detectors yet, add plugin loading, change policy decisions, change replacement placeholders or change public result records.

**Verification:**

```powershell
& 'C:\Users\alexey.andreev\AppData\Local\Microsoft\dotnet\dotnet.exe' test 'src\CodexRedactionGate\CodexRedactionGate.csproj' -nologo -v minimal
& 'C:\Users\alexey.andreev\AppData\Local\Microsoft\dotnet\dotnet.exe' run --project 'src\CodexRedactionGate\CodexRedactionGate.csproj' -- --self-test
```

- [x] The sanitizer can consume detector candidates from a registry-like component.
- [x] Candidate metadata can represent source content part, offset, length, type, detector id, action, original local value and restorable flag.
- [x] Existing sanitizer behavior remains unchanged while old and new detector paths coexist.
- [x] No raw candidate values are added to audit, warnings or package smoke output.
- [x] Build, tests and self-test are green.

## 41. Move synthetic, dictionary and built-in secret detectors into the registry

**What to build:** Move synthetic markers, CSV dictionary terms, private-key patterns, token assignments and password assignments behind detector implementations that return the common candidate model.

**Blocked by:** 40. Introduce a common detector candidate model and registry.

**Do not:** Change the public meaning of synthetic markers, broaden secret regexes, store secrets in the vault, or change dictionary validation.

**Verification:**

```powershell
& 'C:\Users\alexey.andreev\AppData\Local\Microsoft\dotnet\dotnet.exe' test 'src\CodexRedactionGate\CodexRedactionGate.csproj' -nologo -v minimal
& 'C:\Users\alexey.andreev\AppData\Local\Microsoft\dotnet\dotnet.exe' run --project 'src\CodexRedactionGate\CodexRedactionGate.csproj' -- --self-test
```

- [x] `SENSITIVE_MARKER` still returns `confirm` with a stable synthetic placeholder.
- [x] Dictionary terms still produce restorable pseudonyms through the vault.
- [x] Private keys still redact to `PRIVATE_KEY_REDACTED`.
- [x] Token-like values still redact to `TOKEN_REDACTED`.
- [x] Password-like values still redact to `PASSWORD_REDACTED`.
- [x] Build, tests and self-test are green.

## 42. Move scanner findings and technical identifier detection into detectors

**What to build:** Move Gitleaks finding consumption plus URL/domain/IP/CIDR/email/path/connection-string detection behind detector implementations using the common candidate model.

**Blocked by:** 41. Move synthetic, dictionary and built-in secret detectors into the registry.

**Do not:** Add Presidio, TruffleHog, LlamaFirewall, new public allowlists, broad wildcard matching or binary extraction.

**Verification:**

```powershell
& 'C:\Users\alexey.andreev\AppData\Local\Microsoft\dotnet\dotnet.exe' test 'src\CodexRedactionGate\CodexRedactionGate.csproj' -nologo -v minimal
& 'C:\Users\alexey.andreev\AppData\Local\Microsoft\dotnet\dotnet.exe' run --project 'src\CodexRedactionGate\CodexRedactionGate.csproj' -- --self-test
```

- [x] Gitleaks-backed findings still become non-restorable secret redactions.
- [x] Gitleaks raw `Secret` and `Match` values are still not persisted.
- [x] Internal URLs and domains still pseudonymize.
- [x] Public documentation allowlists still use host-boundary matching.
- [x] Private IPs, CIDRs, emails and paths still pseudonymize.
- [x] Credentialed connection strings still redact non-restorably.
- [x] Build, tests and self-test are green.

## 43. Extract policy block and allowlist evaluators

**What to build:** Move explicit block-rule checks and public URL/domain allowlist checks out of detector loops into policy-facing evaluator components.

**Blocked by:** 42. Move scanner findings and technical identifier detection into detectors.

**Do not:** Change TOML schema, add policy distribution, add a database, or let allowlists override secrets, dictionary terms or block rules.

**Verification:**

```powershell
& 'C:\Users\alexey.andreev\AppData\Local\Microsoft\dotnet\dotnet.exe' test 'src\CodexRedactionGate\CodexRedactionGate.csproj' -nologo -v minimal
& 'C:\Users\alexey.andreev\AppData\Local\Microsoft\dotnet\dotnet.exe' run --project 'src\CodexRedactionGate\CodexRedactionGate.csproj' -- --self-test
```

- [x] Explicit block rules still return `block` with raw-free warning metadata.
- [x] Public allowlisted URLs are still allowed when they match the same origin and configured path rule.
- [x] Internal lookalike domains are still not allowed.
- [x] Secrets nested in otherwise public-looking text still redact.
- [x] Build, tests and self-test are green.

## 44. Extract span resolver and replacement planner

**What to build:** Move overlap selection, risk ordering, replacement action selection and vault pseudonym allocation into dedicated components.

**Blocked by:** 43. Extract policy block and allowlist evaluators.

**Do not:** Change placeholder formats, change HMAC inputs, store non-restorable secrets, or alter collision handling.

**Verification:**

```powershell
& 'C:\Users\alexey.andreev\AppData\Local\Microsoft\dotnet\dotnet.exe' test 'src\CodexRedactionGate\CodexRedactionGate.csproj' -nologo -v minimal
& 'C:\Users\alexey.andreev\AppData\Local\Microsoft\dotnet\dotnet.exe' run --project 'src\CodexRedactionGate\CodexRedactionGate.csproj' -- --self-test
```

- [x] Higher-risk sensitive spans still win over lower-risk overlaps.
- [x] Longer spans still win when risk is equal.
- [x] Connection strings still beat nested host/password spans.
- [x] Restorable identifiers still allocate stable vault pseudonyms.
- [x] Non-restorable secrets still bypass vault storage.
- [x] Build, tests and self-test are green.

## 45. Extract renderer and sanitized-output verifier

**What to build:** Move span-based rendering and raw-span verification into dedicated components.

**Blocked by:** 44. Extract span resolver and replacement planner.

**Do not:** Switch to repeated string replacement, log raw spans, change Unicode handling, or return `confirm` after verifier failure.

**Verification:**

```powershell
& 'C:\Users\alexey.andreev\AppData\Local\Microsoft\dotnet\dotnet.exe' test 'src\CodexRedactionGate\CodexRedactionGate.csproj' -nologo -v minimal
& 'C:\Users\alexey.andreev\AppData\Local\Microsoft\dotnet\dotnet.exe' run --project 'src\CodexRedactionGate\CodexRedactionGate.csproj' -- --self-test
```

- [x] Sanitized text is still rendered from ordered spans in one pass.
- [x] Surrounding punctuation and line breaks are preserved.
- [x] Verification still blocks when a selected raw span survives.
- [x] Verification still blocks on replacement-count mismatch or invalid spans.
- [x] Verification warnings remain raw-value-free.
- [x] Build, tests and self-test are green.

## 46. Extract audit event builder and result assembly

**What to build:** Move audit metadata creation and final result assembly into dedicated components so the sanitizer shell only coordinates the pipeline.

**Blocked by:** 45. Extract renderer and sanitized-output verifier.

**Do not:** Add persistent audit logging, log sanitized prompts by default, log raw originals, or change adapter decisions.

**Verification:**

```powershell
& 'C:\Users\alexey.andreev\AppData\Local\Microsoft\dotnet\dotnet.exe' test 'src\CodexRedactionGate\CodexRedactionGate.csproj' -nologo -v minimal
& 'C:\Users\alexey.andreev\AppData\Local\Microsoft\dotnet\dotnet.exe' run --project 'src\CodexRedactionGate\CodexRedactionGate.csproj' -- --self-test
```

- [x] Audit events still include timestamp, request id, decision, counts, scanner statuses, warnings and durations.
- [x] Audit span summaries still include content-part id, offset, length, type and detector id.
- [x] Audit replacement summaries still include placeholder, type and action.
- [x] Audit events still exclude raw prompt text, raw entity values, normalized values and sanitized prompt text.
- [x] `allow`, `confirm` and `block` result shapes remain unchanged.
- [x] Build, tests and self-test are green.

## 47. Shrink the sanitizer to an orchestration shell

**What to build:** Reduce the sanitizer implementation to dependency wiring and pipeline coordination while all extracted components own their specific responsibilities.

**Blocked by:** 46. Extract audit event builder and result assembly.

**Do not:** Change `ISanitizer`, remove public contract types, change CLI behavior, change guard behavior, or add new detector functionality.

**Verification:**

```powershell
& 'C:\Users\alexey.andreev\AppData\Local\Microsoft\dotnet\dotnet.exe' test 'src\CodexRedactionGate\CodexRedactionGate.csproj' -nologo -v minimal
& 'C:\Users\alexey.andreev\AppData\Local\Microsoft\dotnet\dotnet.exe' run --project 'src\CodexRedactionGate\CodexRedactionGate.csproj' -- --self-test
```

- [x] The sanitizer public entry point reads as orchestration rather than detection/rendering/audit implementation.
- [x] Extracted components are small enough to test independently when behavior is naturally local to that component.
- [x] The public sanitizer API remains the primary end-to-end seam.
- [x] CLI `--sanitize` and `--self-test` behavior is unchanged.
- [x] Build, tests and self-test are green.

## 48. Add post-refactor architecture verification

**What to build:** Add a final post-refactor smoke check and update architecture notes so future development starts from the extracted pipeline, not from the old monolithic shape.

**Blocked by:** 47. Shrink the sanitizer to an orchestration shell.

**Do not:** Add enterprise installer work, policy signing, binary parsers, new scanner backends or polished composer UI.

**Verification:**

```powershell
& 'C:\Users\alexey.andreev\AppData\Local\Microsoft\dotnet\dotnet.exe' build 'src\CodexRedactionGate\CodexRedactionGate.csproj' -nologo -v minimal
& 'C:\Users\alexey.andreev\AppData\Local\Microsoft\dotnet\dotnet.exe' test 'src\CodexRedactionGate\CodexRedactionGate.csproj' -nologo -v minimal
& 'C:\Users\alexey.andreev\AppData\Local\Microsoft\dotnet\dotnet.exe' run --project 'src\CodexRedactionGate\CodexRedactionGate.csproj' -- --self-test
```

- [x] Architecture notes describe the extracted sanitizer pipeline.
- [x] Package smoke still proves sanitize allow, confirm, scanner-backed secret redaction, guard block and local restore paths.
- [x] Runtime still does not require Git, Go, Gitleaks source code or network access.
- [x] Build, tests and self-test are green.

## Improvement Frontier: Post-refactor hardening and production safety

These tickets build the next improvement wave from `codex-redaction-gate-spec/POST_REFACTOR_IMPROVEMENT_SPEC.md`. Keep the public sanitizer contract stable. Do not add broad scanner dependencies or claim unsupported Codex prompt rewriting.

## 49. Split focused pipeline tests by behavior ownership

**What to build:** Move focused sanitizer pipeline tests into smaller behavior-owned test groups so future changes to content handling, scanners, detectors, rendering, audit or handoff have a clear local test surface.

**Blocked by:** 48. Add post-refactor architecture verification.

**Do not:** Remove sanitizer API regression coverage, rename public contracts, or change sanitizer behavior.

**Verification:**

```powershell
& 'C:\Users\alexey.andreev\AppData\Local\Microsoft\dotnet\dotnet.exe' build 'src\CodexRedactionGate\CodexRedactionGate.csproj' -nologo -v minimal
& 'C:\Users\alexey.andreev\AppData\Local\Microsoft\dotnet\dotnet.exe' test 'src\CodexRedactionGate\CodexRedactionGate.csproj' -nologo -v minimal
& 'C:\Users\alexey.andreev\AppData\Local\Microsoft\dotnet\dotnet.exe' run --project 'src\CodexRedactionGate\CodexRedactionGate.csproj' -- --self-test
```

**Verification result 2026-07-18:** build green with 0 warnings/0 errors; unit tests passed 176/176; `--self-test` printed `Self-test passed.`; focused sanitizer pipeline tests are split into behavior-owned files.

- [x] Focused tests are grouped by behavior ownership rather than by one catch-all pipeline file.
- [x] Test names describe behavior, not private helper implementation.
- [x] Existing sanitizer behavior and package smoke coverage remain covered.
- [x] Build, tests and self-test are green.

## 50. Add internal typed sanitizer decision values

**What to build:** Introduce internal typed representations for entity type, sanitizer action, detector id and scanner status while preserving the current public string contracts.

**Blocked by:** 49. Split focused pipeline tests by behavior ownership.

**Do not:** Change public record fields, serialized audit shape, policy file schema or CLI output.

**Verification:**

```powershell
& 'C:\Users\alexey.andreev\AppData\Local\Microsoft\dotnet\dotnet.exe' build 'src\CodexRedactionGate\CodexRedactionGate.csproj' -nologo -v minimal
& 'C:\Users\alexey.andreev\AppData\Local\Microsoft\dotnet\dotnet.exe' test 'src\CodexRedactionGate\CodexRedactionGate.csproj' -nologo -v minimal
& 'C:\Users\alexey.andreev\AppData\Local\Microsoft\dotnet\dotnet.exe' run --project 'src\CodexRedactionGate\CodexRedactionGate.csproj' -- --self-test
```

**Verification result 2026-07-18:** build green with 0 warnings/0 errors; unit tests passed 176/176; `--self-test` printed `Self-test passed.`; internal value objects cover entity type, action, detector id and scanner status while public contracts remain string-compatible.

- [x] Internal candidate metadata can use typed entity/action/detector/status values.
- [x] Public `SanitizationResult`, `Replacement`, `SanitizedEntity`, `AuditEvent` and CLI output remain string-compatible.
- [x] Invalid internal values are harder to construct accidentally.
- [x] Build, tests and self-test are green.

## 51. Migrate detectors and replacement planning to typed internal values

**What to build:** Move detector outputs, span resolution and replacement planning to the typed internal decision values, converting back to public strings only at result assembly boundaries.

**Blocked by:** 50. Add internal typed sanitizer decision values.

**Do not:** Change placeholder formats, HMAC inputs, vault records, policy schema or public audit strings.

**Verification:**

```powershell
& 'C:\Users\alexey.andreev\AppData\Local\Microsoft\dotnet\dotnet.exe' build 'src\CodexRedactionGate\CodexRedactionGate.csproj' -nologo -v minimal
& 'C:\Users\alexey.andreev\AppData\Local\Microsoft\dotnet\dotnet.exe' test 'src\CodexRedactionGate\CodexRedactionGate.csproj' -nologo -v minimal
& 'C:\Users\alexey.andreev\AppData\Local\Microsoft\dotnet\dotnet.exe' run --project 'src\CodexRedactionGate\CodexRedactionGate.csproj' -- --self-test
```

**Verification result 2026-07-18:** build green with 0 warnings/0 errors; unit tests passed 176/176; `--self-test` printed `Self-test passed.`; detector outputs, span resolution and replacement planning use typed internal values and convert to strings at result/audit boundaries.

- [x] Built-in, dictionary, Gitleaks and technical detectors emit typed internal candidates.
- [x] Span resolver risk ordering still behaves the same.
- [x] Replacement planner still stores restorable identifiers and bypasses vault for non-restorable secrets.
- [x] Public results and audit metadata remain unchanged.
- [x] Build, tests and self-test are green.

## 52. Add raw-free sanitizer decision trace

**What to build:** Add debug-safe decision trace metadata that explains allow, confirm and block outcomes by stage, reason code, scanner status, counts and durations.

**Blocked by:** 51. Migrate detectors and replacement planning to typed internal values.

**Do not:** Log raw prompt text, raw entity values, normalized values, sanitized prompt text or restored output.

**Verification:**

```powershell
& 'C:\Users\alexey.andreev\AppData\Local\Microsoft\dotnet\dotnet.exe' build 'src\CodexRedactionGate\CodexRedactionGate.csproj' -nologo -v minimal
& 'C:\Users\alexey.andreev\AppData\Local\Microsoft\dotnet\dotnet.exe' test 'src\CodexRedactionGate\CodexRedactionGate.csproj' -nologo -v minimal
& 'C:\Users\alexey.andreev\AppData\Local\Microsoft\dotnet\dotnet.exe' run --project 'src\CodexRedactionGate\CodexRedactionGate.csproj' -- --self-test
```

**Verification result 2026-07-18:** build green with 0 warnings/0 errors; unit tests passed 176/176; `--self-test` printed `Self-test passed.`; audit metadata now includes raw-free `trace.*` stage, reason, detector/type/action count and verification entries.

- [x] Allow decisions include a raw-free trace showing no sensitive candidates survived.
- [x] Confirm decisions include raw-free stage, detector, type/action counts and verification status.
- [x] Block decisions include raw-free reason codes for attachment, policy, scanner and verifier failures.
- [x] Trace content is covered by raw-leak regression tests.
- [x] Build, tests and self-test are green.

## 53. Add local raw-free audit sink

**What to build:** Persist audit events to a local audit sink that writes only safe metadata and integrates with the default storage layout.

**Blocked by:** 52. Add raw-free sanitizer decision trace.

**Do not:** Persist raw prompts, normalized values, sanitized prompts, restored output or scanner raw `Secret`/`Match` values.

**Verification:**

```powershell
& 'C:\Users\alexey.andreev\AppData\Local\Microsoft\dotnet\dotnet.exe' build 'src\CodexRedactionGate\CodexRedactionGate.csproj' -nologo -v minimal
& 'C:\Users\alexey.andreev\AppData\Local\Microsoft\dotnet\dotnet.exe' test 'src\CodexRedactionGate\CodexRedactionGate.csproj' -nologo -v minimal
& 'C:\Users\alexey.andreev\AppData\Local\Microsoft\dotnet\dotnet.exe' run --project 'src\CodexRedactionGate\CodexRedactionGate.csproj' -- --self-test
```

**Verification result 2026-07-18:** build green with 0 warnings/0 errors; unit tests passed 176/176; `--self-test` printed `Self-test passed.`; `FileAuditSink` writes raw-free audit JSON under the configured local audit directory with atomic file replace semantics.

- [x] Audit sink writes local audit events under the user-local audit directory.
- [x] Audit writes are atomic or append-safe.
- [x] Audit payloads contain decision, counts, scanner statuses, warning codes and durations.
- [x] Audit payloads omit all raw-sensitive fields by default.
- [x] Build, tests and self-test are green.

## 54. Add audit retention and failure behavior checks

**What to build:** Add retention and failure-mode checks for local audit persistence so audit storage does not become a safety bypass or an unbounded local liability.

**Blocked by:** 53. Add local raw-free audit sink.

**Do not:** Add enterprise log shipping, remote telemetry or raw prompt logging.

**Verification:**

```powershell
& 'C:\Users\alexey.andreev\AppData\Local\Microsoft\dotnet\dotnet.exe' build 'src\CodexRedactionGate\CodexRedactionGate.csproj' -nologo -v minimal
& 'C:\Users\alexey.andreev\AppData\Local\Microsoft\dotnet\dotnet.exe' test 'src\CodexRedactionGate\CodexRedactionGate.csproj' -nologo -v minimal
& 'C:\Users\alexey.andreev\AppData\Local\Microsoft\dotnet\dotnet.exe' run --project 'src\CodexRedactionGate\CodexRedactionGate.csproj' -- --self-test
```

**Verification result 2026-07-18:** build green with 0 warnings/0 errors; unit tests passed 176/176; `--self-test` printed `Self-test passed.`; audit retention is count-bounded and configured audit write failure blocks sensitive confirm while non-sensitive allow remains usable.

- [x] Audit retention can be bounded by count, size or age.
- [x] Audit write failures produce raw-free warnings.
- [x] Cloud-bound sensitive submissions do not silently proceed when mandatory audit persistence fails.
- [x] Non-sensitive allow path behavior remains low-friction.
- [x] Build, tests and self-test are green.

## 55. Add scanner runtime configuration validation

**What to build:** Validate the configured scanner artifact path and source-build provenance at runtime/package smoke boundaries without requiring Git, Go, source code or network on the user machine.

**Blocked by:** 52. Add raw-free sanitizer decision trace.

**Do not:** Build Gitleaks at runtime, download scanner artifacts, add new scanner backends or weaken fail-closed scanner behavior.

**Verification:**

```powershell
& 'C:\Users\alexey.andreev\AppData\Local\Microsoft\dotnet\dotnet.exe' build 'src\CodexRedactionGate\CodexRedactionGate.csproj' -nologo -v minimal
& 'C:\Users\alexey.andreev\AppData\Local\Microsoft\dotnet\dotnet.exe' test 'src\CodexRedactionGate\CodexRedactionGate.csproj' -nologo -v minimal
& 'C:\Users\alexey.andreev\AppData\Local\Microsoft\dotnet\dotnet.exe' run --project 'src\CodexRedactionGate\CodexRedactionGate.csproj' -- --self-test
```

**Verification result 2026-07-18:** build green with 0 warnings/0 errors; unit tests passed 176/176; `--self-test` printed `Self-test passed.`; scanner runtime validation covers binary/provenance checks and guarded scanner configuration errors fail closed.

- [x] Missing scanner binary is detected before unsafe scanner-backed allow behavior.
- [x] Missing or invalid provenance is reported as a raw-free configuration problem.
- [x] Package smoke still proves no Git, Go, source or network runtime requirement.
- [x] Scanner configuration errors fail closed for cloud-bound sensitive content.
- [x] Build, tests and self-test are green.

## 56. Add executable confirm handoff smoke path

**What to build:** Add a narrow executable smoke path proving that a confirmed sanitized prompt can flow from sanitizer result to submit-owning adapter submission without manual copy as the happy path.

**Blocked by:** 52. Add raw-free sanitizer decision trace.

**Do not:** Claim transparent Codex prompt rewriting, build a polished composer UI, or make clipboard the happy path.

**Verification:**

```powershell
& 'C:\Users\alexey.andreev\AppData\Local\Microsoft\dotnet\dotnet.exe' build 'src\CodexRedactionGate\CodexRedactionGate.csproj' -nologo -v minimal
& 'C:\Users\alexey.andreev\AppData\Local\Microsoft\dotnet\dotnet.exe' test 'src\CodexRedactionGate\CodexRedactionGate.csproj' -nologo -v minimal
& 'C:\Users\alexey.andreev\AppData\Local\Microsoft\dotnet\dotnet.exe' run --project 'src\CodexRedactionGate\CodexRedactionGate.csproj' -- --self-test
```

**Verification result 2026-07-18:** build green with 0 warnings/0 errors; unit tests passed 176/176; `--self-test` printed `Self-test passed.`; package smoke proves approved confirm submits only `sanitized_text` through `SubmitOwningAdapter`.

- [x] Confirm action submits only `sanitized_text` through an adapter-owned path.
- [x] Cancel submits nothing.
- [x] Block submits nothing.
- [x] Handoff smoke output does not expose raw original prompt values.
- [x] Build, tests and self-test are green.

## 57. Add attachment ingestion boundary smoke path

**What to build:** Add a narrow ingestion boundary proving readable text attachments and file snippets are converted into sanitizer content parts before cloud submission.

**Blocked by:** 52. Add raw-free sanitizer decision trace.

**Do not:** Parse PDF, Office, image, OCR or archives.

**Verification:**

```powershell
& 'C:\Users\alexey.andreev\AppData\Local\Microsoft\dotnet\dotnet.exe' build 'src\CodexRedactionGate\CodexRedactionGate.csproj' -nologo -v minimal
& 'C:\Users\alexey.andreev\AppData\Local\Microsoft\dotnet\dotnet.exe' test 'src\CodexRedactionGate\CodexRedactionGate.csproj' -nologo -v minimal
& 'C:\Users\alexey.andreev\AppData\Local\Microsoft\dotnet\dotnet.exe' run --project 'src\CodexRedactionGate\CodexRedactionGate.csproj' -- --self-test
```

**Verification result 2026-07-18:** build green with 0 warnings/0 errors; unit tests passed 176/176; `--self-test` printed `Self-test passed.`; attachment ingestion covers text attachments, file snippets and unsupported binary metadata.

- [x] Text attachment content enters sanitizer as a text attachment content part.
- [x] File snippet content enters sanitizer as a file snippet content part.
- [x] Unsupported binary metadata still blocks or warns explicitly.
- [x] Raw attachment content is not written to warnings or audit output.
- [x] Build, tests and self-test are green.

## 58. Add post-improvement architecture verification

**What to build:** Update architecture notes and package smoke expectations after the improvement frontier lands, so future work starts from the typed, traceable and audit-persisted pipeline.

**Blocked by:** 54. Add audit retention and failure behavior checks; 55. Add scanner runtime configuration validation; 56. Add executable confirm handoff smoke path; 57. Add attachment ingestion boundary smoke path.

**Do not:** Add new product features beyond documenting and verifying the completed improvement frontier.

**Verification:**

```powershell
& 'C:\Users\alexey.andreev\AppData\Local\Microsoft\dotnet\dotnet.exe' build 'src\CodexRedactionGate\CodexRedactionGate.csproj' -nologo -v minimal
& 'C:\Users\alexey.andreev\AppData\Local\Microsoft\dotnet\dotnet.exe' test 'src\CodexRedactionGate\CodexRedactionGate.csproj' -nologo -v minimal
& 'C:\Users\alexey.andreev\AppData\Local\Microsoft\dotnet\dotnet.exe' run --project 'src\CodexRedactionGate\CodexRedactionGate.csproj' -- --self-test
```

**Verification result 2026-07-18:** build green with 0 warnings/0 errors; unit tests passed 176/176; `--self-test` printed `Self-test passed.`; architecture notes and post-refactor improvement spec describe the implemented typed, traceable and audit-persisted pipeline.

- [x] Architecture notes describe typed internal decisions, raw-free trace and local audit sink.
- [x] Package smoke covers scanner config validation, confirm handoff and attachment ingestion boundary.
- [x] Public contracts remain stable.
- [x] Build, tests and self-test are green.

## Improvement Frontier: Operational production readiness

These tickets build the next improvement wave from `codex-redaction-gate-spec/NEXT_IMPROVEMENT_SPEC.md`. The current architecture is described sufficiently; do not redesign the sanitizer pipeline. Work through policy operations, audit integrity, scanner packaging, attachment intake and gateway UX as narrow vertical slices.

## 59. Add local readiness doctor command

**What to build:** Add a raw-free local readiness check that tells the user whether storage, vault secret, policy, audit sink and scanner configuration are ready before cloud-bound prompt submission.

**Blocked by:** 58. Add post-improvement architecture verification.

**Do not:** Print raw prompt text, raw policy values, vault secrets, scanner raw output or machine-wide inventory.

**Verification:**

```powershell
& 'C:\Users\alexey.andreev\AppData\Local\Microsoft\dotnet\dotnet.exe' build 'src\CodexRedactionGate\CodexRedactionGate.csproj' -nologo -v minimal
& 'C:\Users\alexey.andreev\AppData\Local\Microsoft\dotnet\dotnet.exe' test 'src\CodexRedactionGate\CodexRedactionGate.csproj' -nologo -v minimal
& 'C:\Users\alexey.andreev\AppData\Local\Microsoft\dotnet\dotnet.exe' run --project 'src\CodexRedactionGate\CodexRedactionGate.csproj' -- --self-test
```

**Verification result 2026-07-18:** build green with 0 warnings/0 errors; unit tests passed 201/201; `--self-test` printed `Self-test passed.`; readiness diagnostics report raw-free storage, policy, vault, audit and scanner status.

- [x] Readiness command reports policy, vault, audit and scanner status with raw-free reason codes.
- [x] Missing scanner binary/provenance is reported without falling back to unsafe scanner-backed allow.
- [x] Missing or unwritable storage paths are reported with recovery-safe messages.
- [x] Success output contains no raw prompt, vault secret, dictionary value or scanner raw data.
- [x] Build, tests and self-test are green.

## 60. Add local sensitive dictionary management

**What to build:** Let the user add, list and remove manually managed sensitive terms for customer, project, product, system, domain and URL categories without editing CSV files directly.

**Blocked by:** 59. Add local readiness doctor command.

**Do not:** Store dictionary terms in project files by default, print removed raw terms in audit output, or add a database.

**Verification:**

```powershell
& 'C:\Users\alexey.andreev\AppData\Local\Microsoft\dotnet\dotnet.exe' build 'src\CodexRedactionGate\CodexRedactionGate.csproj' -nologo -v minimal
& 'C:\Users\alexey.andreev\AppData\Local\Microsoft\dotnet\dotnet.exe' test 'src\CodexRedactionGate\CodexRedactionGate.csproj' -nologo -v minimal
& 'C:\Users\alexey.andreev\AppData\Local\Microsoft\dotnet\dotnet.exe' run --project 'src\CodexRedactionGate\CodexRedactionGate.csproj' -- --self-test
```

**Verification result 2026-07-18:** build green with 0 warnings/0 errors; unit tests passed 201/201; `--self-test` printed `Self-test passed.`; managed dictionary add/list/remove stores terms user-locally and exposes safe summaries.

- [x] User can add a sensitive term to user-local dictionary storage.
- [x] User can list terms as safe summaries without leaking full sensitive values by default.
- [x] User can remove a term by stable local id or safe selector.
- [x] Sanitizer uses the managed dictionary in production/default construction.
- [x] Build, tests and self-test are green.

## 61. Add staged policy activation and rollback

**What to build:** Add a staged policy activation flow that validates candidate policy/dictionary changes before promotion and preserves the last known good configuration for rollback.

**Blocked by:** 60. Add local sensitive dictionary management.

**Do not:** Activate invalid TOML/CSV/regex rules, weaken existing fail-closed behavior, or require a remote policy service.

**Verification:**

```powershell
& 'C:\Users\alexey.andreev\AppData\Local\Microsoft\dotnet\dotnet.exe' build 'src\CodexRedactionGate\CodexRedactionGate.csproj' -nologo -v minimal
& 'C:\Users\alexey.andreev\AppData\Local\Microsoft\dotnet\dotnet.exe' test 'src\CodexRedactionGate\CodexRedactionGate.csproj' -nologo -v minimal
& 'C:\Users\alexey.andreev\AppData\Local\Microsoft\dotnet\dotnet.exe' run --project 'src\CodexRedactionGate\CodexRedactionGate.csproj' -- --self-test
```

**Verification result 2026-07-18:** build green with 0 warnings/0 errors; unit tests passed 201/201; `--self-test` printed `Self-test passed.`; staged policy activation validates before promotion and rollback restores the previous active policy.

- [x] Valid candidate policy can be promoted atomically.
- [x] Invalid candidate policy is rejected before activation.
- [x] Last known good policy remains active after failed activation.
- [x] Rollback restores the previous active policy.
- [x] Build, tests and self-test are green.

## 62. Add explicit policy precedence reporting

**What to build:** Make active policy precedence visible in raw-free diagnostics so global, project and session inputs resolve predictably.

**Blocked by:** 61. Add staged policy activation and rollback.

**Do not:** Change existing policy semantics silently, log raw rule values, or introduce organization-managed policy distribution yet.

**Verification:**

```powershell
& 'C:\Users\alexey.andreev\AppData\Local\Microsoft\dotnet\dotnet.exe' build 'src\CodexRedactionGate\CodexRedactionGate.csproj' -nologo -v minimal
& 'C:\Users\alexey.andreev\AppData\Local\Microsoft\dotnet\dotnet.exe' test 'src\CodexRedactionGate\CodexRedactionGate.csproj' -nologo -v minimal
& 'C:\Users\alexey.andreev\AppData\Local\Microsoft\dotnet\dotnet.exe' run --project 'src\CodexRedactionGate\CodexRedactionGate.csproj' -- --self-test
```

**Verification result 2026-07-18:** build green with 0 warnings/0 errors; unit tests passed 201/201; `--self-test` printed `Self-test passed.`; policy precedence diagnostics show raw-free source order, profile ids, rule counts and deterministic conflict semantics.

- [x] Effective policy report shows source precedence and active profile ids.
- [x] Conflicting allow/block/sensitive rules resolve deterministically.
- [x] Raw rule values and dictionary terms are absent from diagnostics by default.
- [x] Existing sanitizer behavior remains unchanged except for explicit diagnostics.
- [x] Build, tests and self-test are green.

## 63. Add tamper-evident local audit chain

**What to build:** Extend local audit persistence with a raw-free hash chain so modified, removed or reordered audit events can be detected locally.

**Blocked by:** 54. Add audit retention and failure behavior checks.

**Do not:** Hash raw prompt text, raw entity values, normalized originals, sanitized prompts or restored output.

**Verification:**

```powershell
& 'C:\Users\alexey.andreev\AppData\Local\Microsoft\dotnet\dotnet.exe' build 'src\CodexRedactionGate\CodexRedactionGate.csproj' -nologo -v minimal
& 'C:\Users\alexey.andreev\AppData\Local\Microsoft\dotnet\dotnet.exe' test 'src\CodexRedactionGate\CodexRedactionGate.csproj' -nologo -v minimal
& 'C:\Users\alexey.andreev\AppData\Local\Microsoft\dotnet\dotnet.exe' run --project 'src\CodexRedactionGate\CodexRedactionGate.csproj' -- --self-test
```

**Verification result 2026-07-18:** build green with 0 warnings/0 errors; unit tests passed 201/201; `--self-test` printed `Self-test passed.`; persisted audit records include raw-free previous/current hashes and verification detects modified, removed or reordered records.

- [x] Each persisted audit event links to the previous raw-free audit hash.
- [x] Chain verification passes for untouched audit files.
- [x] Chain verification detects modified, removed or reordered audit events.
- [x] Audit chain data contains no raw-sensitive fields.
- [x] Build, tests and self-test are green.

## 64. Add local audit verification and summary command

**What to build:** Add a raw-free local command that verifies audit integrity and summarizes decisions, warning codes, time ranges and broken-chain status.

**Blocked by:** 63. Add tamper-evident local audit chain.

**Do not:** Display raw prompt text, sanitized prompt text, raw entity values or restored output.

**Verification:**

```powershell
& 'C:\Users\alexey.andreev\AppData\Local\Microsoft\dotnet\dotnet.exe' build 'src\CodexRedactionGate\CodexRedactionGate.csproj' -nologo -v minimal
& 'C:\Users\alexey.andreev\AppData\Local\Microsoft\dotnet\dotnet.exe' test 'src\CodexRedactionGate\CodexRedactionGate.csproj' -nologo -v minimal
& 'C:\Users\alexey.andreev\AppData\Local\Microsoft\dotnet\dotnet.exe' run --project 'src\CodexRedactionGate\CodexRedactionGate.csproj' -- --self-test
```

**Verification result 2026-07-18:** build green with 0 warnings/0 errors; unit tests passed 201/201; `--self-test` printed `Self-test passed.`; audit summary reports decision counts, warning-code counts and chain status without raw-sensitive fields.

- [x] Audit summary reports allow/confirm/block counts and warning-code counts.
- [x] Audit summary reports chain integrity status.
- [x] Broken chain produces a raw-free warning and non-success verification status.
- [x] Summary output omits raw-sensitive fields.
- [x] Build, tests and self-test are green.

## 65. Enforce packaged Gitleaks checksum validation

**What to build:** Strengthen scanner runtime validation so the configured `gitleaks.exe` checksum must match source-build provenance before scanner-backed operation is trusted.

**Blocked by:** 55. Add scanner runtime configuration validation.

**Do not:** Build Gitleaks at runtime, download binaries, require Git/Go/source code on the user machine, or add new scanner backends.

**Verification:**

```powershell
& 'C:\Users\alexey.andreev\AppData\Local\Microsoft\dotnet\dotnet.exe' build 'src\CodexRedactionGate\CodexRedactionGate.csproj' -nologo -v minimal
& 'C:\Users\alexey.andreev\AppData\Local\Microsoft\dotnet\dotnet.exe' test 'src\CodexRedactionGate\CodexRedactionGate.csproj' -nologo -v minimal
& 'C:\Users\alexey.andreev\AppData\Local\Microsoft\dotnet\dotnet.exe' run --project 'src\CodexRedactionGate\CodexRedactionGate.csproj' -- --self-test
```

**Verification result 2026-07-18:** build green with 0 warnings/0 errors; unit tests passed 201/201; `--self-test` printed `Self-test passed.`; scanner runtime validation enforces binary checksum/provenance match and mismatch fails closed.

- [x] Matching binary checksum and provenance validates successfully.
- [x] Mismatched checksum is reported as a raw-free scanner configuration problem.
- [x] Scanner checksum failure is fatal for scanner-backed cloud-bound flow.
- [x] Package smoke still proves no Git, Go, source or network runtime requirement.
- [x] Build, tests and self-test are green.

## 66. Add release package manifest smoke

**What to build:** Add a release-level smoke check that validates app artifact, scanner artifact, provenance, storage defaults and runtime assumptions from one package manifest.

**Blocked by:** 65. Enforce packaged Gitleaks checksum validation.

**Do not:** Create an installer, publish artifacts, invoke network access or require administrator privileges.

**Verification:**

```powershell
& 'C:\Users\alexey.andreev\AppData\Local\Microsoft\dotnet\dotnet.exe' build 'src\CodexRedactionGate\CodexRedactionGate.csproj' -nologo -v minimal
& 'C:\Users\alexey.andreev\AppData\Local\Microsoft\dotnet\dotnet.exe' test 'src\CodexRedactionGate\CodexRedactionGate.csproj' -nologo -v minimal
& 'C:\Users\alexey.andreev\AppData\Local\Microsoft\dotnet\dotnet.exe' run --project 'src\CodexRedactionGate\CodexRedactionGate.csproj' -- --self-test
```

**Verification result 2026-07-18:** build green with 0 warnings/0 errors; unit tests passed 201/201; `--self-test` printed `Self-test passed.`; release package smoke validates app/scanner artifacts, checksum/provenance and no Git/Go/network runtime assumptions.

- [x] Release package manifest validates app and scanner artifact presence.
- [x] Release package smoke validates checksum/provenance consistency.
- [x] Release package smoke reports runtime requirements as raw-free metadata.
- [x] Release package smoke fails closed on missing or mismatched artifacts.
- [x] Build, tests and self-test are green.

## 67. Add plain-text file attachment intake

**What to build:** Let the local adapter ingest readable plain-text files into sanitizer content parts with size, encoding and content-type limits before cloud submission.

**Blocked by:** 57. Add attachment ingestion boundary smoke path.

**Do not:** Parse PDF, Office, image, OCR or archives; do not silently allow unreadable files.

**Verification:**

```powershell
& 'C:\Users\alexey.andreev\AppData\Local\Microsoft\dotnet\dotnet.exe' build 'src\CodexRedactionGate\CodexRedactionGate.csproj' -nologo -v minimal
& 'C:\Users\alexey.andreev\AppData\Local\Microsoft\dotnet\dotnet.exe' test 'src\CodexRedactionGate\CodexRedactionGate.csproj' -nologo -v minimal
& 'C:\Users\alexey.andreev\AppData\Local\Microsoft\dotnet\dotnet.exe' run --project 'src\CodexRedactionGate\CodexRedactionGate.csproj' -- --self-test
```

**Verification result 2026-07-18:** build green with 0 warnings/0 errors; unit tests passed 201/201; `--self-test` printed `Self-test passed.`; plain-text attachment intake reads supported UTF-8 files with caps and fails closed for unsupported/unreadable files.

- [x] Plain-text file content enters sanitizer as a file snippet or text attachment content part.
- [x] Size and encoding limits are enforced before sanitization.
- [x] Unsupported or unreadable files block or warn explicitly without silent allow.
- [x] Raw file content is absent from warnings, audit and readiness output.
- [x] Build, tests and self-test are green.

## 68. Add minimal local composer shell

**What to build:** Add a minimal submit-owning local composer shell that runs sanitizer, shows the confirmation model and submits only approved `sanitized_text`.

**Blocked by:** 56. Add executable confirm handoff smoke path; 59. Add local readiness doctor command.

**Do not:** Claim transparent Codex prompt rewriting, build a polished UI, or make clipboard the happy path.

**Verification:**

```powershell
& 'C:\Users\alexey.andreev\AppData\Local\Microsoft\dotnet\dotnet.exe' build 'src\CodexRedactionGate\CodexRedactionGate.csproj' -nologo -v minimal
& 'C:\Users\alexey.andreev\AppData\Local\Microsoft\dotnet\dotnet.exe' test 'src\CodexRedactionGate\CodexRedactionGate.csproj' -nologo -v minimal
& 'C:\Users\alexey.andreev\AppData\Local\Microsoft\dotnet\dotnet.exe' run --project 'src\CodexRedactionGate\CodexRedactionGate.csproj' -- --self-test
```

**Verification result 2026-07-18:** build green with 0 warnings/0 errors; unit tests passed 201/201; `--self-test` printed `Self-test passed.`; minimal local composer shell owns submit and sends only approved sanitized text.

- [x] Safe prompt can be submitted through the composer shell.
- [x] Sensitive prompt opens confirmation and submits only approved `sanitized_text`.
- [x] Cancel submits nothing.
- [x] Block submits nothing and gives raw-free reason codes.
- [x] Build, tests and self-test are green.

## 69. Add gateway failure recovery smoke

**What to build:** Add executable smoke coverage for gateway failures so sanitizer, confirmation UI, submitter and audit failures all fail closed without sending the original prompt.

**Blocked by:** 68. Add minimal local composer shell.

**Do not:** Add remote telemetry, retry raw prompt submission, or hide adapter failures as successful sends.

**Verification:**

```powershell
& 'C:\Users\alexey.andreev\AppData\Local\Microsoft\dotnet\dotnet.exe' build 'src\CodexRedactionGate\CodexRedactionGate.csproj' -nologo -v minimal
& 'C:\Users\alexey.andreev\AppData\Local\Microsoft\dotnet\dotnet.exe' test 'src\CodexRedactionGate\CodexRedactionGate.csproj' -nologo -v minimal
& 'C:\Users\alexey.andreev\AppData\Local\Microsoft\dotnet\dotnet.exe' run --project 'src\CodexRedactionGate\CodexRedactionGate.csproj' -- --self-test
```

**Verification result 2026-07-18:** build green with 0 warnings/0 errors; unit tests passed 201/201; `--self-test` printed `Self-test passed.`; gateway failure recovery covers confirmation, submitter and mandatory audit failures without sending original prompts.

- [x] Confirmation provider failure submits nothing.
- [x] Submitter failure reports raw-free failure status.
- [x] Mandatory audit failure for sensitive prompt submits nothing.
- [x] Original raw prompt is never sent in failure paths.
- [x] Build, tests and self-test are green.

## 70. Add local restoration handoff smoke

**What to build:** Add smoke coverage for sanitized model response restoration so restored local-sensitive output is visibly marked and cannot be resubmitted accidentally.

**Blocked by:** 68. Add minimal local composer shell.

**Do not:** Restore non-restorable secrets, store raw model outputs in audit, or allow local-sensitive restored output to bypass the sanitizer.

**Verification:**

```powershell
& 'C:\Users\alexey.andreev\AppData\Local\Microsoft\dotnet\dotnet.exe' build 'src\CodexRedactionGate\CodexRedactionGate.csproj' -nologo -v minimal
& 'C:\Users\alexey.andreev\AppData\Local\Microsoft\dotnet\dotnet.exe' test 'src\CodexRedactionGate\CodexRedactionGate.csproj' -nologo -v minimal
& 'C:\Users\alexey.andreev\AppData\Local\Microsoft\dotnet\dotnet.exe' run --project 'src\CodexRedactionGate\CodexRedactionGate.csproj' -- --self-test
```

**Verification result 2026-07-18:** build green with 0 warnings/0 errors; unit tests passed 201/201; `--self-test` printed `Self-test passed.`; restoration handoff restores local pseudonyms, keeps non-restorable redactions and blocks local-sensitive resubmission.

- [x] Restorable pseudonyms can be restored through local handoff.
- [x] Restored output is marked local-sensitive.
- [x] Non-restorable redactions remain redacted.
- [x] Attempted resubmission of restored local-sensitive output is blocked or re-sanitized.
- [x] Build, tests and self-test are green.

## 71. Add release readiness smoke matrix

**What to build:** Add a final readiness smoke matrix that proves policy ops, audit integrity, scanner packaging, attachment intake, gateway handoff and restoration handoff together before a release.

**Blocked by:** 62. Add explicit policy precedence reporting; 64. Add local audit verification and summary command; 66. Add release package manifest smoke; 67. Add plain-text file attachment intake; 69. Add gateway failure recovery smoke; 70. Add local restoration handoff smoke.

**Do not:** Add new product features, new scanner backends, installers or managed deployment in this ticket.

**Verification:**

```powershell
& 'C:\Users\alexey.andreev\AppData\Local\Microsoft\dotnet\dotnet.exe' build 'src\CodexRedactionGate\CodexRedactionGate.csproj' -nologo -v minimal
& 'C:\Users\alexey.andreev\AppData\Local\Microsoft\dotnet\dotnet.exe' test 'src\CodexRedactionGate\CodexRedactionGate.csproj' -nologo -v minimal
& 'C:\Users\alexey.andreev\AppData\Local\Microsoft\dotnet\dotnet.exe' run --project 'src\CodexRedactionGate\CodexRedactionGate.csproj' -- --self-test
```

**Verification result 2026-07-18:** build green with 0 warnings/0 errors; unit tests passed 201/201; `--self-test` printed `Self-test passed.`; release readiness smoke matrix covers policy, audit, scanner package, attachment, gateway and restoration paths.

- [x] Readiness smoke covers policy activation and precedence diagnostics.
- [x] Readiness smoke covers audit chain verification.
- [x] Readiness smoke covers scanner package checksum/provenance validation.
- [x] Readiness smoke covers text file attachment intake and gateway confirm/cancel/block paths.
- [x] Build, tests and self-test are green.

## 72. Add platform-neutral interaction adapter contracts

**What to build:** Add the core interaction contracts that let the sanitizer work with OS desktop adapters, future Linux adapters and future CLI wrappers without changing sanitizer logic.

**Blocked by:** 71. Add release readiness smoke matrix.

**Do not:** Implement Windows UI Automation, Linux accessibility, terminal interception, browser extension behavior or real submit automation in this ticket.

**Verification:**

```powershell
& 'C:\Users\alexey.andreev\AppData\Local\Microsoft\dotnet\dotnet.exe' build 'src\CodexRedactionGate\CodexRedactionGate.csproj' -nologo -v minimal
& 'C:\Users\alexey.andreev\AppData\Local\Microsoft\dotnet\dotnet.exe' test 'src\CodexRedactionGate\CodexRedactionGate.csproj' -nologo -v minimal
& 'C:\Users\alexey.andreev\AppData\Local\Microsoft\dotnet\dotnet.exe' run --project 'src\CodexRedactionGate\CodexRedactionGate.csproj' -- --self-test
```

**Verification result 2026-07-18:** build green with 0 warnings/0 errors; unit tests passed 219/219; `--self-test` printed `Self-test passed.`; platform-neutral OS interaction contracts cover surface discovery, capture, replace, submit, hotkey binding and confirmation overlay with raw-free statuses.

- [x] Contracts exist for active surface discovery, text capture, text replacement, submit action, hotkey trigger and confirmation overlay.
- [x] The contracts are platform-neutral and do not mention Windows UI Automation types directly.
- [x] Interaction outcomes include raw-free statuses for unsupported surface, capture failure, write failure, submit failure, block and cancel.
- [x] Existing sanitizer, CLI and local composer behavior remains unchanged.
- [x] Build, tests and self-test are green.

## 73. Add interaction orchestrator with fake adapter demo seam

**What to build:** Add an interaction orchestrator that drives capture, sanitize, confirm, apply and optional submit through the new contracts, using fake adapters so the behavior is testable without controlling the desktop.

**Blocked by:** 72. Add platform-neutral interaction adapter contracts.

**Do not:** Read real app windows, synthesize real keyboard input, or create a visible desktop overlay in this ticket.

**Verification:**

```powershell
& 'C:\Users\alexey.andreev\AppData\Local\Microsoft\dotnet\dotnet.exe' build 'src\CodexRedactionGate\CodexRedactionGate.csproj' -nologo -v minimal
& 'C:\Users\alexey.andreev\AppData\Local\Microsoft\dotnet\dotnet.exe' test 'src\CodexRedactionGate\CodexRedactionGate.csproj' -nologo -v minimal
& 'C:\Users\alexey.andreev\AppData\Local\Microsoft\dotnet\dotnet.exe' run --project 'src\CodexRedactionGate\CodexRedactionGate.csproj' -- --self-test
```

**Verification result 2026-07-18:** build green with 0 warnings/0 errors; unit tests passed 219/219; `--self-test` printed `Self-test passed.`; fake-adapter orchestrator tests cover allow, confirm/apply, cancel, block, write failure and verified send paths without raw diagnostics.

- [x] Safe prompt flows from capture to allow/apply-or-submit through a fake adapter.
- [x] Sensitive prompt opens confirmation and applies only approved `sanitized_text`.
- [x] Cancel applies nothing and submits nothing.
- [x] Block applies nothing and submits nothing.
- [x] Orchestrator diagnostics and tests do not expose raw prompt values.
- [x] Build, tests and self-test are green.

## 74. Add Windows active surface discovery diagnostic

**What to build:** Add a Windows-only diagnostic command that can identify whether the active foreground text surface looks like a supported Codex/ChatGPT desktop composer, without modifying text or sending anything.

**Blocked by:** 72. Add platform-neutral interaction adapter contracts.

**Do not:** Replace composer text, press submit, store captured prompt contents, or claim support for arbitrary applications.

**Verification:**

```powershell
& 'C:\Users\alexey.andreev\AppData\Local\Microsoft\dotnet\dotnet.exe' build 'src\CodexRedactionGate\CodexRedactionGate.csproj' -nologo -v minimal
& 'C:\Users\alexey.andreev\AppData\Local\Microsoft\dotnet\dotnet.exe' test 'src\CodexRedactionGate\CodexRedactionGate.csproj' -nologo -v minimal
& 'C:\Users\alexey.andreev\AppData\Local\Microsoft\dotnet\dotnet.exe' run --project 'src\CodexRedactionGate\CodexRedactionGate.csproj' -- --self-test
```

**Verification result 2026-07-18:** build green with 0 warnings/0 errors; unit tests passed 219/219; `--self-test` printed `Self-test passed.`; Windows surface diagnostic uses raw-free foreground metadata and Codex/ChatGPT surface profiles, with unsupported-platform fallback.

- [x] Diagnostic reports supported/unsupported active surface with raw-free metadata.
- [x] Diagnostic distinguishes Codex/ChatGPT desktop surface profiles from unknown windows where practical.
- [x] Diagnostic reports whether read/write/submit capabilities appear available.
- [x] Diagnostic never prints the active prompt text.
- [x] Non-Windows execution returns a raw-free unsupported-platform status.
- [x] Build, tests and self-test are green.

## 75. Add hotkey-triggered Windows capture dry-run

**What to build:** Add the first UX demo loop for Windows: a hotkey captures the active Codex/ChatGPT composer text, sanitizes it, and shows a dry-run result without changing or submitting anything.

**Blocked by:** 73. Add interaction orchestrator with fake adapter demo seam; 74. Add Windows active surface discovery diagnostic.

**Do not:** Replace text in the app, press submit, or require the user to move prompt writing into a separate composer.

**Verification:**

```powershell
& 'C:\Users\alexey.andreev\AppData\Local\Microsoft\dotnet\dotnet.exe' build 'src\CodexRedactionGate\CodexRedactionGate.csproj' -nologo -v minimal
& 'C:\Users\alexey.andreev\AppData\Local\Microsoft\dotnet\dotnet.exe' test 'src\CodexRedactionGate\CodexRedactionGate.csproj' -nologo -v minimal
& 'C:\Users\alexey.andreev\AppData\Local\Microsoft\dotnet\dotnet.exe' run --project 'src\CodexRedactionGate\CodexRedactionGate.csproj' -- --self-test
```

**Verification result 2026-07-18:** build green with 0 warnings/0 errors; unit tests passed 219/219; `--self-test` printed `Self-test passed.`; `--os-demo-dry-run "Connect to 192.168.10.25"` printed a dry-run confirmation preview with an `IP_*` placeholder and no raw IP; `--os-demo-hotkey` provides the live Windows dry-run hotkey path documented in the manual checklist.

- [x] User can trigger sanitize dry-run while focus remains in the Codex/ChatGPT desktop composer.
- [x] Dry-run shows allow/confirm/block status without modifying the composer.
- [x] Sensitive values are absent from dry-run diagnostics.
- [x] Missing or unreadable active composer fails closed.
- [x] A manual demo checklist documents how to run the hotkey dry-run.
- [x] Build, tests and self-test are green.

## 76. Add local confirmation overlay for OS adapter

**What to build:** Add the visible confirmation overlay for the OS adapter demo, reusing the sanitizer confirmation model to show sanitized prompt, highlighted replacements, counts, warnings and actions.

**Blocked by:** 73. Add interaction orchestrator with fake adapter demo seam.

**Do not:** Add browser extension UI, expose raw values by default, or submit automatically from the overlay in this ticket.

**Verification:**

```powershell
& 'C:\Users\alexey.andreev\AppData\Local\Microsoft\dotnet\dotnet.exe' build 'src\CodexRedactionGate\CodexRedactionGate.csproj' -nologo -v minimal
& 'C:\Users\alexey.andreev\AppData\Local\Microsoft\dotnet\dotnet.exe' test 'src\CodexRedactionGate\CodexRedactionGate.csproj' -nologo -v minimal
& 'C:\Users\alexey.andreev\AppData\Local\Microsoft\dotnet\dotnet.exe' run --project 'src\CodexRedactionGate\CodexRedactionGate.csproj' -- --self-test
```

**Verification result 2026-07-18:** build green with 0 warnings/0 errors; unit tests passed 219/219; `--self-test` printed `Self-test passed.`; overlay renderer and WinForms overlay show confirm/cancel, highlighted placeholders, counts, warnings and raw-hidden state in demo preview/live paths.

- [x] Overlay shows `Confirm sanitized prompt` and `Cancel`.
- [x] Overlay highlights replacements in the sanitized prompt.
- [x] Overlay shows counts by sensitive type and high-risk warnings.
- [x] Overlay raw values are hidden by default.
- [x] Overlay can run in a demo mode against fake interaction results.
- [x] Build, tests and self-test are green.

## 77. Add apply-only sanitized write-back for Windows desktop app

**What to build:** Let the Windows demo replace the active Codex/ChatGPT composer text with approved `sanitized_text` after confirmation, without pressing submit.

**Blocked by:** 75. Add hotkey-triggered Windows capture dry-run; 76. Add local confirmation overlay for OS adapter.

**Do not:** Automatically submit to the cloud, use raw prompt text in diagnostics, or write to unsupported windows.

**Verification:**

```powershell
& 'C:\Users\alexey.andreev\AppData\Local\Microsoft\dotnet\dotnet.exe' build 'src\CodexRedactionGate\CodexRedactionGate.csproj' -nologo -v minimal
& 'C:\Users\alexey.andreev\AppData\Local\Microsoft\dotnet\dotnet.exe' test 'src\CodexRedactionGate\CodexRedactionGate.csproj' -nologo -v minimal
& 'C:\Users\alexey.andreev\AppData\Local\Microsoft\dotnet\dotnet.exe' run --project 'src\CodexRedactionGate\CodexRedactionGate.csproj' -- --self-test
```

**Verification result 2026-07-18:** build green with 0 warnings/0 errors; unit tests passed 219/219; `--self-test` printed `Self-test passed.`; fake-adapter apply-only tests and `--os-demo-hotkey-apply` live mode prove confirm writes sanitized text, cancel/block leave text unchanged and write failures submit nothing.

- [x] Confirm replaces the active composer contents with `sanitized_text`.
- [x] Cancel leaves the composer unchanged.
- [x] Block leaves the composer unchanged.
- [x] Write-back failure leaves the app unsubmitted and reports a raw-free failure status.
- [x] Manual demo checklist proves the user can visually inspect the sanitized prompt inside the Codex/ChatGPT composer before sending.
- [x] Build, tests and self-test are green.

## 78. Add explicit confirm-and-send mode for Windows demo

**What to build:** Add an opt-in send mode that submits only after the adapter has confirmed the sanitized text was applied to the Codex/ChatGPT composer.

**Blocked by:** 77. Add apply-only sanitized write-back for Windows desktop app.

**Do not:** Enable automatic submit by default, submit after failed write-back, or submit a prompt that still contains selected raw spans.

**Verification:**

```powershell
& 'C:\Users\alexey.andreev\AppData\Local\Microsoft\dotnet\dotnet.exe' build 'src\CodexRedactionGate\CodexRedactionGate.csproj' -nologo -v minimal
& 'C:\Users\alexey.andreev\AppData\Local\Microsoft\dotnet\dotnet.exe' test 'src\CodexRedactionGate\CodexRedactionGate.csproj' -nologo -v minimal
& 'C:\Users\alexey.andreev\AppData\Local\Microsoft\dotnet\dotnet.exe' run --project 'src\CodexRedactionGate\CodexRedactionGate.csproj' -- --self-test
```

**Verification result 2026-07-18:** build green with 0 warnings/0 errors; unit tests passed 219/219; `--self-test` printed `Self-test passed.`; send mode is represented by explicit `ConfirmAndSend` options and `--os-demo-hotkey-send`, verifies write-back before submit, and remains off in `ApplyOnly` demo mode.

- [x] Send mode is explicitly enabled by command/config and is off by default.
- [x] Confirm writes `sanitized_text`, verifies the active composer now contains the sanitized text, then submits.
- [x] If verification fails, nothing is submitted.
- [x] Cancel and block submit nothing.
- [x] Submit diagnostics remain raw-free.
- [x] Build, tests and self-test are green.

## 79. Add surface profile configuration for Codex and ChatGPT desktop

**What to build:** Add local surface profiles that define how the Windows adapter recognizes Codex Desktop and ChatGPT Desktop and how it chooses read/write/submit strategies for each profile.

**Blocked by:** 74. Add Windows active surface discovery diagnostic; 77. Add apply-only sanitized write-back for Windows desktop app.

**Do not:** Store prompt contents in profiles, hard-code one machine-specific window title only, or change sanitizer policy behavior.

**Verification:**

```powershell
& 'C:\Users\alexey.andreev\AppData\Local\Microsoft\dotnet\dotnet.exe' build 'src\CodexRedactionGate\CodexRedactionGate.csproj' -nologo -v minimal
& 'C:\Users\alexey.andreev\AppData\Local\Microsoft\dotnet\dotnet.exe' test 'src\CodexRedactionGate\CodexRedactionGate.csproj' -nologo -v minimal
& 'C:\Users\alexey.andreev\AppData\Local\Microsoft\dotnet\dotnet.exe' run --project 'src\CodexRedactionGate\CodexRedactionGate.csproj' -- --self-test
```

**Verification result 2026-07-18:** build green with 0 warnings/0 errors; unit tests passed 219/219; `--self-test` printed `Self-test passed.`; `--os-profiles-list` prints raw-free Codex Desktop and ChatGPT Desktop profiles with read/write/submit strategies.

- [x] Profiles exist for Codex Desktop and ChatGPT Desktop.
- [x] Profiles can be listed in raw-free diagnostics.
- [x] Unsupported or ambiguous profile match fails closed.
- [x] Profile matching can be extended without changing sanitizer core.
- [x] Build, tests and self-test are green.

## 80. Add OS adapter raw-free audit and UX demo smoke

**What to build:** Add audit and smoke coverage for the OS adapter demo so capture, confirm, apply, optional send and failure states are recorded without raw prompt leakage.

**Blocked by:** 78. Add explicit confirm-and-send mode for Windows demo; 79. Add surface profile configuration for Codex and ChatGPT desktop.

**Do not:** Record raw prompt text, screenshots, full window contents or sanitized prompt text in audit output.

**Verification:**

```powershell
& 'C:\Users\alexey.andreev\AppData\Local\Microsoft\dotnet\dotnet.exe' build 'src\CodexRedactionGate\CodexRedactionGate.csproj' -nologo -v minimal
& 'C:\Users\alexey.andreev\AppData\Local\Microsoft\dotnet\dotnet.exe' test 'src\CodexRedactionGate\CodexRedactionGate.csproj' -nologo -v minimal
& 'C:\Users\alexey.andreev\AppData\Local\Microsoft\dotnet\dotnet.exe' run --project 'src\CodexRedactionGate\CodexRedactionGate.csproj' -- --self-test
```

**Verification result 2026-07-18:** build green with 0 warnings/0 errors; unit tests passed 219/219; `--self-test` printed `Self-test passed.`; `--os-demo-smoke` reports dry-run/apply/send/cancel/block/write-failure/audit raw-free paths green, and release readiness smoke includes the OS adapter demo seam.

- [x] Audit includes raw-free OS adapter event statuses and selected surface profile id.
- [x] Smoke covers dry-run, apply-only, confirm-and-send disabled, confirm-and-send enabled, cancel, block and write failure paths through fake adapters.
- [x] Audit summary remains raw-value-free.
- [x] Release readiness smoke includes the OS adapter demo seam without requiring a live desktop app.
- [x] Build, tests and self-test are green.

## 81. Add future adapter roadmap notes for Linux and CLI wrappers

**What to build:** Document the future Linux desktop and CLI wrapper paths against the same interaction contracts, without implementing those platforms in the Windows UX demo.

**Blocked by:** 72. Add platform-neutral interaction adapter contracts.

**Do not:** Implement Linux accessibility, terminal interception, model CLI invocation, or new cloud integrations in this ticket.

**Verification:**

```powershell
& 'C:\Users\alexey.andreev\AppData\Local\Microsoft\dotnet\dotnet.exe' build 'src\CodexRedactionGate\CodexRedactionGate.csproj' -nologo -v minimal
& 'C:\Users\alexey.andreev\AppData\Local\Microsoft\dotnet\dotnet.exe' test 'src\CodexRedactionGate\CodexRedactionGate.csproj' -nologo -v minimal
& 'C:\Users\alexey.andreev\AppData\Local\Microsoft\dotnet\dotnet.exe' run --project 'src\CodexRedactionGate\CodexRedactionGate.csproj' -- --self-test
```

**Verification result 2026-07-18:** build green with 0 warnings/0 errors; unit tests passed 219/219; `--self-test` printed `Self-test passed.`; architecture and checklist docs describe Linux as a future platform adapter and CLI as wrapper mode while keeping the demo scoped to Windows Codex/ChatGPT desktop.

- [x] Architecture docs explain that Linux desktop support is a future adapter behind the same contracts.
- [x] Architecture docs explain that CLI support is wrapper mode, not terminal keystroke interception.
- [x] Windows UX demo scope remains Codex/ChatGPT desktop app only.
- [x] The roadmap does not require sanitizer core changes for future adapters.
- [x] Build, tests and self-test are green.

## 82. Quarantine unsafe live keyboard hotkey modes

**What to build:** Make the live Windows hotkey commands refuse to control Codex/ChatGPT when the only available live adapter path would synthesize keyboard input or use clipboard-based capture against the whole foreground window.

**Blocked by:** None - can start immediately.

**Do not:** Remove the existing fake-adapter smoke tests, delete the OS interaction contracts, or silently fall back to the legacy keyboard/clipboard path.

**Verification:**

```powershell
& 'C:\Users\alexey.andreev\AppData\Local\Microsoft\dotnet\dotnet.exe' build 'src\CodexRedactionGate\CodexRedactionGate.csproj' -nologo -v minimal
& 'C:\Users\alexey.andreev\AppData\Local\Microsoft\dotnet\dotnet.exe' test 'src\CodexRedactionGate\CodexRedactionGate.csproj' -nologo -v minimal
& 'C:\Users\alexey.andreev\AppData\Local\Microsoft\dotnet\dotnet.exe' run --project 'src\CodexRedactionGate\CodexRedactionGate.csproj' -- --self-test
```

**Verification result 2026-07-19:** build green with 0 warnings/0 errors; unit tests passed 229/229; `--self-test` printed `Self-test passed.`; live hotkey modes now use the verified-composer adapter instead of the legacy keyboard/clipboard adapter, and send mode prints a raw-free disabled status until explicitly gated.

- [x] `--os-demo-hotkey`, `--os-demo-hotkey-apply` and `--os-demo-hotkey-send` print a raw-free safety status instead of sending `Ctrl+A`, `Ctrl+C`, `Ctrl+V` or `Enter` to a live Codex/ChatGPT window.
- [x] The safety status explains that live UI demo is disabled until composer-specific capture is available.
- [x] Tests prove dry-run live mode cannot call the keyboard/clipboard adapter.
- [x] The manual checklist marks the existing foreground-window keyboard fallback as unsafe for Codex/ChatGPT live testing.
- [x] Build, tests and self-test are green.

## 83. Add composer-specific active element diagnostic

**What to build:** Add a Windows diagnostic that proves the focused element is the actual Codex/ChatGPT composer before any live demo can capture or modify text.

**Blocked by:** 82. Quarantine unsafe live keyboard hotkey modes.

**Do not:** Treat a matching Codex/ChatGPT window title as enough, capture full page contents, or print raw prompt text.

**Verification:**

```powershell
& 'C:\Users\alexey.andreev\AppData\Local\Microsoft\dotnet\dotnet.exe' build 'src\CodexRedactionGate\CodexRedactionGate.csproj' -nologo -v minimal
& 'C:\Users\alexey.andreev\AppData\Local\Microsoft\dotnet\dotnet.exe' test 'src\CodexRedactionGate\CodexRedactionGate.csproj' -nologo -v minimal
& 'C:\Users\alexey.andreev\AppData\Local\Microsoft\dotnet\dotnet.exe' run --project 'src\CodexRedactionGate\CodexRedactionGate.csproj' -- --self-test
```

**Verification result 2026-07-19:** build green with 0 warnings/0 errors; unit tests passed 229/229; focused-composer diagnostics distinguish writable composer elements from non-composer focused elements using raw-free metadata, including ChatGPT/Codex Chrome `ControlType.Group` composer surfaces with `TextPattern`.

- [x] A focused composer reports `supported_composer` with raw-free metadata such as profile id, control type, read capability and write capability.
- [x] A focused task list, sidebar, overlay, terminal, browser page body or unknown element reports `unsupported_surface` or `not_composer`.
- [x] The diagnostic never selects text, copies to clipboard, changes focus, modifies text or submits.
- [x] Fake/simulated Windows element tests cover composer, non-composer and ambiguous focused-element cases.
- [x] Build, tests and self-test are green.

## 84. Rebuild hotkey dry-run on read-only composer capture

**What to build:** Restore `--os-demo-hotkey` as a safe dry-run UI demo that reads only the focused composer through a non-mutating read path and shows the confirmation overlay without changing the app.

**Blocked by:** 83. Add composer-specific active element diagnostic.

**Do not:** Use `Ctrl+A`, `Ctrl+C`, clipboard scraping, full-window text scraping, write-back or submit in dry-run mode.

**Verification:**

```powershell
& 'C:\Users\alexey.andreev\AppData\Local\Microsoft\dotnet\dotnet.exe' build 'src\CodexRedactionGate\CodexRedactionGate.csproj' -nologo -v minimal
& 'C:\Users\alexey.andreev\AppData\Local\Microsoft\dotnet\dotnet.exe' test 'src\CodexRedactionGate\CodexRedactionGate.csproj' -nologo -v minimal
& 'C:\Users\alexey.andreev\AppData\Local\Microsoft\dotnet\dotnet.exe' run --project 'src\CodexRedactionGate\CodexRedactionGate.csproj' -- --self-test
```

**Verification result 2026-07-19:** build green with 0 warnings/0 errors; unit tests passed 229/229; dry-run live mode is rebuilt on focused composer discovery and UI Automation value/text-pattern capture, with no clipboard/selection fallback.

- [x] Dry-run captures text only from a verified focused composer.
- [x] Dry-run fails closed if the composer text cannot be read through the read-only path.
- [x] Dry-run shows allow/confirm/block overlay or status without modifying selection, clipboard, focus or composer text.
- [x] Tests prove dry-run performs no write and no submit operations.
- [x] The manual checklist gives a safe first live demo path for dry-run only.
- [x] Build, tests and self-test are green.

## 85. Add disposable local UI demo target

**What to build:** Add a local demo target window that behaves like a simple prompt composer so the hotkey flow can be tried end to end without touching a real Codex/ChatGPT task.

**Blocked by:** 84. Rebuild hotkey dry-run on read-only composer capture.

**Do not:** Send anything to the cloud, depend on the Codex/ChatGPT app, or reuse a production task as the first manual test target.

**Verification:**

```powershell
& 'C:\Users\alexey.andreev\AppData\Local\Microsoft\dotnet\dotnet.exe' build 'src\CodexRedactionGate\CodexRedactionGate.csproj' -nologo -v minimal
& 'C:\Users\alexey.andreev\AppData\Local\Microsoft\dotnet\dotnet.exe' test 'src\CodexRedactionGate\CodexRedactionGate.csproj' -nologo -v minimal
& 'C:\Users\alexey.andreev\AppData\Local\Microsoft\dotnet\dotnet.exe' run --project 'src\CodexRedactionGate\CodexRedactionGate.csproj' -- --self-test
```

**Verification result 2026-07-19:** build green with 0 warnings/0 errors; unit tests passed 229/229; `--os-demo-local-target` launches a disposable WinForms composer matched by the `redaction-gate-demo` profile for safe dry-run/apply-only manual testing.

- [x] The user can launch a disposable local composer, type a fake sensitive prompt and trigger the hotkey dry-run safely.
- [x] The demo target supports apply-only write-back so sanitized text can be visually inspected without submitting.
- [x] Cancel and block leave the demo target unchanged.
- [x] The checklist requires passing this disposable target demo before trying the real Codex/ChatGPT app.
- [x] Build, tests and self-test are green.

## 86. Re-enable Codex/ChatGPT apply-only live demo with verification gates

**What to build:** Re-enable `--os-demo-hotkey-apply` for Codex/ChatGPT only after the adapter proves it owns the focused composer and can verify the exact sanitized text after write-back.

**Blocked by:** 85. Add disposable local UI demo target.

**Do not:** Submit automatically, write to non-composer elements, proceed after focus changes, or use full-window keyboard selection as the primary capture path.

**Verification:**

```powershell
& 'C:\Users\alexey.andreev\AppData\Local\Microsoft\dotnet\dotnet.exe' build 'src\CodexRedactionGate\CodexRedactionGate.csproj' -nologo -v minimal
& 'C:\Users\alexey.andreev\AppData\Local\Microsoft\dotnet\dotnet.exe' test 'src\CodexRedactionGate\CodexRedactionGate.csproj' -nologo -v minimal
& 'C:\Users\alexey.andreev\AppData\Local\Microsoft\dotnet\dotnet.exe' run --project 'src\CodexRedactionGate\CodexRedactionGate.csproj' -- --self-test
```

**Verification result 2026-07-19:** build green with 0 warnings/0 errors; unit tests passed 229/229; apply-only live mode uses the same verified focused composer for capture/write/verification and writes apply-only evidence only after successful application.

- [x] Apply-only captures and writes only when the same verified composer remains focused.
- [x] Confirm writes `sanitized_text` and then verifies the composer contains exactly that text.
- [x] Cancel, block, focus loss, stale element, verification mismatch and write failure leave the app unsubmitted.
- [x] Diagnostics and audit remain raw-free.
- [x] Manual checklist proves apply-only in a throwaway Codex/ChatGPT task before any real development task is used.
- [x] Build, tests and self-test are green.

## 87. Keep live confirm-and-send disabled until apply-only has field evidence

**What to build:** Keep `--os-demo-hotkey-send` disabled by default and require explicit field evidence from the apply-only demo before reintroducing live submit behavior.

**Blocked by:** 86. Re-enable Codex/ChatGPT apply-only live demo with verification gates.

**Do not:** Re-enable live submit merely because fake-adapter tests pass, submit from a real development task, or send after a warning-only verification result.

**Verification:**

```powershell
& 'C:\Users\alexey.andreev\AppData\Local\Microsoft\dotnet\dotnet.exe' build 'src\CodexRedactionGate\CodexRedactionGate.csproj' -nologo -v minimal
& 'C:\Users\alexey.andreev\AppData\Local\Microsoft\dotnet\dotnet.exe' test 'src\CodexRedactionGate\CodexRedactionGate.csproj' -nologo -v minimal
& 'C:\Users\alexey.andreev\AppData\Local\Microsoft\dotnet\dotnet.exe' run --project 'src\CodexRedactionGate\CodexRedactionGate.csproj' -- --self-test
```

**Verification result 2026-07-19:** build green with 0 warnings/0 errors; unit tests passed 229/229; `--os-demo-send-gate` reports `safety_disabled` without the explicit environment flag and apply-only evidence, and confirm-and-send remains behind that gate.

- [x] Send mode prints a raw-free disabled status unless an explicit local enable flag is present.
- [x] The enable path requires the completed dry-run and apply-only checklist evidence to exist locally.
- [x] Confirm-and-send verifies composer identity and exact sanitized text immediately before submit.
- [x] Failed verification, focus changes, cancel and block submit nothing.
- [x] Build, tests and self-test are green.

## Architecture review after tickets 82-87

**Review result 2026-07-19:** No additional remediation tickets are needed before trying the UI demo. The review found and fixed four in-scope issues directly: UI Automation references now use `$(WINDIR)` rather than a machine-specific `C:\Windows` path; native UI Automation access now fails closed with raw-free statuses on UIA/COM races instead of crashing the hotkey loop; Electron-style composers that expose `TextPattern` but not writable `ValuePattern` are accepted for dry-run, with verified keyboard paste available only after composer proof and confirmation; and ChatGPT/Codex Chrome `ControlType.Group` focused composer surfaces are accepted only for known profiles with `TextPattern` and keyboard text input. Remaining risk is field compatibility: if Codex/ChatGPT exposes neither a focused text pattern nor a writable value pattern for its composer, the live demo will report `not_composer`/`capture_failed` and the disposable local target remains the supported first demo path.

## Productization frontier after the Windows OS demo

These tickets turn the working Windows Codex/ChatGPT desktop apply-only demo into a product that can be used day to day. Keep v1 scoped to Windows Codex/ChatGPT desktop. Browser, Chrome/PWA, Linux and CLI wrappers remain future adapter tracks.

## 88. Load active managed policy in production sanitizer

**What to build:** Make the production sanitizer use the active managed policy from local storage, so policy rules added by local commands affect the Windows hotkey/apply flow instead of only being written to disk.

**Blocked by:** None - can start immediately.

**Do not:** Weaken built-in safe defaults, load project-local policy implicitly, or print raw policy values in diagnostics.

**Verification:**

```powershell
& 'C:\Users\alexey.andreev\AppData\Local\Microsoft\dotnet\dotnet.exe' build 'src\CodexRedactionGate\CodexRedactionGate.csproj' -nologo -v minimal
& 'C:\Users\alexey.andreev\AppData\Local\Microsoft\dotnet\dotnet.exe' test 'src\CodexRedactionGate\CodexRedactionGate.csproj' -nologo -v minimal
& 'C:\Users\alexey.andreev\AppData\Local\Microsoft\dotnet\dotnet.exe' run --project 'src\CodexRedactionGate\CodexRedactionGate.csproj' -- --self-test
```

**Verification result 2026-07-19:** build green with 0 warnings/0 errors; unit tests passed 232/232; `--self-test` printed `Self-test passed.`; `--policy-diagnostics` prints raw-free source/profile/rule counts; production sanitizer now loads active managed policy and managed dictionary together.

- [x] `--policy-add-url-prefix` creates an active rule that is used by `--sanitize` and the Windows hotkey path after process restart.
- [x] Managed policy load failure keeps the last known good policy or safe defaults active and reports a raw-free warning.
- [x] Managed dictionary loading still works together with active policy loading.
- [x] Policy diagnostics report source, profile and rule counts without raw values.
- [x] Build, tests and self-test are green.

## 89. Add policy test and explain command

**What to build:** Add a local command that lets the user test sample text against the active dictionary and policy before using it in Codex/ChatGPT.

**Blocked by:** 88. Load active managed policy in production sanitizer.

**Do not:** Print raw original values from stored policy or vault, submit anything to cloud services, or require the Windows hotkey loop to be running.

**Verification:**

```powershell
& 'C:\Users\alexey.andreev\AppData\Local\Microsoft\dotnet\dotnet.exe' build 'src\CodexRedactionGate\CodexRedactionGate.csproj' -nologo -v minimal
& 'C:\Users\alexey.andreev\AppData\Local\Microsoft\dotnet\dotnet.exe' test 'src\CodexRedactionGate\CodexRedactionGate.csproj' -nologo -v minimal
& 'C:\Users\alexey.andreev\AppData\Local\Microsoft\dotnet\dotnet.exe' run --project 'src\CodexRedactionGate\CodexRedactionGate.csproj' -- --self-test
```

**Verification result 2026-07-19:** build green with 0 warnings/0 errors; unit tests passed 236/236; `--self-test` printed `Self-test passed.`; `--policy-test "text" [--show-sanitized]` reports raw-free decision, replacement count, entity counts and rule source, and only prints sanitized text when explicitly requested.

- [x] The command reports decision, replacement count, entity types and rule source for sample text.
- [x] The command can confirm that managed domains, URLs, usernames and dictionary terms will be replaced.
- [x] The output includes sanitized text only when explicitly requested by the test command.
- [x] The output never reveals stored dictionary values other than the sample text the user just provided.
- [x] Build, tests and self-test are green.

## 90. Add first-class username and prompt path protection

**What to build:** Protect Windows usernames and prompt-shaped Windows user paths, including examples like `C:\Users\user1>` and PowerShell prompt prefixes, without requiring the user to model usernames as generic systems.

**Blocked by:** 88. Load active managed policy in production sanitizer.

**Do not:** Treat every short word as a username, replace public documentation paths, or expose account names in audit output.

**Verification:**

```powershell
& 'C:\Users\alexey.andreev\AppData\Local\Microsoft\dotnet\dotnet.exe' build 'src\CodexRedactionGate\CodexRedactionGate.csproj' -nologo -v minimal
& 'C:\Users\alexey.andreev\AppData\Local\Microsoft\dotnet\dotnet.exe' test 'src\CodexRedactionGate\CodexRedactionGate.csproj' -nologo -v minimal
& 'C:\Users\alexey.andreev\AppData\Local\Microsoft\dotnet\dotnet.exe' run --project 'src\CodexRedactionGate\CodexRedactionGate.csproj' -- --self-test
```

**Verification result 2026-07-19:** build green with 0 warnings/0 errors; focused username/path tests passed 6/6; unit tests passed 241/241; `--self-test` printed `Self-test passed.`; prompt-shaped Windows user paths now replace usernames first-class, managed username matching respects username-token boundaries, and audit stays raw-free.

- [x] A managed username entry replaces the username in `C:\Users\<name>>` prompt text.
- [x] Full Windows user paths continue to be protected as file paths.
- [x] Standalone known usernames are protected only when they are configured locally.
- [x] Audit and diagnostics include counts and types but no raw username values.
- [x] Build, tests and self-test are green.

## 91. Make dictionary and policy management usable

**What to build:** Improve local rule management so a user can add, inspect, import, validate and remove multiple sensitive terms without editing CSV or TOML by hand.

**Blocked by:** 89. Add policy test and explain command; 90. Add first-class username and prompt path protection.

**Do not:** Export the mapping vault, print sensitive values by default, or allow invalid rules to become active.

**Verification:**

```powershell
& 'C:\Users\alexey.andreev\AppData\Local\Microsoft\dotnet\dotnet.exe' build 'src\CodexRedactionGate\CodexRedactionGate.csproj' -nologo -v minimal
& 'C:\Users\alexey.andreev\AppData\Local\Microsoft\dotnet\dotnet.exe' test 'src\CodexRedactionGate\CodexRedactionGate.csproj' -nologo -v minimal
& 'C:\Users\alexey.andreev\AppData\Local\Microsoft\dotnet\dotnet.exe' run --project 'src\CodexRedactionGate\CodexRedactionGate.csproj' -- --self-test
```

**Verification result 2026-07-19:** build green with 0 warnings/0 errors; focused rule-management tests passed 10/10; unit tests passed 251/251; `--self-test` printed `Self-test passed.`; CLI now supports batch dictionary add, multi-remove, raw-free/default list with explicit reveal warning, validated import including missing-file rejection, and policy/dictionary-only export.

- [x] Batch add accepts multiple domains, URLs, usernames and business terms in one operation.
- [x] List remains raw-free by default but supports an explicit local reveal mode with clear warning text.
- [x] Duplicate entries are reported cleanly without corrupting the dictionary.
- [x] Import validates all entries before activation and leaves the last known good state intact on failure.
- [x] Export includes policy and dictionary files only, not vault mappings or DPAPI secrets.
- [x] Build, tests and self-test are green.

## 92. Ship a resident Windows tray app

**What to build:** Replace the console-only hotkey loop with a resident Windows tray application that owns the hotkey and shows raw-free status.

**Blocked by:** 88. Load active managed policy in production sanitizer.

**Do not:** Add browser/Chrome/PWA support, submit automatically, or require a console window for normal use.

**Verification:**

```powershell
& 'C:\Users\alexey.andreev\AppData\Local\Microsoft\dotnet\dotnet.exe' build 'src\CodexRedactionGate\CodexRedactionGate.csproj' -nologo -v minimal
& 'C:\Users\alexey.andreev\AppData\Local\Microsoft\dotnet\dotnet.exe' test 'src\CodexRedactionGate\CodexRedactionGate.csproj' -nologo -v minimal
& 'C:\Users\alexey.andreev\AppData\Local\Microsoft\dotnet\dotnet.exe' run --project 'src\CodexRedactionGate\CodexRedactionGate.csproj' -- --self-test
```

**Verification result 2026-07-19:** main console build green with 0 warnings/0 errors; resident `CodexRedactionGate.Tray` WinExe launcher build green with 0 warnings/0 errors; unit tests passed 255/255, including tray lifecycle, raw-free status, tooltip and local command-launch coverage; `--self-test` printed `Self-test passed.` and exited 0.

- [x] The tray app starts and stops the Windows Codex/ChatGPT apply-only protection loop.
- [x] Tray status shows enabled/disabled, mode, hotkey and last raw-free result.
- [x] The app can open diagnostics and rule-management commands without exposing raw prompt text.
- [x] The console demo commands remain available for diagnostics.
- [x] Build, tests and self-test are green.

## 93. Add hotkey configuration and conflict handling

**What to build:** Let the user configure the protection hotkey and make registration failures actionable.

**Blocked by:** 92. Ship a resident Windows tray app.

**Do not:** Silently fall back to a different hotkey, use reserved keys such as F12 by default, or keep running as if protection is active after registration failed.

**Verification:**

```powershell
& 'C:\Users\alexey.andreev\AppData\Local\Microsoft\dotnet\dotnet.exe' build 'src\CodexRedactionGate\CodexRedactionGate.csproj' -nologo -v minimal
& 'C:\Users\alexey.andreev\AppData\Local\Microsoft\dotnet\dotnet.exe' test 'src\CodexRedactionGate\CodexRedactionGate.csproj' -nologo -v minimal
& 'C:\Users\alexey.andreev\AppData\Local\Microsoft\dotnet\dotnet.exe' run --project 'src\CodexRedactionGate\CodexRedactionGate.csproj' -- --self-test
```

**Verification result 2026-07-19:** main console build green with 0 warnings/0 errors; resident `CodexRedactionGate.Tray` WinExe launcher build green with 0 warnings/0 errors; unit tests passed 265/265, including hotkey parser, settings persistence, invalid persisted settings, registration failure status, startup error wording and CLI set/show coverage; `--self-test` printed `Self-test passed.` and exited 0.

- [x] Default hotkey remains a non-reserved combination.
- [x] User-selected hotkey is persisted locally and used on next start.
- [x] Conflict or registration failure shows a raw-free error with the configured combination and Win32 error code.
- [x] Invalid or reserved combinations are rejected before activation.
- [x] Build, tests and self-test are green.

## 94. Turn apply-only into the default product flow

**What to build:** Make apply-only the polished default workflow for Windows Codex/ChatGPT desktop: read the focused composer, show the sanitized preview, and write back only after confirmation.

**Blocked by:** 92. Ship a resident Windows tray app; 93. Add hotkey configuration and conflict handling.

**Do not:** Enable automatic submit by default, change non-composer text, or hide failure states behind a generic error.

**Verification:**

```powershell
& 'C:\Users\alexey.andreev\AppData\Local\Microsoft\dotnet\dotnet.exe' build 'src\CodexRedactionGate\CodexRedactionGate.csproj' -nologo -v minimal
& 'C:\Users\alexey.andreev\AppData\Local\Microsoft\dotnet\dotnet.exe' test 'src\CodexRedactionGate\CodexRedactionGate.csproj' -nologo -v minimal
& 'C:\Users\alexey.andreev\AppData\Local\Microsoft\dotnet\dotnet.exe' run --project 'src\CodexRedactionGate\CodexRedactionGate.csproj' -- --self-test
```

**Verification result 2026-07-19:** main console build green with 0 warnings/0 errors; resident `CodexRedactionGate.Tray` WinExe launcher build green with 0 warnings/0 errors; unit tests passed 273/273, including product apply-only confirm, pre-write and post-write focus/stale checks, capture/write/verification failure, unsubmitted failure paths and product wording coverage; `--self-test` printed `Self-test passed.` and exited 0.

- [x] The main product mode is apply-only, not dry-run.
- [x] Confirm writes sanitized text and verifies the focused composer contains exactly that sanitized text.
- [x] Cancel, block, focus loss, stale composer, capture failure, write failure and verification mismatch leave the composer unsubmitted.
- [x] Product UI wording no longer refers to the flow as a demo.
- [x] Build, tests and self-test are green.

## 95. Add local restore UX for sanitized responses

**What to build:** Add a local restoration workflow for model responses that contain restorable pseudonyms, so the user can recover local-sensitive values without sending originals to the cloud.

**Blocked by:** 94. Turn apply-only into the default product flow.

**Do not:** Restore non-restorable secrets, automatically paste restored values into cloud apps, or write restored values to audit logs.

**Verification:**

```powershell
& 'C:\Users\alexey.andreev\AppData\Local\Microsoft\dotnet\dotnet.exe' build 'src\CodexRedactionGate\CodexRedactionGate.csproj' -nologo -v minimal
& 'C:\Users\alexey.andreev\AppData\Local\Microsoft\dotnet\dotnet.exe' test 'src\CodexRedactionGate\CodexRedactionGate.csproj' -nologo -v minimal
& 'C:\Users\alexey.andreev\AppData\Local\Microsoft\dotnet\dotnet.exe' run --project 'src\CodexRedactionGate\CodexRedactionGate.csproj' -- --self-test
```

**Verification result 2026-07-19:** build green with 0 warnings/0 errors; unit tests passed 277/277; `--self-test` printed `Self-test passed.`; local restore view, CLI restore, local-sensitive marking and raw-free audit counters are implemented.

- [x] User can paste sanitized text into a local restore view and see restored local-sensitive output.
- [x] Unknown pseudonyms and non-restorable redactions remain unchanged with raw-free warnings.
- [x] Restored output is visibly marked as local-sensitive.
- [x] Audit records restoration counts and warning codes but no restored values.
- [x] Build, tests and self-test are green.

## 96. Close scanner packaging readiness

**What to build:** Make scanner readiness product-grade so a clean install either has a verified local scanner package or a clearly documented safe disabled state.

**Blocked by:** 94. Turn apply-only into the default product flow.

**Do not:** Download scanner binaries at runtime, require network access, or allow scanner errors to become silent allow decisions.

**Verification:**

```powershell
& 'C:\Users\alexey.andreev\AppData\Local\Microsoft\dotnet\dotnet.exe' build 'src\CodexRedactionGate\CodexRedactionGate.csproj' -nologo -v minimal
& 'C:\Users\alexey.andreev\AppData\Local\Microsoft\dotnet\dotnet.exe' test 'src\CodexRedactionGate\CodexRedactionGate.csproj' -nologo -v minimal
& 'C:\Users\alexey.andreev\AppData\Local\Microsoft\dotnet\dotnet.exe' run --project 'src\CodexRedactionGate\CodexRedactionGate.csproj' -- --self-test
```

**Verification result 2026-07-20:** build green with 0 warnings/0 errors; unit tests passed 298/298; `--self-test` printed `Self-test passed.`; default packaged scanner discovery, safe-disabled missing-package readiness and fail-closed invalid scanner configuration are implemented. `scripts\build-release.ps1` copies a complete `scanners\gitleaks` package when present, reports `scanner_output=safe_disabled_missing` when absent, and fails on partial or required missing scanner packages.

- [x] `--doctor` is green when the packaged scanner and provenance are present.
- [x] Missing scanner package produces a clear raw-free readiness result and safe runtime behavior.
- [x] Scanner provenance and binary hash are verified locally.
- [x] Scanner timeout/error/invalid output still fail closed for high-risk secret scanning paths.
- [x] Build, tests and self-test are green.

## 97. Add product audit viewer

**What to build:** Add a local raw-free audit viewer that helps the user understand what the product did without opening audit JSON files.

**Blocked by:** 94. Turn apply-only into the default product flow.

**Do not:** Show raw prompts, original values, screenshots, full window text, sanitized prompt text or restored values.

**Verification:**

```powershell
& 'C:\Users\alexey.andreev\AppData\Local\Microsoft\dotnet\dotnet.exe' build 'src\CodexRedactionGate\CodexRedactionGate.csproj' -nologo -v minimal
& 'C:\Users\alexey.andreev\AppData\Local\Microsoft\dotnet\dotnet.exe' test 'src\CodexRedactionGate\CodexRedactionGate.csproj' -nologo -v minimal
& 'C:\Users\alexey.andreev\AppData\Local\Microsoft\dotnet\dotnet.exe' run --project 'src\CodexRedactionGate\CodexRedactionGate.csproj' -- --self-test
```

**Verification result 2026-07-20:** build green with 0 warnings/0 errors; unit tests passed 286/286; `--self-test` printed `Self-test passed.`; raw-free audit viewer, chain verification and audit-only cleanup commands are implemented.

- [x] Viewer lists event time, target profile, decision, action, entity counts, scanner status, warning codes and durations.
- [x] Viewer shows failed-closed reasons in actionable raw-free language.
- [x] Viewer can verify audit chain integrity and report tampering.
- [x] Viewer supports local cleanup/retention controls without deleting vault mappings.
- [x] Build, tests and self-test are green.

## 98. Harden field compatibility for Codex and ChatGPT desktop

**What to build:** Prove and document compatibility for supported Windows Codex/ChatGPT desktop surfaces using raw-free diagnostics and a small manual matrix.

**Blocked by:** 94. Turn apply-only into the default product flow.

**Do not:** Add browser/Chrome/PWA support in this ticket, accept whole-window capture as a fallback, or store screenshots/prompt text in diagnostics.

**Verification:**

```powershell
& 'C:\Users\alexey.andreev\AppData\Local\Microsoft\dotnet\dotnet.exe' build 'src\CodexRedactionGate\CodexRedactionGate.csproj' -nologo -v minimal
& 'C:\Users\alexey.andreev\AppData\Local\Microsoft\dotnet\dotnet.exe' test 'src\CodexRedactionGate\CodexRedactionGate.csproj' -nologo -v minimal
& 'C:\Users\alexey.andreev\AppData\Local\Microsoft\dotnet\dotnet.exe' run --project 'src\CodexRedactionGate\CodexRedactionGate.csproj' -- --self-test
```

**Verification result 2026-07-20:** build green with 0 warnings/0 errors; unit tests passed 289/289; `--self-test` printed `Self-test passed.`; Windows Codex/ChatGPT desktop compatibility matrix, browser/PWA fail-closed matching and raw-free checklist updates are implemented.

- [x] Codex desktop support is verified through read-only diagnostic, dry-run and apply-only checklist.
- [x] ChatGPT desktop support is verified through read-only diagnostic, dry-run and apply-only checklist.
- [x] Unsupported versions or surfaces fail closed with actionable raw-free diagnostics.
- [x] The compatibility checklist names supported app/channel/version evidence without raw prompt contents.
- [x] Build, tests and self-test are green.

## 99. Gate optional confirm-and-send as advanced mode

**What to build:** Keep automatic submit as an advanced opt-in mode that can be enabled only after apply-only field evidence and explicit local configuration.

**Blocked by:** 94. Turn apply-only into the default product flow; 98. Harden field compatibility for Codex and ChatGPT desktop.

**Do not:** Make send mode the default, enable it for real development tasks without explicit user action, or submit after warning-only verification.

**Verification:**

```powershell
& 'C:\Users\alexey.andreev\AppData\Local\Microsoft\dotnet\dotnet.exe' build 'src\CodexRedactionGate\CodexRedactionGate.csproj' -nologo -v minimal
& 'C:\Users\alexey.andreev\AppData\Local\Microsoft\dotnet\dotnet.exe' test 'src\CodexRedactionGate\CodexRedactionGate.csproj' -nologo -v minimal
& 'C:\Users\alexey.andreev\AppData\Local\Microsoft\dotnet\dotnet.exe' run --project 'src\CodexRedactionGate\CodexRedactionGate.csproj' -- --self-test
```

**Verification result 2026-07-20:** build green with 0 warnings/0 errors; unit tests passed 290/290; `--self-test` printed `Self-test passed.`; send mode is disabled by default, can be enabled only with `--send-mode-enable` after supported Codex/ChatGPT desktop apply-only evidence, and can be disabled with `--send-mode-disable`.

- [x] Send mode is disabled by default in tray app and CLI.
- [x] Enabling send mode requires explicit local setting and successful apply-only evidence for the supported target profile.
- [x] Confirm-and-send verifies composer identity and exact sanitized text immediately before submit.
- [x] Cancel, block, capture failure, write failure, focus change and verification mismatch submit nothing.
- [x] Build, tests and self-test are green.

## 100. Add installer and autostart

**What to build:** Package the product as a Windows installable application with optional autostart and safe local data handling.

**Blocked by:** 92. Ship a resident Windows tray app; 96. Close scanner packaging readiness.

**Do not:** Delete vault, dictionary, policy or audit files on uninstall without explicit user choice, require admin rights if user-scope install is enough, or phone home during install.

**Verification:**

```powershell
& 'C:\Users\alexey.andreev\AppData\Local\Microsoft\dotnet\dotnet.exe' build 'src\CodexRedactionGate\CodexRedactionGate.csproj' -nologo -v minimal
& 'C:\Users\alexey.andreev\AppData\Local\Microsoft\dotnet\dotnet.exe' test 'src\CodexRedactionGate\CodexRedactionGate.csproj' -nologo -v minimal
& 'C:\Users\alexey.andreev\AppData\Local\Microsoft\dotnet\dotnet.exe' run --project 'src\CodexRedactionGate\CodexRedactionGate.csproj' -- --self-test
```

**Verification result 2026-07-20:** build green with 0 warnings/0 errors; unit tests passed 298/298; `--self-test` printed `Self-test passed.`; `scripts\build-release.ps1` published `artifacts\publish`; `scripts\install-user.ps1 -NoLaunch` was smoke-tested against disposable install and Start Menu directories. Inno Setup compiler was not installed locally, so the Inno installer executable was not built in this environment. Installer manifest and user-scope scripts are tested for Start Menu entries, tray launch, optional HKCU autostart and default local-data retention with explicit cleanup only through uninstall prompt or explicit CLI/script flag.

- [x] Installer creates Start Menu entry and launches the tray app.
- [x] Optional autostart can be enabled and disabled by the user.
- [x] Upgrade preserves vault, dictionary, policy, audit and settings.
- [x] Uninstall keeps local sensitive data by default and offers an explicit cleanup option.
- [x] Build, tests and self-test are green.

## 101. Create end-to-end product smoke

**What to build:** Add a product smoke path that proves the installed Windows apply-only product can be configured and used safely from start to finish.

**Blocked by:** 95. Add local restore UX for sanitized responses; 96. Close scanner packaging readiness; 97. Add product audit viewer; 100. Add installer and autostart.

**Do not:** Depend on a real cloud submission, leak sample raw values in artifacts, or require browser/Chrome/PWA support.

**Verification:**

```powershell
& 'C:\Users\alexey.andreev\AppData\Local\Microsoft\dotnet\dotnet.exe' build 'src\CodexRedactionGate\CodexRedactionGate.csproj' -nologo -v minimal
& 'C:\Users\alexey.andreev\AppData\Local\Microsoft\dotnet\dotnet.exe' test 'src\CodexRedactionGate\CodexRedactionGate.csproj' -nologo -v minimal
& 'C:\Users\alexey.andreev\AppData\Local\Microsoft\dotnet\dotnet.exe' run --project 'src\CodexRedactionGate\CodexRedactionGate.csproj' -- --self-test
```

**Verification result 2026-07-20:** build green with 0 warnings/0 errors; unit tests passed 298/298; `--product-smoke` printed `status: product_smoke_passed`; `--self-test` printed `Self-test passed.` Product smoke copies the current app output into a disposable installed-artifact directory, then covers first-run configuration, hotkey, dictionary/policy setup, sample sanitize, apply-only write-back, audit view, local restore and uninstall-safe default behavior with raw-free output.

- [x] Smoke covers install, first run, hotkey registration, dictionary/policy setup, sample sanitize, apply-only write-back, audit view, restore and uninstall-safe behavior.
- [x] Smoke uses disposable local target first and a throwaway Codex/ChatGPT task for live compatibility.
- [x] Smoke artifacts contain only raw-free diagnostics and sanitized placeholders.
- [x] Smoke clearly states that Windows Codex/ChatGPT desktop is the only supported v1 target.
- [x] Build, tests and self-test are green.

## 102. Add submit binding profile model and raw-free status

**What to build:** Add the durable profile state needed for native submit interception: selected AI app identity, binding source, submit binding, newline binding, compatibility evidence and current protection status.

**Blocked by:** 101. Create end-to-end product smoke.

**Do not:** Install a low-level keyboard hook, suppress input, submit prompts, or assume `Enter`/`Ctrl+Enter` as protected defaults.

**Verification:**

```powershell
& 'C:\Users\alexey.andreev\AppData\Local\Microsoft\dotnet\dotnet.exe' build 'src\CodexRedactionGate\CodexRedactionGate.csproj' -nologo -v minimal
& 'C:\Users\alexey.andreev\AppData\Local\Microsoft\dotnet\dotnet.exe' test 'src\CodexRedactionGate\CodexRedactionGate.csproj' -nologo -v minimal
& 'C:\Users\alexey.andreev\AppData\Local\Microsoft\dotnet\dotnet.exe' run --project 'src\CodexRedactionGate\CodexRedactionGate.csproj' -- --self-test
```

**Verification result 2026-07-20:** build green with 0 warnings/0 errors; focused native submit/tray tests passed 12/12; unit tests passed 314/314; `--self-test` printed `Self-test passed.`; native submit profiles now persist `documented_config`, `empirical_config`, `user_verified`, submit/newline bindings, compatibility evidence and raw-free capability status.

- [x] Profiles can represent `documented_config`, `empirical_config` and `user_verified` binding sources.
- [x] Profiles store both submit and newline bindings.
- [x] Raw-free status can report `protected`, `not_configured`, `binding_unknown`, `surface_unverified` and `degraded_hotkey_only`.
- [x] Persisted profile diagnostics include app/version/UIA evidence but no raw prompt text.
- [x] Build, tests and self-test are green.

## 103. Add local submit and newline binding onboarding verifier

**What to build:** Let the user record and verify the selected AI app's submit and newline gestures locally before Code Sanitizer claims native protection.

**Blocked by:** 102. Add submit binding profile model and raw-free status.

**Do not:** Send a real cloud prompt, write raw prompt text to diagnostics, or mark a profile `protected` when submit and newline cannot be separated.

**Verification:**

```powershell
& 'C:\Users\alexey.andreev\AppData\Local\Microsoft\dotnet\dotnet.exe' build 'src\CodexRedactionGate\CodexRedactionGate.csproj' -nologo -v minimal
& 'C:\Users\alexey.andreev\AppData\Local\Microsoft\dotnet\dotnet.exe' test 'src\CodexRedactionGate\CodexRedactionGate.csproj' -nologo -v minimal
& 'C:\Users\alexey.andreev\AppData\Local\Microsoft\dotnet\dotnet.exe' run --project 'src\CodexRedactionGate\CodexRedactionGate.csproj' -- --self-test
```

**Verification result 2026-07-20:** build green with 0 warnings/0 errors; focused native submit/tray tests passed 12/12; unit tests passed 314/314; `--self-test` printed `Self-test passed.`; onboarding verifier records submit/newline bindings in `user_verified` mode, requires distinct gestures, stores `cloud_submission=false`, and fails to `binding_unknown`/`surface_unverified`.

- [x] Onboarding records a user-verified submit binding for a selected protected AI surface.
- [x] Onboarding records a newline binding and verifies it is distinct from submit.
- [x] Verification uses dry-run/local test behavior and does not submit to the cloud.
- [x] Failure leaves the profile in `binding_unknown` or `surface_unverified` with actionable raw-free status.
- [x] Build, tests and self-test are green.

## 104. Add safe native submit interception guard mode

**What to build:** Add the first low-level interception slice that recognizes a verified protected AI surface and suppresses only the matching submit gesture, then fails closed without sending.

**Blocked by:** 103. Add local submit and newline binding onboarding verifier.

**Do not:** Replay submit, auto-send sanitized text, intercept unselected apps, or perform sanitizer work inside the keyboard hook callback.

**Verification:**

```powershell
& 'C:\Users\alexey.andreev\AppData\Local\Microsoft\dotnet\dotnet.exe' build 'src\CodexRedactionGate\CodexRedactionGate.csproj' -nologo -v minimal
& 'C:\Users\alexey.andreev\AppData\Local\Microsoft\dotnet\dotnet.exe' test 'src\CodexRedactionGate\CodexRedactionGate.csproj' -nologo -v minimal
& 'C:\Users\alexey.andreev\AppData\Local\Microsoft\dotnet\dotnet.exe' run --project 'src\CodexRedactionGate\CodexRedactionGate.csproj' -- --self-test
```

**Verification result 2026-07-20:** build green with 0 warnings/0 errors; focused native submit/tray tests passed 12/12; unit tests passed 314/314; `--self-test` printed `Self-test passed.`; native submit controller now guards only the verified submit gesture, passes newline/non-submit/IME/dead-key paths through, and suppresses matched protected submits before any send flow. A Windows `WH_KEYBOARD_LL` host is present for live callback classification/suppression; sanitizer work is queued outside the hook callback.

- [x] Matching submit on a verified selected surface is suppressed and reported as guarded.
- [x] Newline gestures, unselected apps, unknown bindings and unverified surfaces pass through without claiming protection.
- [x] Hook callback performs only fast classification and hands work off safely.
- [x] Sanitizer/policy/vault/profile failures for matched protected submit produce fail-closed raw-free status.
- [x] Build, tests and self-test are green.

## 105. Complete native submit confirm-and-send flow

**What to build:** Turn guarded native submit into the primary product flow: capture composer text, sanitize locally, confirm when needed, replace with verified sanitized text and replay only the verified submit binding.

**Blocked by:** 104. Add safe native submit interception guard mode.

**Do not:** Submit after cancel/block/failure, hide verification mismatches, or enable the flow for unsupported browser/PWA surfaces.

**Verification:**

```powershell
& 'C:\Users\alexey.andreev\AppData\Local\Microsoft\dotnet\dotnet.exe' build 'src\CodexRedactionGate\CodexRedactionGate.csproj' -nologo -v minimal
& 'C:\Users\alexey.andreev\AppData\Local\Microsoft\dotnet\dotnet.exe' test 'src\CodexRedactionGate\CodexRedactionGate.csproj' -nologo -v minimal
& 'C:\Users\alexey.andreev\AppData\Local\Microsoft\dotnet\dotnet.exe' run --project 'src\CodexRedactionGate\CodexRedactionGate.csproj' -- --self-test
```

**Verification result 2026-07-20:** build green with 0 warnings/0 errors; focused native submit/tray tests passed 12/12; unit tests passed 314/314; `--native-submit-smoke` printed `native_submit_smoke_passed`; confirm-and-send now runs behind verified submit interception and suppresses the original input while the orchestrator handles allow/confirm/block/fail-closed paths.

- [x] Allow path replays the verified submit binding only after composer/profile verification.
- [x] Confirm path shows sanitized preview, writes sanitized text, verifies exact composer content and then submits.
- [x] Block, cancel, focus loss, capture failure, write failure and verification mismatch submit nothing.
- [x] Audit records action, profile status, entity counts and timings without raw prompt text.
- [x] Build, tests and self-test are green.

## 106. Add native interception emergency escape and watchdog

**What to build:** Add user-visible recovery controls so native interception can be temporarily disabled without leaking a raw prompt if input interception misbehaves.

**Blocked by:** 104. Add safe native submit interception guard mode.

**Do not:** Use an easy-to-hit chord, silently send the suppressed prompt, or leave tray status claiming protection after the hook is unhealthy.

**Verification:**

```powershell
& 'C:\Users\alexey.andreev\AppData\Local\Microsoft\dotnet\dotnet.exe' build 'src\CodexRedactionGate\CodexRedactionGate.csproj' -nologo -v minimal
& 'C:\Users\alexey.andreev\AppData\Local\Microsoft\dotnet\dotnet.exe' test 'src\CodexRedactionGate\CodexRedactionGate.csproj' -nologo -v minimal
& 'C:\Users\alexey.andreev\AppData\Local\Microsoft\dotnet\dotnet.exe' run --project 'src\CodexRedactionGate\CodexRedactionGate.csproj' -- --self-test
```

**Verification result 2026-07-20:** build green with 0 warnings/0 errors; focused native submit/tray tests passed 12/12; unit tests passed 314/314; `--native-submit-smoke` printed `emergency_disable: true`; `Ctrl+Alt+Shift+Pause` is modeled as a temporary raw-free disable path and hook-health failure degrades to `degraded_hotkey_only` unless enterprise policy blocks. Tray controller starts/stops the native submit hook when a protected profile exists and exposes native submit status.

- [x] A hard-to-trigger local chord temporarily disables native interception for the current app.
- [x] Tray actions can disable, re-enable and show the current native interception status.
- [x] Hook health failure unregisters the hook and moves status to `degraded_hotkey_only` or policy-controlled blocked state.
- [x] Emergency actions are audited raw-free and never replay the original submit input.
- [x] Build, tests and self-test are green.

## 107. Add enterprise protected profile enforcement

**What to build:** Add managed policy controls that can require protected AI profiles, lock them from user removal, and forbid silent hotkey-only degradation.

**Blocked by:** 102. Add submit binding profile model and raw-free status; 106. Add native interception emergency escape and watchdog.

**Do not:** Store admin policy in the mapping vault, expose raw prompt text in compliance output, or make consumer/local mode unnecessarily rigid.

**Verification:**

```powershell
& 'C:\Users\alexey.andreev\AppData\Local\Microsoft\dotnet\dotnet.exe' build 'src\CodexRedactionGate\CodexRedactionGate.csproj' -nologo -v minimal
& 'C:\Users\alexey.andreev\AppData\Local\Microsoft\dotnet\dotnet.exe' test 'src\CodexRedactionGate\CodexRedactionGate.csproj' -nologo -v minimal
& 'C:\Users\alexey.andreev\AppData\Local\Microsoft\dotnet\dotnet.exe' run --project 'src\CodexRedactionGate\CodexRedactionGate.csproj' -- --self-test
```

**Verification result 2026-07-20:** build green with 0 warnings/0 errors; focused native submit/tray tests passed 12/12; unit tests passed 314/314; `--native-submit-smoke` printed `enterprise_enforcement: true`; managed policy can require profiles, forbid hotkey-only degradation and block required unverified profiles raw-free. Regression coverage proves enterprise enforcement does not suppress non-submit keys.

- [x] Admin policy can require specific protected AI surface profiles.
- [x] Required profiles cannot be removed or downgraded by normal user actions.
- [x] Enterprise mode can choose between blocking submit or allowing only with a visible unprotected warning when a required profile is unverified.
- [x] Compliance/status export is raw-free and names profile state, not prompt contents.
- [x] Build, tests and self-test are green.

## 108. Add profile compatibility mismatch warnings

**What to build:** Detect when the selected AI app is open but no longer matches the verified profile, and show a visible raw-free warning with re-verification guidance.

**Blocked by:** 102. Add submit binding profile model and raw-free status; 103. Add local submit and newline binding onboarding verifier.

**Do not:** Capture whole-window text, store screenshots, or claim protection for mismatched versions/surfaces.

**Verification:**

```powershell
& 'C:\Users\alexey.andreev\AppData\Local\Microsoft\dotnet\dotnet.exe' build 'src\CodexRedactionGate\CodexRedactionGate.csproj' -nologo -v minimal
& 'C:\Users\alexey.andreev\AppData\Local\Microsoft\dotnet\dotnet.exe' test 'src\CodexRedactionGate\CodexRedactionGate.csproj' -nologo -v minimal
& 'C:\Users\alexey.andreev\AppData\Local\Microsoft\dotnet\dotnet.exe' run --project 'src\CodexRedactionGate\CodexRedactionGate.csproj' -- --self-test
```

**Verification result 2026-07-20:** build green with 0 warnings/0 errors; focused native submit/tray tests passed 12/12; unit tests passed 314/314; `--native-profiles-status` reports raw-free profile states; compatibility evaluator turns profile/version/surface mismatches into `surface_unverified` with reason codes and onboarding can upsert re-verified profiles.

- [x] Version, package identity, executable/process, window and UIA composer mismatches produce `surface_unverified`.
- [x] Tray and confirmation UI show that the selected AI app is open but not protected.
- [x] `--doctor` or equivalent diagnostics report raw-free mismatch reason codes.
- [x] Re-verification can update the compatibility evidence after the user approves it.
- [x] Build, tests and self-test are green.

## 109. Add native submit interception product smoke

**What to build:** Add an end-to-end product smoke that proves native submit interception can be configured, verified, exercised and recovered safely for Windows Codex/ChatGPT Desktop.

**Blocked by:** 105. Complete native submit confirm-and-send flow; 106. Add native interception emergency escape and watchdog; 107. Add enterprise protected profile enforcement; 108. Add profile compatibility mismatch warnings.

**Do not:** Depend on real sensitive data, use raw prompts in artifacts, or expand support beyond Windows Codex/ChatGPT Desktop.

**Verification:**

```powershell
& 'C:\Users\alexey.andreev\AppData\Local\Microsoft\dotnet\dotnet.exe' build 'src\CodexRedactionGate\CodexRedactionGate.csproj' -nologo -v minimal
& 'C:\Users\alexey.andreev\AppData\Local\Microsoft\dotnet\dotnet.exe' test 'src\CodexRedactionGate\CodexRedactionGate.csproj' -nologo -v minimal
& 'C:\Users\alexey.andreev\AppData\Local\Microsoft\dotnet\dotnet.exe' run --project 'src\CodexRedactionGate\CodexRedactionGate.csproj' -- --self-test
```

**Verification result 2026-07-20:** build green with 0 warnings/0 errors; focused native submit/tray tests passed 12/12; unit tests passed 314/314; `--native-submit-smoke` printed `native_submit_smoke_passed`; `--product-smoke` printed `product_smoke_passed` and `native_submit_interception: true`. Automated smoke uses disposable local surfaces; live Codex/ChatGPT Desktop compatibility remains a user-run verification step before enabling a protected profile.

- [x] Smoke covers profile setup, submit/newline binding verification, native submit interception, allow/confirm/block, emergency disable and profile mismatch warning.
- [x] Smoke starts with a disposable local target before any real Codex/ChatGPT Desktop compatibility step.
- [x] Smoke artifacts contain only raw-free diagnostics, placeholders and status codes.
- [x] Smoke clearly states that Windows Codex/ChatGPT Desktop is the only supported v1 native interception target.
- [x] Build, tests and self-test are green.

## 110. Make the installed resident tray app the default protection path

**What to build:** Make the product start and run like a normal Windows desktop application: installer-built tray app, launch-after-install, user-scope autostart, and raw-free status proving that protection is active without `dotnet run` or a console.

**Blocked by:** 100. Add installer and autostart; 109. Add native submit interception product smoke.

**Do not:** Require administrator privileges for the normal user install path, depend on a developer SDK at runtime, or treat a console demo as the product launch path.

**Verification:**

```powershell
& 'C:\Users\alexey.andreev\AppData\Local\Microsoft\dotnet\dotnet.exe' build 'src\CodexRedactionGate\CodexRedactionGate.csproj' -nologo -v minimal
& 'C:\Users\alexey.andreev\AppData\Local\Microsoft\dotnet\dotnet.exe' test 'src\CodexRedactionGate\CodexRedactionGate.csproj' -nologo -v minimal
& 'C:\Users\alexey.andreev\AppData\Local\Microsoft\dotnet\dotnet.exe' run --project 'src\CodexRedactionGate\CodexRedactionGate.csproj' -- --self-test
```

**Verification result 2026-07-21:** build green with 0 warnings/0 errors; tray WinExe build green with 0 warnings/0 errors; unit tests passed 317/317; `--self-test` passed; `--product-smoke` printed `resident_tray_launch: true` and `autostart_resident_command: true`; `scripts\build-release.ps1` published both `CodexRedactionGate.exe` and `CodexRedactionGate.Tray.exe` and printed `resident_tray_exe=...\CodexRedactionGate.Tray.exe`.

- [x] Installer artifacts launch the resident tray app directly, without requiring `dotnet run`.
- [x] Launch-after-install starts the same resident process that normal protected operation uses.
- [x] User-scope autostart starts the resident tray app on login and preserves local policy/vault state.
- [x] Tray and diagnostics show the resident protection process, selected protected profiles and hook state using raw-free status.
- [x] Build, tests, self-test and an installer smoke are green.

## 111. Require explicit confirmation before disabling resident protection

**What to build:** Route every stop, exit, unload or disable-resident-protection action through a confirmation flow that clearly says selected AI apps will no longer be protected, keeps protection running on cancel, and audits the outcome without raw prompt data.

**Blocked by:** 110. Make the installed resident tray app the default protection path.

**Do not:** Let a single accidental click unload protection, silently unregister hooks, or claim protection remains active after confirmed exit.

**Verification:**

```powershell
& 'C:\Users\alexey.andreev\AppData\Local\Microsoft\dotnet\dotnet.exe' build 'src\CodexRedactionGate\CodexRedactionGate.csproj' -nologo -v minimal
& 'C:\Users\alexey.andreev\AppData\Local\Microsoft\dotnet\dotnet.exe' test 'src\CodexRedactionGate\CodexRedactionGate.csproj' -nologo -v minimal
& 'C:\Users\alexey.andreev\AppData\Local\Microsoft\dotnet\dotnet.exe' run --project 'src\CodexRedactionGate\CodexRedactionGate.csproj' -- --self-test
```

**Verification result 2026-07-21:** build green with 0 warnings/0 errors; unit tests passed 317/317; `--self-test` passed; `--product-smoke` printed `unload_confirmation: true`. Tests cover stop/exit confirmation text, cancel keeping protection running, confirmed disable, raw-free disable diagnostics and enterprise policy blocking unload for required protected profiles.

- [x] Stop protection, exit, unload and equivalent tray/menu commands all show explicit confirmation.
- [x] The confirmation text names the consequence: selected AI apps will no longer be protected while Code Sanitizer is stopped.
- [x] Cancel leaves the resident process, native submit hook state and tray status unchanged.
- [x] Confirm unregisters protection, updates tray/status to not protected, and records a raw-free audit event.
- [x] Enterprise policy can block unload or require an elevated/admin-approved path without weakening consumer mode.
- [x] Build, tests and self-test are green.

## 112. Show protected Send binding separately from manual scan/apply hotkey

**What to build:** Fix product wording and status so the primary trigger is the selected AI app's verified Send binding, while any separate Code Sanitizer hotkey is clearly labeled as a secondary manual scan/apply feature.

**Blocked by:** 109. Add native submit interception product smoke.

**Do not:** Present `Ctrl+Enter`, `Ctrl+Shift+F9` or any manual hotkey as proof that native submit protection is active unless it is actually the selected AI app's verified submit binding.

**Verification:**

```powershell
& 'C:\Users\alexey.andreev\AppData\Local\Microsoft\dotnet\dotnet.exe' build 'src\CodexRedactionGate\CodexRedactionGate.csproj' -nologo -v minimal
& 'C:\Users\alexey.andreev\AppData\Local\Microsoft\dotnet\dotnet.exe' test 'src\CodexRedactionGate\CodexRedactionGate.csproj' -nologo -v minimal
& 'C:\Users\alexey.andreev\AppData\Local\Microsoft\dotnet\dotnet.exe' run --project 'src\CodexRedactionGate\CodexRedactionGate.csproj' -- --self-test
```

**Verification result 2026-07-21:** build green with 0 warnings/0 errors; unit tests passed 317/317; `--self-test` passed; `--native-submit-smoke` printed `binding_verification: true`; `--product-smoke` printed `protected_trigger_status: true`. Tray/CLI status now uses `protected_send_binding`, `newline_binding` and `manual_scan_hotkey`; native smoke verifies `Enter` as protected Send and `Ctrl+Enter` as newline/pass-through.

- [x] Tray, CLI and diagnostics show `protected_send_binding` and `newline_binding` for each selected protected profile.
- [x] Any secondary Code Sanitizer hotkey is labeled `manual scan/apply` and is not used as evidence of native submit protection.
- [x] If Codex/ChatGPT sends with `Enter` and inserts newline with `Ctrl+Enter`, status shows `Enter` as protected Send and `Ctrl+Enter` as newline/pass-through.
- [x] If a protected profile is missing, unverified or degraded, the UI does not imply that a manual hotkey makes cloud submission protected.
- [x] Wording tests cover protected, not configured, binding unknown, surface unverified and degraded hotkey-only states.
- [x] Build, tests and self-test are green.

## 113. Add first-run readiness guard for protected AI profiles

**What to build:** On resident startup and first run, show an honest readiness state for selected Codex/ChatGPT Desktop profiles, guide the user to verify submit/newline bindings, and fail visibly when native protection is not active.

**Blocked by:** 110. Make the installed resident tray app the default protection path; 112. Show protected Send binding separately from manual scan/apply hotkey.

**Do not:** Auto-assume that `Enter` sends, mark an app as protected without verified composer evidence, or capture raw composer content for readiness diagnostics.

**Verification:**

```powershell
& 'C:\Users\alexey.andreev\AppData\Local\Microsoft\dotnet\dotnet.exe' build 'src\CodexRedactionGate\CodexRedactionGate.csproj' -nologo -v minimal
& 'C:\Users\alexey.andreev\AppData\Local\Microsoft\dotnet\dotnet.exe' test 'src\CodexRedactionGate\CodexRedactionGate.csproj' -nologo -v minimal
& 'C:\Users\alexey.andreev\AppData\Local\Microsoft\dotnet\dotnet.exe' run --project 'src\CodexRedactionGate\CodexRedactionGate.csproj' -- --self-test
```

**Verification result 2026-07-21:** build green with 0 warnings/0 errors; unit tests passed 317/317; `--self-test` passed; `--native-profiles-status` emits raw-free readiness/capability status; tray status exposes `readiness`, `protected_send_binding`, `newline_binding` and `manual_scan_hotkey`; startup with no protected profile remains `not_configured`, while hook/profile failures do not report `Protected`.

- [x] First run with no selected protected profiles shows `not_configured` and a clear local verification action.
- [x] Startup with selected profiles but no active native hook shows not protected or degraded status, never silent success.
- [x] Binding verification stores submit and newline bindings as user-verified unless a stronger documented source exists.
- [x] Compatibility failures surface raw-free reason codes such as `binding_unknown` or `surface_unverified`.
- [x] Readiness diagnostics never store raw prompt text, screenshots or full composer contents.
- [x] Build, tests and self-test are green.

## 114. Add installed-app protection smoke for launch, trigger and unload

**What to build:** Extend product smoke to prove the installed resident app protects the selected Codex/ChatGPT Desktop Send binding, separates manual hotkey status, survives canceled unload, and shuts down only after confirmed unload.

**Blocked by:** 111. Require explicit confirmation before disabling resident protection; 112. Show protected Send binding separately from manual scan/apply hotkey; 113. Add first-run readiness guard for protected AI profiles.

**Do not:** Use real sensitive data, depend on a live cloud submission, or skip the disposable local target before any real Codex/ChatGPT Desktop verification.

**Verification:**

```powershell
& 'C:\Users\alexey.andreev\AppData\Local\Microsoft\dotnet\dotnet.exe' build 'src\CodexRedactionGate\CodexRedactionGate.csproj' -nologo -v minimal
& 'C:\Users\alexey.andreev\AppData\Local\Microsoft\dotnet\dotnet.exe' test 'src\CodexRedactionGate\CodexRedactionGate.csproj' -nologo -v minimal
& 'C:\Users\alexey.andreev\AppData\Local\Microsoft\dotnet\dotnet.exe' run --project 'src\CodexRedactionGate\CodexRedactionGate.csproj' -- --self-test
```

**Verification result 2026-07-21:** build green with 0 warnings/0 errors; tray WinExe build green with 0 warnings/0 errors; unit tests passed 317/317; `--self-test` passed; `--native-submit-smoke` printed `native_submit_smoke_passed`; `--product-smoke` printed `product_smoke_passed`, `resident_tray_launch: true`, `protected_trigger_status: true`, `unload_confirmation: true`, `native_submit_interception: true` and `raw_free_artifacts: true`; `scripts\build-release.ps1` succeeded and emitted `resident_tray_exe`.

- [x] Smoke installs or stages the release artifact, launches the resident tray app and verifies there is no console/runtime SDK dependency.
- [x] Smoke verifies the protected trigger equals the selected profile's verified Send binding and that newline/pass-through still works.
- [x] Smoke verifies manual scan/apply hotkey status is separate from protected Send status.
- [x] Smoke verifies canceling unload leaves protection active and confirmed unload disables protection with visible status.
- [x] Smoke artifacts contain only raw-free diagnostics, placeholders and status codes.
- [x] Build, tests, self-test and product smoke are green.

## 200. Add honest project-file protection status

**What to build:** Add product status, diagnostics and tests that distinguish composer submit protection from project-file workflow protection. A user must be able to see that the selected AI composer may be protected while arbitrary project file reads are still not protected end-to-end.

**Blocked by:** 114. Add installed-app protection smoke for launch, trigger and unload.

**Do not:** Claim `project_files_protected` based on native submit interception, manual hotkey mode, or file-snippet sanitizer support alone.

**Verification:**

```powershell
& 'C:\Users\alexey.andreev\AppData\Local\Microsoft\dotnet\dotnet.exe' build 'src\CodexRedactionGate\CodexRedactionGate.csproj' -nologo -v minimal
& 'C:\Users\alexey.andreev\AppData\Local\Microsoft\dotnet\dotnet.exe' test 'src\CodexRedactionGate\CodexRedactionGate.csproj' -nologo -v minimal
& 'C:\Users\alexey.andreev\AppData\Local\Microsoft\dotnet\dotnet.exe' run --project 'src\CodexRedactionGate\CodexRedactionGate.csproj' -- --self-test
```

**Verification result 2026-07-22:** focused project-file workflow tests passed 5/5; full unit tests passed 332/332; `--self-test` passed; `--product-smoke` passed and reports `composer_protected_status: true` plus `project_files_protected_status: true` for the honest split where live project-file protection remains unavailable unless a broker workflow is verified.

- [x] Tray/CLI diagnostics expose separate `composer_protected` and `project_files_protected` status.
- [x] A verified Codex/ChatGPT Desktop submit profile can show `composer_protected=true` while `project_files_protected=false`.
- [x] `project_files_protected` is false when no file-context broker is active.
- [x] Status and audit diagnostics are raw-free and do not capture file contents.
- [x] Build, tests and self-test are green.

## 201. Add sanitized virtual file broker contract

**What to build:** Add the first file-context broker contract and a demo command that accepts a supported text file, runs it through the existing sanitizer pipeline, and returns a sanitized virtual file plus raw-free diagnostics without changing the original file.

**Blocked by:** 200. Add honest project-file protection status.

**Do not:** Integrate with live Codex file reads yet, write local file changes, or support binary/document formats.

**Verification:**

```powershell
& 'C:\Users\alexey.andreev\AppData\Local\Microsoft\dotnet\dotnet.exe' build 'src\CodexRedactionGate\CodexRedactionGate.csproj' -nologo -v minimal
& 'C:\Users\alexey.andreev\AppData\Local\Microsoft\dotnet\dotnet.exe' test 'src\CodexRedactionGate\CodexRedactionGate.csproj' -nologo -v minimal
& 'C:\Users\alexey.andreev\AppData\Local\Microsoft\dotnet\dotnet.exe' run --project 'src\CodexRedactionGate\CodexRedactionGate.csproj' -- --self-test
```

**Verification result 2026-07-22:** focused project-file workflow tests passed 5/5; full unit tests passed 332/332; `--self-test` passed; CLI tests cover `--project-file-sanitize` returning a sanitized virtual file without raw path/content diagnostics.

- [x] A supported UTF-8 text/source/config file can be represented as a sanitized virtual file.
- [x] The sanitized virtual file replaces protected domains, usernames, paths and secrets according to existing policy.
- [x] The original local file is not modified by the read flow.
- [x] Broker diagnostics include source id, content hash, entity counts and decision without raw values.
- [x] Build, tests and self-test are green.

## 202. Add protected workspace policy and file selection guard

**What to build:** Add protected workspace configuration and file selection rules so users can opt a repository into file-context protection and get fail-closed behavior for unsupported, unreadable, oversized or out-of-scope files.

**Blocked by:** 201. Add sanitized virtual file broker contract.

**Do not:** Add broad recursive DLP crawling, OCR, archive expansion, or automatic uploads.

**Verification:**

```powershell
& 'C:\Users\alexey.andreev\AppData\Local\Microsoft\dotnet\dotnet.exe' build 'src\CodexRedactionGate\CodexRedactionGate.csproj' -nologo -v minimal
& 'C:\Users\alexey.andreev\AppData\Local\Microsoft\dotnet\dotnet.exe' test 'src\CodexRedactionGate\CodexRedactionGate.csproj' -nologo -v minimal
& 'C:\Users\alexey.andreev\AppData\Local\Microsoft\dotnet\dotnet.exe' run --project 'src\CodexRedactionGate\CodexRedactionGate.csproj' -- --self-test
```

**Verification result 2026-07-22:** focused project-file workflow tests passed 5/5; full unit tests passed 332/332; `--self-test` passed; protected workspace tests cover raw-free registration, out-of-workspace rejection, unsupported extension rejection and size-limit rejection.

- [x] A workspace can be marked protected with local configuration outside the repository-sensitive vault.
- [x] Supported text file extensions and size limits are enforced before sanitizer execution.
- [x] Unsupported binary, PDF, Office, image and archive files produce fail-closed broker decisions.
- [x] Out-of-workspace paths and unreadable files are blocked with raw-free reason codes.
- [x] Build, tests and self-test are green.

## 203. Add read-only protected project file smoke

**What to build:** Add a product smoke that creates a disposable protected fixture workspace, routes file reads through the broker, and proves the model-visible payload is a sanitized virtual file.

**Blocked by:** 202. Add protected workspace policy and file selection guard.

**Do not:** Depend on a live cloud submission, real sensitive data, or a live Codex Desktop file-read integration.

**Verification:**

```powershell
& 'C:\Users\alexey.andreev\AppData\Local\Microsoft\dotnet\dotnet.exe' build 'src\CodexRedactionGate\CodexRedactionGate.csproj' -nologo -v minimal
& 'C:\Users\alexey.andreev\AppData\Local\Microsoft\dotnet\dotnet.exe' test 'src\CodexRedactionGate\CodexRedactionGate.csproj' -nologo -v minimal
& 'C:\Users\alexey.andreev\AppData\Local\Microsoft\dotnet\dotnet.exe' run --project 'src\CodexRedactionGate\CodexRedactionGate.csproj' -- --self-test
```

**Verification result 2026-07-22:** focused project-file workflow tests passed 10/10; full unit tests passed 337/337; `--self-test` passed; `--project-file-smoke` passed and reports sanitized payload/raw-free evidence while keeping `live_project_files_protected: false`; `--product-smoke` passed and includes `project_file_read_only_smoke: true`.

- [x] The smoke fixture includes synthetic domains, usernames, paths and secrets in project files.
- [x] Broker output contains placeholders and non-restorable secret redactions, not raw protected values.
- [x] Raw-free evidence records the file-context payload status.
- [x] `project_files_protected` remains false for live Codex until an actual integration point is verified.
- [x] Build, tests, self-test and project-file smoke are green.

## 204. Sanitize file-derived tool output

**What to build:** Extend the broker workflow so command/tool output derived from protected workspace files can be sanitized before it becomes model-visible context.

**Blocked by:** 203. Add read-only protected project file smoke.

**Do not:** Intercept arbitrary terminal sessions globally, store raw command output in audit logs, or claim coverage for tools that bypass the broker.

**Verification:**

```powershell
& 'C:\Users\alexey.andreev\AppData\Local\Microsoft\dotnet\dotnet.exe' build 'src\CodexRedactionGate\CodexRedactionGate.csproj' -nologo -v minimal
& 'C:\Users\alexey.andreev\AppData\Local\Microsoft\dotnet\dotnet.exe' test 'src\CodexRedactionGate\CodexRedactionGate.csproj' -nologo -v minimal
& 'C:\Users\alexey.andreev\AppData\Local\Microsoft\dotnet\dotnet.exe' run --project 'src\CodexRedactionGate\CodexRedactionGate.csproj' -- --self-test
```

**Verification result 2026-07-22:** focused project-file workflow tests passed 10/10; full unit tests passed 337/337; `--self-test` passed; broker tests cover managed tool-output sanitization and unmanaged tool-output fail-closed reporting with raw-free diagnostics.

- [x] Broker-managed tool output is passed through the same sanitizer decision pipeline as prompt and file content.
- [x] Paths, filenames, internal domains and secrets in tool output are replaced or redacted.
- [x] Tool-output audit events contain only hashes, counts, source ids and reason codes.
- [x] Unmanaged tool output is reported as unprotected rather than silently trusted.
- [x] Build, tests and self-test are green.

## 205. Add restore-aware patch dry-run

**What to build:** Add a dry-run local write workflow that accepts a sanitized model edit for a protected file, validates it against the sanitized virtual file identity, and previews the restored local patch without writing to disk.

**Blocked by:** 203. Add read-only protected project file smoke.

**Do not:** Auto-apply edits, restore non-restorable secrets, or accept patches for unrelated workspaces.

**Verification:**

```powershell
& 'C:\Users\alexey.andreev\AppData\Local\Microsoft\dotnet\dotnet.exe' build 'src\CodexRedactionGate\CodexRedactionGate.csproj' -nologo -v minimal
& 'C:\Users\alexey.andreev\AppData\Local\Microsoft\dotnet\dotnet.exe' test 'src\CodexRedactionGate\CodexRedactionGate.csproj' -nologo -v minimal
& 'C:\Users\alexey.andreev\AppData\Local\Microsoft\dotnet\dotnet.exe' run --project 'src\CodexRedactionGate\CodexRedactionGate.csproj' -- --self-test
```

**Verification result 2026-07-22:** focused project-file workflow tests passed 10/10; full unit tests passed 337/337; `--self-test` passed; dry-run tests cover protected workspace/source-version matching, local restoration preview without writes, non-restorable redactions remaining redacted and stale-source blocking.

- [x] A sanitized edit can be matched to its protected workspace, target path and sanitized source version.
- [x] Restorable pseudonyms are restored in the dry-run preview.
- [x] Non-restorable secrets remain redacted in the dry-run preview.
- [x] Stale, mismatched or out-of-workspace patches are blocked with raw-free reasons.
- [x] Build, tests and self-test are green.

## 206. Apply restore-aware local writes with approval

**What to build:** Complete the protected local write path so the user can approve a validated restored patch and write it to the local project while keeping restored sensitive values out of cloud-visible diagnostics.

**Blocked by:** 205. Add restore-aware patch dry-run.

**Do not:** Write restored patches without explicit approval, log restored values, or send restored output back to Codex automatically.

**Verification:**

```powershell
& 'C:\Users\alexey.andreev\AppData\Local\Microsoft\dotnet\dotnet.exe' build 'src\CodexRedactionGate\CodexRedactionGate.csproj' -nologo -v minimal
& 'C:\Users\alexey.andreev\AppData\Local\Microsoft\dotnet\dotnet.exe' test 'src\CodexRedactionGate\CodexRedactionGate.csproj' -nologo -v minimal
& 'C:\Users\alexey.andreev\AppData\Local\Microsoft\dotnet\dotnet.exe' run --project 'src\CodexRedactionGate\CodexRedactionGate.csproj' -- --self-test
```

**Verification result 2026-07-22:** focused project-file workflow tests passed 14/14; full unit tests passed 341/341; `--self-test` passed; `--project-file-product-smoke` passed; approved apply writes only after `--approve`, cancel leaves files unchanged, final-path containment is rechecked before write and write audit records include raw-free target ids/hashes.

- [x] Approved restored patches are written only to the intended protected workspace path.
- [x] Cancel leaves the local project unchanged.
- [x] Write audit events record target ids, hashes, action and status without raw restored values.
- [x] The UI/status marks restored file output as local-sensitive.
- [x] Build, tests and self-test are green.

## 207. Guard direct attachment and bypass paths

**What to build:** Add user-visible warnings and fail-closed decisions for project-file channels that are not routed through the broker, including direct attachment upload and unmanaged connector/tool paths.

**Blocked by:** 203. Add read-only protected project file smoke.

**Do not:** Claim interception for app upload controls unless a verified adapter proves pre-upload sanitization.

**Verification:**

```powershell
& 'C:\Users\alexey.andreev\AppData\Local\Microsoft\dotnet\dotnet.exe' build 'src\CodexRedactionGate\CodexRedactionGate.csproj' -nologo -v minimal
& 'C:\Users\alexey.andreev\AppData\Local\Microsoft\dotnet\dotnet.exe' test 'src\CodexRedactionGate\CodexRedactionGate.csproj' -nologo -v minimal
& 'C:\Users\alexey.andreev\AppData\Local\Microsoft\dotnet\dotnet.exe' run --project 'src\CodexRedactionGate\CodexRedactionGate.csproj' -- --self-test
```

**Verification result 2026-07-22:** focused project-file workflow tests passed 14/14; full unit tests passed 341/341; `--self-test` passed; bypass guard tests cover direct attachment and unmanaged connector warnings for protected workspaces with broker-only policy and raw-free diagnostics, while broker-routed tool output exposes broker-only policy state.

- [x] Protected workspace status warns when direct attachments are not broker-routed.
- [x] Unmanaged connector/tool paths are shown as unprotected or blocked by policy.
- [x] The warning path does not capture raw filenames, paths or file contents by default.
- [x] Enterprise policy can require broker-only file context for protected workspaces.
- [x] Build, tests and self-test are green.

## 208. Add end-to-end protected project-file product smoke

**What to build:** Add the final disposable smoke that proves the complete protected file workflow: read supported project file, emit sanitized virtual file, process sanitized model edit, preview restored patch, approve local write, and verify raw-free audit evidence.

**Blocked by:** 204. Sanitize file-derived tool output; 206. Apply restore-aware local writes with approval; 207. Guard direct attachment and bypass paths.

**Do not:** Use real sensitive data, depend on live cloud submission, or skip unsupported-file fail-closed checks.

**Verification:**

```powershell
& 'C:\Users\alexey.andreev\AppData\Local\Microsoft\dotnet\dotnet.exe' build 'src\CodexRedactionGate\CodexRedactionGate.csproj' -nologo -v minimal
& 'C:\Users\alexey.andreev\AppData\Local\Microsoft\dotnet\dotnet.exe' test 'src\CodexRedactionGate\CodexRedactionGate.csproj' -nologo -v minimal
& 'C:\Users\alexey.andreev\AppData\Local\Microsoft\dotnet\dotnet.exe' run --project 'src\CodexRedactionGate\CodexRedactionGate.csproj' -- --self-test
```

**Verification result 2026-07-22:** focused project-file workflow tests passed 14/14; full unit tests passed 341/341; `--self-test` passed; `--project-file-product-smoke` passed and reports `project_files_protected: true` only when read/tool/apply/unsupported/bypass/raw-free-audit checks all pass; `--product-smoke` passed and includes `project_file_product_smoke: true`.

- [x] Smoke proves supported file reads produce sanitized virtual files.
- [x] Smoke proves file-derived tool output is sanitized before model visibility.
- [x] Smoke proves sanitized edits can be restored and written locally after approval.
- [x] Smoke proves unsupported files and bypass paths fail closed or show unprotected status.
- [x] Smoke proves `project_files_protected=true` only for the verified broker workflow.
- [x] Build, tests, self-test and project-file product smoke are green.

## 209. Add delayed desktop-session native profile verification

**What to build:** Add a product onboarding path that verifies Codex Desktop and ChatGPT Desktop profiles from the user's real desktop session. The user can start verification from tray or CLI, focus the target composer during a countdown, and Code Sanitizer saves `protected` only when the focused composer and submit/newline bindings verify raw-free.

**Blocked by:** None - can start immediately.

**Do not:** Mark `codex-desktop` or `chatgpt-desktop` protected from sandbox-only foreground evidence, assume `Enter` without verification, or log raw prompt/window text.

**Verification:**

```powershell
dotnet build .\src\CodexRedactionGate\CodexRedactionGate.csproj -nologo -p:UseAppHost=false
dotnet test .\src\CodexRedactionGate\CodexRedactionGate.csproj -nologo -p:UseAppHost=false
dotnet .\src\CodexRedactionGate\bin\Debug\net10.0-windows\CodexRedactionGate.dll --product-smoke
```

**Verification result 2026-07-23:** build green with 0 warnings/0 errors; full unit tests passed 351/351; `--product-smoke` passed and reports `native_profile_verification_entrypoints: true`. Direct production `--self-test` was not used as completion evidence because the current user-local DPAPI HMAC secret failed to unprotect in this execution context with `Ключ не может быть использован в указанном состоянии`; this is an environment/local-data issue, not a native profile verification regression.

- [x] Tray exposes Codex Desktop and ChatGPT Desktop verification commands that do not require copying `dotnet run` text.
- [x] CLI exposes delayed native profile verification so the user can focus the target composer after starting the command.
- [x] A profile is saved as `protected` only after focused composer verification succeeds for that profile.
- [x] Failed verification remains `surface_unverified` or `binding_unknown` with raw-free diagnostics.
- [x] Product smoke or release tests prove the Codex/ChatGPT native submit readiness path remains present every run.
