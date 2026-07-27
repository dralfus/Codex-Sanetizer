# Spec: Native Submit Cancel, Edit, Bypass, and Onboarding Enforcement

## Problem Statement

The user can type a sensitive value in Codex/ChatGPT Desktop, press Send, see the Code Sanitizer replacement window, press Cancel, return to the AI app composer, and then press Send again. In live testing, the next Send can bypass the replacement window and send the raw sensitive value to the cloud.

This makes Cancel unsafe. Cancel must mean "do not submit now and return me to editing"; it must never mean "allow a later Send to bypass Code Sanitizer." The user also needs a safe way to manually edit the sanitized text before approving it, and a deliberately separate emergency bypass action for rare cases where raw submission is intentionally required.

After installation, an unconfigured user can also press Send before the Codex/ChatGPT profile is protected. That first-run gap is unsafe because the product is installed but not yet enforcing the selected AI app's submit path.

The actual Send shortcut can differ from the application's visible preference. For example, a user may configure or observe `Ctrl+Enter` as Send while ordinary `Enter` remains needed for normal interaction. The current product hard-codes `Enter` as Send for ChatGPT verification, which can both miss a real `Ctrl+Enter` submission and block unrelated `Enter` actions in the selected AI window.

## Solution

Every press of the verified Send binding in a selected Codex/ChatGPT composer must run the Code Sanitizer native submit handler while protection is enabled, regardless of any previous Cancel, close, failed confirmation, or edit. If sensitive terms are still present, the replacement window must appear again. Cancel only returns to the AI app composer; it does not create an allow token, remembered pass-through, or one-shot bypass for the next Send.

While Code Sanitizer is running and the selected Codex/ChatGPT profile is protected, ordinary composer Send must be suppress-first and must not be able to submit raw text that still contains detected sensitive terms. The only permitted cloud-submission paths from that protected surface are: no detected sensitive terms, verified sanitized text submitted from the replacement overlay, or the explicit one-shot emergency bypass action.

The replacement window must provide three distinct actions:

1. Confirm sanitized prompt: submit only the verified sanitized text.
2. Edit sanitized prompt: let the user modify the sanitized text locally before submitting, then verify and submit only the edited sanitized text.
3. Cancel: send nothing and return to editing in the AI app.

Sending raw text with detected sensitive terms is allowed only through a separate emergency bypass action. The proposed default emergency bypass gesture is `Ctrl+Alt+Shift+Enter` while the replacement window is active, plus a visible button/menu action labeled `Emergency send original once`. This action must be difficult to hit accidentally, must show a second confirmation, must be audited raw-free, and must never become the default Send behavior.

After installation, the app must force profile setup before claiming protection. If no selected Codex/ChatGPT profile is protected, the resident app must show an active setup window and block or fail closed matching AI submit attempts until onboarding succeeds. The setup window should run the delayed focus workflow: show `waiting_for_focus`, give the user time to focus the target composer, and mark the profile `protected` only after successful verification.

The setup and re-verification workflow must explicitly record the user's effective Send shortcut and newline shortcut for each selected AI profile. It must support at least the inverse pairs `Enter` Send / `Ctrl+Enter` newline and `Ctrl+Enter` Send / `Enter` newline. The workflow must never silently assume `Enter`, overwrite a user-verified binding with a fixed default, or leave an old binding active after the user changes it. While a binding change is incomplete or cannot be verified, the profile is not protected.

At runtime, Code Sanitizer must intercept only the exact configured Send shortcut in the verified composer. The configured shortcut may be suppressed fail-closed on the identifiable AI Send control, but ordinary typing, the configured newline shortcut, unrelated controls such as skill selection, and keyboard input in unselected applications must pass through unchanged. A second resident instance must not install a competing hook; it must activate the existing resident UI or exit with a raw-free status.

## User Stories

1. As a Codex/ChatGPT Desktop user, I want every press of Send to run Code Sanitizer, so that a previous Cancel cannot make later prompts bypass protection.
2. As a Codex/ChatGPT Desktop user, I want Cancel to send nothing, so that I can safely return to editing without weakening future interception.
3. As a Codex/ChatGPT Desktop user, I want sensitive terms to trigger the replacement window again after I cancel and press Send again, so that raw values cannot slip through by retrying.
4. As a Codex/ChatGPT Desktop user, I want to edit the sanitized text inside the replacement window, so that I can improve the prompt without returning raw sensitive data to the composer.
5. As a Codex/ChatGPT Desktop user, I want edited sanitized text to be verified before send, so that manual edits cannot reintroduce forbidden terms.
6. As a Codex/ChatGPT Desktop user, I want raw submission to require a separate emergency action, so that accidental Send never becomes a bypass.
7. As a Codex/ChatGPT Desktop user, I want the emergency bypass to require a second confirmation, so that I cannot trigger it accidentally.
8. As a security reviewer, I want emergency bypass events audited without raw prompt text, so that intentional bypasses are visible without leaking the data.
9. As a first-time user, I want setup to appear immediately after installation, so that I know which AI app is protected.
10. As a first-time user, I want Code Sanitizer to block selected AI app Send attempts until setup is complete, so that installation does not create a false sense of protection.
11. As a maintainer, I want tests to prove Cancel does not arm a pass-through state, so that this failure cannot regress.
12. As an enterprise admin, I want policy to disable emergency raw bypass, so that managed environments can require redaction with no user override.
13. As a Codex/ChatGPT Desktop user, I want to choose and verify my application's actual Send shortcut, so that CS protects the shortcut I really use instead of assuming `Enter`.
14. As a Codex/ChatGPT Desktop user, I want to change my Send shortcut without restarting or silently weakening protection, so that my profile remains accurate after I change an AI-app preference.
15. As a Codex/ChatGPT Desktop user, I want ordinary `Enter`, newline input, and skill-selection controls to keep working when they are not my configured Send action, so that protection does not make the AI app unusable.
16. As a security reviewer, I want CS to intercept the exact verified Send action and identifiable Send button only, so that raw sensitive prompts cannot leave while unrelated application input is not blocked.
17. As a user, I want only one resident CS instance to own input protection, so that duplicate tray processes cannot produce conflicting keyboard behavior.
18. As a Codex/ChatGPT Desktop user, I want one coherent protection state for each Send attempt, so that reload, setup, and deferred confirmation cannot combine stale and current state into a raw pass-through.
19. As a user of other Windows applications, I want Code Sanitizer to distinguish selected-client uncertainty from unrelated input, so that it fails closed at the cloud boundary without disrupting ordinary applications.

## Implementation Decisions

- Cancel is a terminal decision for the current attempt only. It sends nothing and does not mutate profile state, hook readiness, sanitizer policy, or future submit handling.
- The native submit controller must treat each matching Send gesture as a new attempt with no reuse of prior confirmation result.
- The confirmation overlay must own an editable sanitized text field. The original raw prompt remains hidden by default.
- Confirming edited sanitized text must run local verification before replaying the AI app submit binding.
- If edited text still contains forbidden values or cannot be verified in the composer, the product must fail closed and keep the user in a local decision surface.
- Emergency bypass is explicit and separate from Cancel. The proposed shortcut is `Ctrl+Alt+Shift+Enter` only while the replacement window is active. Outside the replacement window, the existing emergency disable path remains separate and must not submit raw text.
- Emergency bypass requires a second confirmation that explains the consequence: the original text will be sent to the selected AI cloud service once.
- Emergency bypass must be one-shot. It must not set a persistent allow, dictionary exception, profile downgrade, or "send raw next time" flag.
- Enterprise policy may disable emergency bypass entirely.
- First-run setup is part of the resident tray app, not a console-only workflow.
- If no selected profile is protected, the app must show an active setup window and expose the delayed focus verification flow from UI.
- Until setup succeeds, matching selected AI app submit attempts must be suppressed and reported as `setup_required` or an equivalent raw-free fail-closed status.
- Setup status must distinguish `waiting_for_focus`, `protected`, `surface_unverified`, `binding_unknown`, and `not_configured`.
- Send binding is profile data, not a tray-menu constant. The setup UI must expose the supported Send/newline shortcut pairs and show the currently saved pair before verification.
- Selecting a new pair invalidates the previous protected binding until delayed focus verification succeeds and the resident controller reloads the profile. A failed, cancelled, or timed-out change leaves the profile unprotected rather than retaining a stale protected binding.
- For v1, the supported shortcut pairs are `Enter` Send with `Ctrl+Enter` newline, and `Ctrl+Enter` Send with `Enter` newline. Unsupported combinations must be shown as unsupported rather than coerced to a default.
- The native hook must use the stored, verified profile binding for matching. It must not contain a ChatGPT- or Codex-specific hard-coded `Enter` assumption.
- The hook may fail closed for the stored Send shortcut on an identifiable Send control in the selected protected AI app. It must pass through that shortcut on non-Send controls and pass through every non-Send shortcut, including the stored newline shortcut.
- The resident tray process must enforce a per-user single-instance boundary. A second launch must not register another input hook; it should foreground the existing tray UI or exit with a raw-free status.
- Native resident protection is published as one immutable, versioned snapshot containing selected profiles and bindings, hook readiness, the guarded submit flow, classification capability, and target identity rules. A callback reads exactly one snapshot and cannot combine independently changing fields.
- Candidate reload/setup state is fully validated before atomic publication. If activation fails, the prior complete snapshot remains active. A selected AI Send attempt must not be released during a transition or because the candidate cannot be published.
- The event decision matrix is fixed: verified selected Send suppresses and guards; verified selected non-Send/newline passes; uncertainty inside a selected AI client suppresses with raw-free status; uncertainty outside selected AI clients passes through. This distinction prevents both cloud leakage and global keyboard interference.
- Deferred sanitize, overlay, and replay work carries the captured snapshot generation and composer/window identity. It must abort raw-free if that exact target cannot be revalidated; it must not use a later foreground-window lookup.

## Testing Decisions

- The highest-value seam is the resident native submit flow: trigger Send, cancel, change or keep composer text, trigger Send again, and assert the sanitizer/confirmation path runs again.
- Confirmation overlay tests should cover the editable sanitized text path and verify that edited text is the only submitted payload.
- Verification tests should prove edited sanitized text is rejected when it reintroduces sensitive terms.
- Emergency bypass tests should prove the normal Send key cannot bypass, while the explicit emergency bypass action is one-shot, audited raw-free, and policy-blockable.
- First-run setup tests should cover installed resident startup with no protected profile, active setup prompt, delayed focus verification, and fail-closed submit attempts before setup completion.
- Product smoke should include Cancel-then-retry behavior and setup-required status without live cloud submission or raw sensitive values.
- Binding tests must cover both supported shortcut pairs, persistence/reload of each pair, invalidation after a requested change, and rejection of an unsupported pair without a fallback to `Enter`.
- Native-hook tests must prove that with `Ctrl+Enter` as Send, `Enter` passes through in the verified composer and unrelated selected-app controls, while `Ctrl+Enter` is suppressed and guarded. The inverse pair must have symmetric coverage.
- Tests must prove that keyboard interaction with non-Send controls, including skill selection, remains available; an identifiable selected-app Send button remains fail-closed for keyboard and mouse activation.
- Resident-process tests must prove that a second launch does not create a second hook-owning tray instance.
- Lifecycle tests must prove atomic snapshot publication, rollback to the prior snapshot after failed reload, and the selected-client versus unrelated-client uncertainty matrix.
- Deferred-flow tests must prove that changing focus after suppression cannot redirect replay and instead results in raw-free abort.
- The installed-app release checklist must include a manual verification for each supported binding pair and must report the saved profile binding before sensitive test input is entered.

## Out of Scope

- Making raw bypass easy or available from the normal AI app composer.
- Persisting per-term allow decisions from the replacement window.
- Guaranteeing support for non-Windows AI apps in this version.
- Sending live cloud prompts as part of automated tests.

## Further Notes

This spec tightens the meaning of Cancel. Cancel is a safety stop, not an approval, bypass, or downgrade. Any behavior that lets raw sensitive text pass through after Cancel is a security bug.
