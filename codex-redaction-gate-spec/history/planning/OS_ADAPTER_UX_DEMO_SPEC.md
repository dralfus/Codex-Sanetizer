# OS Adapter UX Demo Spec

## Problem Statement

The sanitizer core can already decide `allow`, `confirm` and `block`, but the user still cannot feel the intended workflow in the real Codex/ChatGPT desktop app. A separate local composer is useful technically, but it does not answer the main usability question: "what happens when I type my prompt directly in Codex and try to send it?"

The desired demo must show the sanitizer in the user's normal writing surface, without relying on an unverified Codex prompt rewrite hook and without making a browser extension the primary path.

## Solution

Build an OS-level interaction adapter demo that grows into the production workflow. For the first production-targeted path, the target surface is the Windows Codex/ChatGPT desktop app. The user writes in the existing app composer and presses that app's configured Send shortcut. Code Sanitizer intercepts the submit input for the selected AI surface, suppresses the raw submit, shows a local confirmation overlay when needed, and sends only the original safe prompt or approved `sanitized_text`.

A dedicated sanitizer hotkey may still exist for manual scan/apply, diagnostics and recovery, but it is no longer the primary workflow.

The core architecture must be adapter-based so the same sanitizer can later support other AI tools, Linux desktop environments, and CLI wrappers without changing the sanitizer pipeline.

## User Stories

1. As a Codex/ChatGPT desktop user, I want to type in the normal app composer, so that the sanitizer does not force me into a separate prompt editor.
2. As a Codex/ChatGPT desktop user, I want Code Sanitizer to intercept my normal Send shortcut, so that I cannot forget to run a separate sanitizer hotkey.
3. As a Codex/ChatGPT desktop user, I want the adapter to read the active composer text, so that I can review the exact prompt I was about to submit.
4. As a Codex/ChatGPT desktop user, I want a local overlay when sensitive data is detected, so that I can inspect the sanitized prompt before anything leaves the machine.
5. As a Codex/ChatGPT desktop user, I want replacements highlighted in the sanitized prompt, so that I can understand what changed.
6. As a Codex/ChatGPT desktop user, I want counts by sensitive type, so that I can quickly see whether the prompt contains a token, URL, IP, customer name or another protected entity.
7. As a Codex/ChatGPT desktop user, I want non-restorable redactions called out clearly, so that I understand which values cannot be restored locally later.
8. As a Codex/ChatGPT desktop user, I want `Confirm sanitized prompt`, so that only the sanitized text is applied or sent.
9. As a Codex/ChatGPT desktop user, I want `Cancel`, so that no prompt is changed or sent if I do not trust the replacement.
10. As a Codex/ChatGPT desktop user, I want a block screen with raw-free reason codes, so that unsafe prompts do not leak through error messages.
11. As a Codex/ChatGPT desktop user, I want a dry-run mode, so that the first usability demo can show the workflow without sending anything to the cloud.
12. As a Codex/ChatGPT desktop user, I want an apply-only mode, so that the sanitized prompt is written back to the composer but not submitted automatically.
13. As a Codex/ChatGPT desktop user, I want an explicit send mode, so that automatic submission is only enabled after the safer apply-only path is proven.
14. As a security reviewer, I want the adapter to fail closed if it cannot identify the active text surface, so that the original prompt is not guessed or sent unsafely.
15. As a security reviewer, I want raw prompt values excluded from logs and diagnostics, so that debugging the adapter does not create a second leakage path.
16. As a maintainer, I want app-specific behavior isolated in surface profiles, so that Codex Desktop, ChatGPT Desktop and future apps can be supported without changing sanitizer logic.
17. As a maintainer, I want platform-specific input/output code isolated behind interfaces, so that Windows UI Automation can be replaced by Linux accessibility or CLI wrappers later.
18. As a future Linux user, I want the core interaction contracts to be platform-neutral, so that Linux support does not require a sanitizer rewrite.
19. As a future CLI user, I want wrapper-mode integration to use the same interaction flow, so that `safe-codex` or `safe-claude` can sanitize before invoking a model CLI.
20. As a product owner, I want a visible UX demo before production hardening, so that menus, overlays, wording and safety states can be judged by feel.
21. As a user, I want to choose which AI apps are protected, so that Code Sanitizer intercepts only the intended Codex/ChatGPT surfaces.
22. As a user, I want Code Sanitizer to read or verify the selected AI app's configured Send shortcut, so that it protects my actual workflow rather than a hardcoded assumption.
23. As a security reviewer, I want `Protected` status to mean native submit interception is active for the selected app, so that hotkey-only mode is not mistaken for enforcement.

## Implementation Decisions

- The primary UX direction is OS-level native submit interception for selected AI desktop apps, not a browser extension and not a hotkey-first workflow.
- The first demo targets Windows Codex/ChatGPT desktop app only.
- Hook-only `UserPromptSubmit` remains guard mode because prompt rewriting is not treated as verified.
- Hotkey-triggered workflow is secondary: useful for diagnostics, manual apply and rescue, but insufficient as the main protection.
- The first production-safe workflow suppresses native submit, sanitizes locally, then replays the verified submit binding only after allow or explicit confirmation.
- The adapter owns cloud-bound submission only after confirmation. If the adapter cannot prove the sanitized text was applied, it must not submit.
- The adapter must protect only explicitly enabled AI profiles and must not intercept shortcuts in unrelated applications.
- Submit binding discovery must prefer the selected AI app's local configuration when available; otherwise onboarding must ask the user to choose/record the binding and verify it.
- If the active AI profile, composer shape or submit binding is unknown, the status is not `protected`; it is `not_configured`, `binding_unknown`, `surface_unverified` or `degraded_hotkey_only`.
- The sanitizer core remains app-agnostic and platform-agnostic.
- Introduce interaction contracts for active surface discovery, text capture, text replacement, submit action, hotkey trigger and confirmation overlay.
- Windows-specific implementation uses UI Automation and keyboard/clipboard fallback only inside the Windows adapter boundary.
- App-specific matching lives in surface profiles, such as Codex Desktop and ChatGPT Desktop.
- The overlay can use a Windows-native UI for the first demo, but it must be behind a confirmation view contract so a future Linux UI can replace it.
- CLI support is future wrapper mode, not terminal keystroke interception.
- Browser extension support is not part of this demo and is not the primary strategy for the user's Codex workflow.
- Adapter diagnostics must use raw-free statuses, control metadata and lengths/counts, not prompt contents.
- The interaction state machine is:

```text
Idle
  -> SurfaceDiscovery
  -> SubmitBindingMatch
  -> SuppressNativeSubmit
  -> CaptureText
  -> Sanitize
  -> AllowReplaySubmit
  -> NeedsConfirmationOverlay
  -> ApplySanitizedText
  -> ReplayVerifiedSubmit
  -> Completed
  -> Blocked
  -> FailedClosed
```

## Testing Decisions

- Use fake interaction adapters for most automated tests so sanitizer UX behavior is testable without controlling the real desktop.
- Add Windows UI Automation smoke diagnostics separately from unit tests, because real desktop focus and accessibility trees are environment-dependent.
- Test at the highest seam: the interaction orchestrator should prove capture, sanitize, confirm, apply and submit decisions end to end.
- Keep raw-leak regression tests around adapter diagnostics and overlay models.
- Test failure states explicitly: no active supported app, disabled profile, unknown submit binding, shortcut in unrelated app, unreadable text surface, failed suppression, failed write-back, failed submit and sanitizer block.
- A manual usability checklist is required for the Windows Codex/ChatGPT app demo.

## Out of Scope

- Browser extension integration.
- Linux desktop implementation.
- CLI wrapper implementation.
- Transparent rewriting through Codex `UserPromptSubmit`.
- Production installer, autostart, signing and managed deployment.
- OCR/PDF/Office attachment extraction inside the desktop adapter.
- Support for arbitrary applications beyond the configured Windows Codex/ChatGPT desktop surface profiles.

## Further Notes

This spec does not replace the sanitizer MVP. It adds a UX frontier around the implemented core. The critical product bet is that the adapter layer, not the sanitizer, owns app-specific capture/apply/submit behavior.

The first implementation slice currently includes platform-neutral interaction contracts, fake-adapter orchestration tests, Windows surface profile diagnostics, CLI preview commands, a WinForms confirmation overlay, explicit Windows hotkey modes and a manual checklist. The next slice must promote native submit interception and AI profile configuration to the primary path. The demo remains Windows Codex/ChatGPT desktop only; Linux desktop and CLI wrappers are documented future adapters behind the same contracts.
