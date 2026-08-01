# Spec: Codex Redaction Gate

## Problem Statement

Codex users solve real engineering tasks and inevitably paste context that may include trade secrets, personal data, and internal information: URLs, domains, product names, customer names, project names, private IP addresses, logs, configuration files, tokens, passwords, and infrastructure identifiers.

Manual cleanup with a separate script before every prompt is not acceptable. It adds too much friction, is quickly bypassed, and does not protect against accidental pasting of sensitive fragments. The project needs a global mechanism that runs before cloud submission, replaces sensitive values, keeps the mapping table local, and allows the user to restore the response locally.

## Solution

Build a local Redaction Gate for Codex: a system layer before prompt submission that intercepts input, detects sensitive data, replaces detected values with stable pseudonyms, shows a diff/confirmation step, submits only sanitized text, and later offers local restoration of real values.

The MVP is a Windows-first local .NET application/adapter installed as a resident desktop app. The main user workflow is not manual copy/paste and not a separate sanitizer hotkey. It is native submit interception for selected AI apps: the user writes in Codex/ChatGPT Desktop, presses that app's configured Send shortcut, Code Sanitizer intercepts the submit before cloud submission, and the adapter sends only a safe original prompt or approved `sanitized_text`.

The mapping table must be user-global across projects. The same real URL, domain, IP, email, product name, or customer name should always receive the same pseudonym. High-sensitivity secrets such as passwords, tokens, and keys are non-restorable by default: they are replaced with `SECRET_REDACTED` or stored only after an explicit temporary user approval.

## User Stories

1. As a Codex user, I want my prompt to be scanned automatically before submission, so that I do not need to remember to run a redaction script manually.
2. As a Codex user, I want URLs to be replaced with stable pseudonyms, so that internal systems are not disclosed to OpenAI.
3. As a Codex user, I want internal domains to be replaced with stable pseudonyms, so that company structure is not exposed.
4. As a Codex user, I want private IP addresses and CIDR ranges to be replaced, so that internal network topology is not exposed.
5. As a Codex user, I want emails and personal names to be replaced, so that personal data is not sent to the cloud.
6. As a Codex user, I want product, project and customer names to be replaced using a local dictionary, so that commercial context remains local.
7. As a Codex user, I want passwords, tokens and API keys to be removed or strongly redacted, so that credentials are not leaked even as hashes.
8. As a Codex user, I want to see the sanitized prompt before it is sent, so that I can confirm the transformation.
9. As a Codex user, I want to see a compact replacement summary, so that I understand what was changed without exposing everything unnecessarily.
10. As a Codex user, I want one-click approval of the sanitized prompt, so that the security flow does not become tedious.
11. As a Codex user, I want one-click cancellation, so that I can stop an unsafe submission.
12. As a Codex user, I want the same original value to map to the same pseudonym across all projects, so that multi-project investigations remain coherent.
13. As a Codex user, I want the mapping table to stay local, so that real identifiers are not sent to OpenAI.
14. As a Codex user, I want the mapping store to be encrypted, so that local compromise is harder.
15. As a Codex user, I want the answer from Codex to be restorable locally, so that I can turn `URL_a1b2c3` back into the real URL when needed.
16. As a Codex user, I want restoration to be optional, so that I can keep management-facing reports sanitized.
17. As a Codex user, I want restored output to be clearly marked as local, so that I do not accidentally send it back to the cloud.
18. As a Codex user, I want the gate to cover prompts, file snippets, clipboard content and tool outputs where possible, so that sensitive data does not bypass the main prompt path.
19. As a Codex user, I want the system to fail closed when uncertain, so that unknown sensitive-looking text is not silently sent.
20. As a Codex user, I want project-specific allowlists, so that public URLs and harmless package names do not cause noise.
21. As a Codex user, I want global blocklists, so that company domains and known customer/product names are always protected.
22. As a Codex user, I want an audit log of redaction events without raw secrets, so that I can review whether the gate is working.
23. As a Codex user, I want low-friction operation, so that security does not break my normal Codex workflow.
24. As a security reviewer, I want clear threat boundaries, so that I know what this system can and cannot protect.
25. As a future maintainer, I want detectors to be modular, so that new sensitive patterns can be added without rewriting the gate.
26. As a future maintainer, I want deterministic tests with sample sensitive inputs, so that detector changes do not regress protection.
27. As an enterprise admin, I want a managed enforcement mode, so that users cannot accidentally disable the protection for sensitive workspaces.
28. As a Codex user, I want to manually mark a customer, project, domain, URL prefix or regex pattern as sensitive, so that newly discovered false negatives can be fixed without code changes.
29. As a Codex user, I want text attachments and large pasted file contents to pass through the same sanitizer pipeline, so that large logs and config dumps do not bypass redaction.
30. As a Codex user, I want unsupported binary attachments to be blocked or explicitly warned, so that unreadable files are not silently treated as safe.
31. As a Codex user, I want replaced spans highlighted before confirmation, so that I can understand what will be sent without inspecting raw secrets.
32. As a Codex/ChatGPT Desktop user, I want Code Sanitizer to intercept my normal Send shortcut, so that I cannot forget a separate hotkey and accidentally send raw sensitive data.
33. As a Codex/ChatGPT Desktop user, I want to choose which AI app surfaces are protected, so that Code Sanitizer does not intercept unrelated applications.
34. As a Codex/ChatGPT Desktop user, I want Code Sanitizer to read or verify my selected AI app's configured Send shortcut, so that it protects the keys I actually use.
35. As a Codex/ChatGPT Desktop user, I want Code Sanitizer to verify both my Send shortcut and my newline shortcut, so that normal multiline editing does not accidentally submit or get blocked.
36. As a Codex/ChatGPT Desktop user, I want a visible emergency disable action, so that I can recover local input if interception misbehaves without sending a raw prompt.
37. As a Codex/ChatGPT Desktop user, I want a clear warning when my installed app version no longer matches a verified protected profile, so that I know protection is not active.
38. As an enterprise admin, I want protected AI profiles to be lockable by policy, so that managed users cannot silently downgrade to hotkey-only protection.
39. As a Windows user, I want Code Sanitizer to install and launch as a normal resident tray application, so that protection is running without manual `dotnet run` commands.
40. As a Windows user, I want exiting or unloading Code Sanitizer to require explicit confirmation, so that I do not accidentally turn protection off.
41. As a Codex/ChatGPT Desktop user, I want Code Sanitizer's primary trigger to be the same Send shortcut I use in the selected AI app, so that pressing the normal Codex/ChatGPT send key is what activates protection.
42. As a Codex coding user, I want project file reads to pass through a local sanitizer before they become model context, so that sensitive data inside repository files does not bypass composer protection.
43. As a Codex coding user, I want sanitized virtual files to preserve enough structure for useful coding assistance, so that the model can still reason about code after identifiers are pseudonymized.
44. As a Codex coding user, I want model-generated edits restored locally before they are written to disk when policy allows, so that my real project remains usable while the cloud saw only placeholders.
45. As a security reviewer, I want product status to distinguish composer protection from project-file protection, so that users do not over-trust the desktop submit interceptor.
46. As a Codex/ChatGPT Desktop user, I want profile verification to run from my real desktop session with time to focus the target composer, so that protected status does not depend on an automation or sandbox session seeing the same foreground window.
47. As a maintainer, I want every build to exercise the Codex/ChatGPT native submit readiness path, so that regressions cannot silently turn live input protection back into hotkey-only mode.
48. As a Codex/ChatGPT Desktop user, I want the replacement confirmation overlay to become active whenever it appears, so that I do not miss a hidden security decision.
49. As a Codex/ChatGPT Desktop user, I want every protected Send attempt to trigger a fresh sanitizer decision, so that one successful replacement does not make later sensitive prompts bypass the overlay.
50. As a Codex/ChatGPT Desktop user, I want Cancel in the replacement window to send nothing and preserve future interception, so that canceling once cannot let the next raw prompt bypass Code Sanitizer.
51. As a Codex/ChatGPT Desktop user, I want to edit the sanitized prompt inside the replacement window before sending, so that I can fix the prompt without returning sensitive text to the cloud-bound composer.
52. As a Codex/ChatGPT Desktop user, I want raw submission with detected sensitive terms to require a separate emergency bypass action, so that normal Send and Cancel can never accidentally approve raw data.
53. As a first-time Windows user, I want setup/profile verification to appear after installation and block unsafe sends until protected, so that installing Code Sanitizer does not leave an unconfigured gap.
54. As a Codex/ChatGPT Desktop user, I want to select and verify my effective Send/newline shortcut pair, so that Code Sanitizer protects the shortcut that really submits my prompt rather than assuming `Enter`.
55. As a Codex/ChatGPT Desktop user, I want normal editing and non-Send controls to retain their keyboard behavior, so that protection does not block `Enter`, multiline input, or skill selection when those are not my configured Send action.
56. As a Windows user, I want only one resident Code Sanitizer instance to own the input hook, so that duplicate tray processes cannot produce conflicting interception behavior.
57. As a Codex/ChatGPT Desktop user, I want each Send decision to use one complete resident protection state, so that profile reloads, setup changes, and hook replacement cannot mix old and new protection data.
58. As a Codex/ChatGPT Desktop user, I want an uncertain selected AI Send attempt to stop locally, so that a transient UI Automation, hook, or focus failure cannot release my prompt to the cloud.
59. As a user of other Windows applications, I want uncertainty outside a selected AI client to leave my input alone, so that Code Sanitizer does not interfere with unrelated work.
60. As a maintainer, I want a release gate based on real resident lifecycle evidence, so that a collection of isolated unit tests cannot falsely prove protected submission.

## Implementation Decisions

- The project is a standalone local application/library, not a patch inside every target repository.
- The MVP implementation runtime is .NET for the Windows-first orchestrator, file-based vault, local UI and gateway/adapter layer.
- The core module is `redaction-engine`: it receives text and returns sanitized text, detected entities, replacement records and warnings.
- The mapping module is `mapping-vault`: it stores global mappings, keyed by normalized entity type and original value, in a file-based vault for MVP.
- Pseudonyms use deterministic keyed hashing, preferably HMAC-SHA256 with a local secret, not raw SHA256. This reduces dictionary attack risk for short values such as domains, names and product codes.
- The HMAC/encryption secret is protected by DPAPI or equivalent OS-protected storage. Plaintext vault mode is only an explicit dev/diagnostic mode.
- Pseudonym format preserves type but not value: `URL_8F3A21B9`, `DOMAIN_19C0E44A`, `IP_6D9A72C1`, `EMAIL_BA1080F2`, `PRODUCT_0D83A7AA`. Usernames use readable deterministic aliases such as `USERNAME_bright_turing_8F3A` so sanitized paths still look like user paths.
- Secrets are handled differently from identifiers. Passwords, API keys, bearer tokens, private keys and session cookies are redacted as non-restorable by default.
- Gitleaks is the first external secret scanner. It is built from source in the project release process from a pinned tag/commit, with revision/build command/checksum recorded, and the resulting `gitleaks.exe` shipped to users.
- The first implementation should support two integration modes:
  - Guard mode: a Codex hook or equivalent pre-submit checker blocks unsafe prompts and offers a sanitized replacement.
  - Gateway/adapter mode: a local composer/proxy/desktop layer owns the submit action and can send sanitized text after user confirmation.
- Guard mode is acceptable as a baseline but does not fully satisfy the desired UX unless it can programmatically replace the submitted prompt.
- Native submit interception for selected Windows Codex/ChatGPT Desktop surfaces is the primary gateway/adapter path. A minimal confirm-and-send adapter is part of MVP because it implements the desired `Confirm sanitized prompt` flow. A polished full composer is later.
- Native submit interception protects the selected AI app composer submit path. It does not guarantee protection for arbitrary project files read by a coding agent, attachments uploaded outside the sanitizer, or tool outputs derived from local files.
- Full coding-workflow protection requires a restore-aware file-context broker that owns supported project file reads, creates sanitized virtual files for model context, blocks unsupported file content, validates sanitized patches, and restores restorable pseudonyms only in the local write path.
- The product must distinguish `composer_protected` from `project_files_protected` in status, diagnostics and documentation.
- The product must ship as a Windows installer that installs the resident tray app, can launch it at the end of setup, and can configure user-scope autostart. Normal protected operation must not require a console or `dotnet run`.
- Stopping protection, exiting the tray app, or unloading the resident process must require an explicit confirmation dialog that names the consequence: selected AI apps will no longer be protected. Enterprise policy may block unload or require administrator approval.
- Hotkey-triggered scan/apply is a secondary diagnostic/manual feature. It must not be presented as the main protection path.
- In native protected mode, Code Sanitizer's effective trigger is the selected AI app's verified `submit_binding`. The tray/status text must show this as the protected Send binding, not as a separate CS hotkey. For v1, supported pairs are `Enter` Send / `Ctrl+Enter` newline and `Ctrl+Enter` Send / `Enter` newline. CS must intercept only the configured Send binding in the verified composer, and must pass the configured newline binding through.
- Any separate manual CS hotkey must be clearly labeled as `manual scan/apply`, must not be used as evidence that native submit protection is active, and should avoid conflicting with the selected AI app's newline binding.
- The adapter must maintain enabled AI surface profiles and must protect only explicitly selected AI apps.
- The adapter must discover the selected AI app's submit binding from local app configuration when available. If not available, onboarding must ask the user to choose or record a supported Send/newline pair and verify it. The current saved pair must be visible in resident UI before verification.
- The adapter must not silently assume that `Enter` is the active Send shortcut, hard-code a shortcut pair in a tray command, or replace a user-verified pair with a fixed default.
- The current Windows release path treats submit binding configuration as `user_verified` by default. A binding can be marked `documented_config` only when the target app vendor documents a stable local setting, or `empirical_config` only after repeated compatibility evidence across app updates.
- Each AI surface profile must store both `submit_binding` and `newline_binding`. The adapter may claim `protected` only when it can distinguish them for the active focused composer.
- Selecting a different binding pair invalidates the old protected binding until delayed focused verification succeeds. A cancelled, failed, or timed-out change must leave the profile unprotected rather than retaining a stale protected binding.
- The native hook must pass through non-Send keys and keyboard use of non-Send controls, including ordinary `Enter` when `Ctrl+Enter` is the configured Send binding. It may suppress the configured binding fail-closed only for the verified composer or an identifiable selected-app Send control.
- Resident protection state is an immutable, versioned snapshot published atomically. A snapshot contains the selected profile set and verified bindings, hook readiness, controller/runner needed to guard a submit, and the target identity contract. A native input event reads exactly one snapshot; it must never assemble a decision from separately mutable profile, controller, runner, or hook fields.
- The resident protection state is the sole authority for whether protection is active, degraded, setup-required, or recovery-required. Tray UI, notification text, and local protection-status rows are read-only projections of that published resident state. They may request an explicit remediation command, but must not combine local flags or persisted profile data to decide that a cloud-bound path is protected.
- Reload is transactional: build and verify a candidate snapshot first, then publish it in one operation. A failed candidate preserves the previous complete snapshot. No event may observe a controller from one generation with bindings, profiles, or a hook from another generation.
- The selected-client decision contract is explicit. For a selected AI client, a verified non-Send control passes through, a verified Send control is suppressed and guarded, and uncertainty in composer identity, Send-control identity, hook health, UI Automation, or target validity blocks the submission with raw-free status. Input outside a selected AI client remains outside the protected boundary and continues normally.
- Deferred sanitize/confirm/replay work must receive the snapshot generation and the composer/window target identity captured for the original gesture. It must not rediscover the foreground target later. If the captured target is invalid, changed, or cannot be verified before replay, it aborts raw-free and does not submit.
- The only normal cloud-submission paths from a selected protected surface remain: no sensitive terms after local processing, verified sanitized text from the replacement overlay, or the separately confirmed one-shot emergency bypass. A stale runtime state, failed reload, cancelled overlay, or uncertain target is never a fourth path.
- Work that changes native protection must be delivered as a complete vertical state transition with its regression evidence. Installer, notification, and cosmetic work cannot be used as proof that the protected-send core is complete.
- The resident tray app must enforce one hook-owning instance per user. A second launch must foreground or signal the existing instance and exit without registering another keyboard hook.
- The tray/menu status must distinguish `protected` from `not_configured`, `binding_unknown`, `surface_unverified` and `degraded_hotkey_only`.
- Native submit interception must include a visible, raw-free emergency disable path and hook-health watchdog. An emergency action disables protection temporarily; it must not silently send the raw prompt.
- Enterprise mode may lock required protected profiles, prevent user removal, and disallow silent `degraded_hotkey_only` fallback for selected AI apps.
- A verified AI profile must include package/app identity, version compatibility, executable/process signals, window identity, UI Automation composer shape, supported read/write patterns, binding source, binding values, and last verification result.
- Native profile onboarding must provide a delayed verification path that runs in the user's desktop session. The user starts verification, focuses the Codex or ChatGPT composer before the countdown ends, and the profile is marked `protected` only if the focused composer and submit/newline bindings verify raw-free.
- The installed tray app must expose profile verification for Codex Desktop and ChatGPT Desktop without requiring the user to copy `dotnet run` commands. Console commands may remain as diagnostics, but normal onboarding must be reachable from resident UI.
- Native submit interception must be repeatable for every matching Send gesture while protection is enabled. Confirmation, cancellation, block and failure paths must return the resident hook to a ready state for the next protected Send.
- The confirmation overlay for native submit decisions must request active foreground display when shown. If foreground activation is refused by Windows, Code Sanitizer must produce visible raw-free status rather than silently hiding the decision window.
- Cancel in the replacement overlay is scoped only to the current submit attempt. It sends nothing and must not create a pass-through, allow token, profile downgrade, or remembered bypass for the next Send.
- The replacement overlay must include an editable sanitized-text path. Edited sanitized text must be locally verified before submission, and any edit that reintroduces sensitive values must fail closed.
- Sending original raw text that still contains detected sensitive terms requires a distinct emergency bypass action, proposed as `Ctrl+Alt+Shift+Enter` while the replacement window is active plus a visible one-shot button. It must require a second confirmation, be audited raw-free, and be policy-blockable.
- While Code Sanitizer is running and a selected Codex/ChatGPT profile is protected, normal composer Send must never submit a prompt that still contains detected sensitive terms. Cloud submission from that surface is allowed only when the prompt has no detected sensitive terms, when verified sanitized text is submitted from the replacement overlay, or when the explicit emergency bypass action is confirmed for that one attempt.
- After installation, if no selected Codex/ChatGPT profile is protected, the resident app must show an active setup window and suppress selected AI app submit attempts with a raw-free setup-required status until delayed focus verification succeeds.
- The system must never send the mapping table or HMAC secret to the cloud.
- The system must show an explicit confirmation step when sensitive data was replaced.
- The system must use a 10-second total hard cap for sanitizer work, target under 2 seconds for ordinary prompts, and fail closed on timeout.
- The system may auto-send without confirmation only when no sensitive data was detected, or when the user has explicitly enabled a low-risk auto mode.
- The system must maintain a local audit log that records event time, entity types, counts, target application, policy decision, scanner statuses, warning codes, durations/timeouts, span offsets/length/type and replacement pseudonyms, but never raw original values.
- The system must support import/export of policy dictionaries without exporting the mapping vault by default.
- Sensitivity rules must be policy-as-data: built-in detectors cover generic technical patterns, while local policy files and dictionaries cover organization-specific names and identifiers.
- Manual dictionaries are CSV; structured policy/config is TOML.
- Policy files and dictionaries must be treated as sensitive local artifacts because they can contain customer names, product names, project names and internal domains.

## Testing Decisions

- Tests should validate external behavior: given an input, the gate returns expected sanitized output, entity classifications, mapping behavior and policy decisions.
- Detector tests should cover URLs, domains, emails, private IPs, CIDRs, tokens, passwords, connection strings, JWTs, private keys, customer names, project names and mixed log snippets.
- Attachment tests should cover text attachments, large pasted content and unsupported binary attachment metadata.
- Mapping tests should verify deterministic pseudonyms across sessions and projects.
- Vault tests should verify that raw originals are not present in audit logs or exported policy files.
- Gateway/adapter integration tests should simulate the full lifecycle: input -> sanitize -> confirm -> send sanitized -> receive sanitized answer -> restore locally.
- Project-file workflow tests should simulate file read -> sanitized virtual file -> sanitized model edit -> restore-aware local write, and prove that raw protected values do not appear in cloud-visible payload records.
- Submit interception tests should simulate selected and unselected AI surfaces, matching and non-matching submit bindings, input suppression, sanitizer allow/confirm/block and fail-closed behavior.
- Binding verifier tests should cover submit vs newline separation, unknown binding status, IME/dead-key pass-through, and user-verified binding persistence without cloud submission.
- Binding tests must cover both supported Send/newline pairs, profile persistence and resident reload, invalidation after a requested binding change, and unsupported-pair rejection without fallback to `Enter`.
- Native-hook tests must prove that a non-Send `Enter` or `Ctrl+Enter` action passes through in the selected AI app while the configured Send binding is guarded; identifiable Send-button keyboard and mouse activation must fail closed.
- Resident tests must prove a second process launch cannot create another hook-owning tray instance.
- Installer/tray tests should cover install, launch-after-install, user-scope autostart, resident startup status, explicit unload confirmation and local data retention.
- Trigger wording tests should prove the product reports the protected AI app Send binding separately from any secondary manual scan/apply hotkey.
- Emergency escape tests should cover temporary disable, tray status changes, hook watchdog downgrade, and raw-free audit events.
- Enterprise enforcement tests should cover locked profiles, forbidden hotkey-only degradation, and configured fail behavior for unverified surfaces.
- Compatibility tests should cover app/package version mismatch, UIA composer mismatch, and `surface_unverified` diagnostics without raw prompt capture.
- Native profile verification tests should cover delayed focused-composer verification, raw-free output, and command/tray discoverability for both Codex Desktop and ChatGPT Desktop.
- Product smoke must continue to exercise the Windows Codex/ChatGPT native submit readiness path every run, including selected-profile gating and raw-free status reporting.
- Native submit regression tests should trigger repeated protected Send gestures and prove the overlay/sanitizer path runs for each attempt, not only the first one.
- Resident-state tests must exercise the externally visible decision matrix for selected verified Send, selected verified non-Send, selected uncertain, and unrelated uncertain input. They must prove the selected uncertain case is suppressed while unrelated input continues.
- Runtime reload tests must prove that an event sees one complete snapshot generation, that failed replacement retains the previous working snapshot, and that no mixed profile/binding/controller/runner state is observable.
- Deferred-flow tests must prove replay targets the composer/window captured for the initiating gesture and aborts raw-free when that target changes or cannot be revalidated.
- Release smoke must exercise the real resident application-context lifecycle and native-hook seam, rather than infer protection from file presence, constants, or isolated helper tests.
- Native submit regression tests should cover Cancel followed by another Send with the same or edited composer text and prove the sanitizer/confirmation path runs again.
- Confirmation overlay tests should cover manual editing of sanitized text, verification before submit, rejection of edited text that reintroduces sensitive values, and explicit emergency bypass behavior.
- First-run setup tests should cover installer-launched resident startup with no protected profile, active delayed-focus setup, and fail-closed submit attempts before setup completes.
- Confirmation overlay tests should prove the replacement window requests active foreground display and remains raw-free.
- Timeout tests should verify fail-closed behavior at the 10-second hard cap and scanner-level errors.
- Security regression tests should include near-miss samples and false-positive samples.
- The highest-value seam is the redaction engine API: it can be tested without depending on Codex UI internals.
- A second seam is the gateway adapter contract: adapter receives a prompt event and must either allow, block or replace with explicit user confirmation.
- UI tests must obtain status through the same resident-state projection used by the tray. They must drive remediation through deterministic dispatch seams, not timer polling, foreground focus, or a live cloud submission.

## Out of Scope

- Guaranteeing that data already sent to OpenAI can be removed from past model-training pipelines.
- Protecting against users who intentionally bypass the gate.
- Protecting every possible third-party connector unless the connector traffic also flows through the gateway.
- End-to-end project file protection without a verified local file-context broker.
- Full DLP classification for arbitrary documents in the first version.
- Full binary/PDF/Office/image parsing, OCR and recursive archive scanning in the first version.
- Silent mutation of Codex prompts using unsupported private APIs.
- Claiming protection for a target AI app when its submit shortcut or composer surface cannot be verified.
- Storing real passwords or long-lived secrets for restoration by default.

## Further Notes

The design must be honest about Codex hook limits. If the official hook surface only allows blocking a user prompt, the project cannot claim transparent pre-submit replacement inside Codex itself. In that case, the correct product is a local gateway/composer that sits before Codex, or a managed Codex extension point that explicitly supports prompt rewriting.

The most important success criterion is not perfect detection. It is a workflow the user can actually keep enabled every day: low friction, deterministic replacements, clear confirmations and fail-closed behavior for obviously sensitive data.
