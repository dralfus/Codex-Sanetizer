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

The submit binding must be discovered from the target AI configuration when a stable, local, documented or empirically verified config source exists. If the config source is unavailable, onboarding must ask the user to choose or record the submit shortcut and then verify it against the selected app surface. The product must not silently assume `Enter`.

Submit interception is allowed only when all of these are true:

- the foreground surface matches an enabled AI profile;
- the focused element matches the verified composer shape for that profile;
- the pressed shortcut matches the profile's active submit binding;
- the current mode is `protected`, not `hotkey_only` or `diagnostic`;
- the sanitizer, vault, policy and confirmation UI are available.

If any condition fails, Code Sanitizer must not claim protection. If the pressed shortcut belongs to a selected protected surface but sanitizer processing cannot complete, it must suppress the original submit and fail closed.

Onboarding and re-verification must run against the user's real desktop session, not an agent or build sandbox's foreground window. The product must provide a delayed verification path from the installed tray app and CLI: the user starts verification, focuses the Codex/ChatGPT composer before the countdown ends, and the profile is saved as `protected` only when that focused composer verifies. The release test surface must keep exercising this readiness path so regressions do not silently downgrade native submit protection to manual hotkey mode.

Runtime protection must also be repeatable. Every matching Send gesture in a selected protected composer is a new guarded submit attempt; a previous confirmation must not disable or satisfy later prompts. The local confirmation overlay must request foreground activation whenever a replacement decision is required. If Windows refuses activation, the product must show raw-free visible status and must not submit raw text.

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
