# ADR-004: Native Submit Interception Is the Primary Desktop UX

## Status

Accepted

## Context

The hotkey-first desktop demo reduced risk during early UI Automation work, but it is not enough for the product. A user can type sensitive data into Codex or ChatGPT Desktop, forget the sanitizer hotkey, press the normal Send shortcut, and leak the raw prompt to the cloud.

The product must protect the path the user already uses: write prompt in the selected AI app, press that app's configured submit shortcut, and have Code Sanitizer intercept the submission before it leaves the machine.

## Decision

For Windows Codex/ChatGPT Desktop, the primary product mode is native submit interception:

```text
User presses the selected AI app's Send shortcut
  -> Code Sanitizer recognizes selected protected surface
  -> Code Sanitizer suppresses the original submit input
  -> Code Sanitizer captures composer text locally
  -> sanitizer returns allow | confirm | block
  -> allow: Code Sanitizer replays the verified submit binding
  -> confirm: Code Sanitizer shows local confirmation, applies sanitized text, then submits only after approval
  -> block/failure: Code Sanitizer sends nothing
```

The existing hotkey remains as a secondary feature for manual scan/apply, diagnostics, and rescue workflows. It is not the main safety mechanism.

The user must explicitly choose which AI surfaces are protected. A protected surface profile contains:

- application identity, such as Codex Desktop or ChatGPT Desktop;
- executable/process/window identity signals;
- supported composer UI Automation shape;
- submit binding source and value;
- capability status: `protected`, `not_configured`, `binding_unknown`, `surface_unverified`, or `degraded_hotkey_only`.

The submit binding must be discovered from the target AI configuration when a stable, local, documented or empirically verified config source exists. If the config source is unavailable, onboarding must ask the user to choose or record the effective submit shortcut and its newline counterpart, then verify the pair against the selected app surface. The product must not silently assume `Enter`, hard-code the ChatGPT pair in a tray command, or retain a stale protected binding after the user changes it. For v1, `Enter` Send / `Ctrl+Enter` newline and `Ctrl+Enter` Send / `Enter` newline are supported profile pairs.

Submit interception is allowed only when all of these are true:

- the foreground surface matches an enabled AI profile;
- the focused element matches the verified composer shape for that profile;
- the pressed shortcut matches the profile's active submit binding;
- the current mode is `protected`, not `hotkey_only` or `diagnostic`;
- the sanitizer, vault, policy and confirmation UI are available.

If any condition fails, Code Sanitizer must not claim protection. If the pressed shortcut belongs to a selected protected surface but sanitizer processing cannot complete, it must suppress the original submit and fail closed.

Non-submit input must pass through. In particular, the profile newline binding and ordinary `Enter` when `Ctrl+Enter` is the configured Send shortcut must not be intercepted in the composer or unrelated selected-app controls. The only exception is an identifiable selected-app Send control: activation using the configured Send shortcut must fail closed until it is handled by the protected flow. The resident tray app must have a per-user single-instance boundary so two instances cannot compete for the same input hook.

Onboarding and re-verification must run against the user's real desktop session, not an agent or build sandbox's foreground window. The product must provide a delayed verification path from the installed tray app and CLI: the user starts verification, focuses the Codex/ChatGPT composer before the countdown ends, and the profile is saved as `protected` only when that focused composer verifies. The release test surface must keep exercising this readiness path so regressions do not silently downgrade native submit protection to manual hotkey mode.

Runtime protection must also be repeatable. Every matching Send gesture in a selected protected composer is a new guarded submit attempt; a previous confirmation must not disable or satisfy later prompts. The local confirmation overlay must request foreground activation whenever a replacement decision is required. If Windows refuses activation, the product must show raw-free visible status and must not submit raw text.

Cancel is not a bypass. Cancel sends nothing for the current attempt and returns the user to editing, but the next press of the selected AI app's Send shortcut must run the native submit handler again. Raw submission with detected sensitive values may exist only as a separate emergency bypass action, proposed as `Ctrl+Alt+Shift+Enter` while the replacement overlay is active plus a visible one-shot button. That bypass requires second confirmation, raw-free audit, and enterprise policy control.

The replacement overlay is also an edit surface for sanitized text. A user may manually adjust the sanitized prompt before approving, but the edited sanitized text must be verified locally before the adapter replays the AI app submit binding.

First-run setup is part of the protected product state. After installation, if no selected Codex/ChatGPT profile is protected, the resident app must show an active setup window with delayed focus verification and must suppress matching selected AI app submit attempts with a raw-free setup-required status until the profile is verified.

## Consequences

Positive:

- The normal user habit becomes protected by default.
- Forgetting the sanitizer hotkey no longer bypasses the cloud boundary.
- The product can show a meaningful `Protected` status per AI app instead of a generic tray state.
- Hotkey mode remains useful without being the primary control.

Negative:

- The Windows adapter must move from `RegisterHotKey`-style global hotkeys to a narrower low-level input interception model.
- Incorrect surface matching or shortcut matching could break normal typing, so profile verification and fail-safe escape behavior are required.
- Reading third-party app configuration is app-specific and may break across Codex/ChatGPT releases.
- Some target apps may not expose their send shortcut in a stable local config; those apps can only be `hotkey_only` until verified.

## Guardrails

- Never intercept submit shortcuts outside explicitly enabled AI surface profiles.
- Never claim `protected` when the active submit binding is unknown.
- Never replay the native submit shortcut until the composer text is verified as sanitized or sanitizer result is `allow`.
- Never treat confirmation overlay display as one-shot state; after confirm, cancel, block or failure, the resident hook must be ready for the next protected Send.
- Never treat Cancel as approval, pass-through, or a future bypass.
- Never send edited sanitized text until it has been locally verified.
- Never expose raw emergency bypass through the normal Send key; emergency bypass must be explicit, one-shot, confirmed twice, audited raw-free, and policy-blockable.
- Never claim protection after installation until a selected AI profile is actually verified as `protected`; unconfigured selected surfaces must fail closed.
- Provide an emergency bypass/disable action that is visible, audited raw-free, and not easy to trigger accidentally.
- Raw prompts, raw config contents and raw composer text must not be logged.
- Profile diagnostics may record app identity, control metadata, binding names, lengths, statuses and reason codes only.

## Open Questions

Resolved in `../SUBMIT_INTERCEPTION_RESEARCH_2026-07-20.md`:

1. No stable local source for the prompt submit shortcut is confirmed. Use `user_verified` binding capture first; config discovery is optional until documented or empirically stable.
2. Distinguish send from newline only through profile-verified context: foreground app, composer UIA shape, exact submit binding, exact newline binding, and IME/dead-key checks.
3. Use layered emergency escape: fast hook callback, pass-through for non-matches, local `Ctrl+Alt+Shift+Pause` disable window, tray controls, watchdog, and fail-closed submit suppression.
4. Enterprise mode should allow admins to lock protected profiles and disallow silent `hotkey_only` degradation for protected AI apps.
5. Profile mismatch should produce `surface_unverified` with visible tray/confirmation warning and raw-free mismatch reason codes.
