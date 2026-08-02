# Prompt Protection Usability Specification

## Problem Statement

Code Sanitizer exposes separate Codex Desktop and ChatGPT Desktop verification
actions even though a user normally works in one active desktop client. The
verification console can take focus away from the target composer. The tray
also presents dense diagnostic status text and engineering commands, while
ordinary status text cannot be selected for copying. Most importantly, the
native hook must never make normal typing or editing unusable.

## Solution

The installed tray application provides one normal setup action: `Set up prompt
protection`. It asks for the real Send-key pair, temporarily gets out of the
way, detects the focused supported Codex Desktop or ChatGPT Desktop composer,
and verifies that profile locally. On a fresh installation it opens
automatically until one profile is verified.

The keyboard hook performs expensive composer checks only for a potential Send
gesture. Ordinary characters, navigation, clipboard shortcuts, and editing are
passed directly to Windows. A recognized selected client with no verified
binding remains fail-closed for Enter while setup is incomplete.

The tray menu is a user surface: it contains protection controls, setup,
sensitive terms, restore, and a readable protection-status window. Audit,
diagnostics, and command-reference commands remain CLI-only. Status rows use
selectable read-only text controls and the tray summary is short and readable.

## User Stories

1. As a Windows user, I want one setup action, so that I do not have to decide
   whether a technical profile is named Codex or ChatGPT.
2. As a user after installation, I want setup to open automatically when no
   profile is protected, so that I do not accidentally assume protection exists.
3. As a user, I want the setup window to release focus while I select my
   composer, so that verification can inspect the intended application.
4. As a user, I want the detected application and chosen Send key saved only
   after local verification, so that the protected status is trustworthy.
5. As a user, I want normal typing, paste, navigation, and editing to work in
   the selected composer, so that protection does not interfere with writing.
6. As a user, I want only the actual Send gesture intercepted, so that text
   entry remains responsive.
7. As a user, I want an unconfigured selected client to remain blocked for its
   potential Send key, so that setup does not create an unprotected submission
   path.
8. As a user, I want a short tray summary and a readable details window, so
   that I can understand readiness without parsing internal status identifiers.
9. As a user, I want to select and copy visible status text, so that I can
   share support information without screenshots.
10. As a user, I want audit and diagnostic tools removed from the normal tray
    menu, so that everyday controls are not confused with engineering tools.

## Implementation Decisions

- `FirstRunSetupController` owns focused-profile verification and profile-store
  updates. It records one detected protected profile as sufficient for normal
  first-run completion.
- The resident tray owns launching the single setup workflow and reloading the
  resident runtime only after that workflow succeeds.
- `WindowsNativeSubmitHookHost` owns the fast keyboard prefilter. Its
  fail-closed state is a potential Send gesture in a recognized selected client
  when configuration is missing or the protected classification cannot finish.
- The UI renders only the resident protection-state projection. It does not
  infer readiness from local flags or from a successful dictionary operation.
- User-facing status details are raw-free and selectable. CLI remains the
  support surface for audit and detailed diagnostics.

## Testing Decisions

- Test the focused setup seam with a deterministic focused-surface result: it
  must select the detected profile, preserve the chosen key pair, and not add a
  second default profile.
- Test the native hook prefilter without Windows timing: ordinary keys and a
  configured newline must bypass classification; the configured Send key and
  setup-pending Enter must be classified.
- Test the status form through its public rendered controls: each displayed
  status row must use selectable read-only text and remain raw-free across a
  refresh.
- Test tray-menu content by user-visible commands: it must contain one setup
  action and exclude audit, diagnostics, and command-reference entries.

## Out of Scope

- Browser, PWA, and unsupported desktop surfaces.
- Programmatic UI Automation Send, which remains explicitly unsupported.
- Project-file ingress protection, which remains a separate architecture track.
- Removing CLI diagnostics or audit functionality from the installed package.

## Further Notes

If the active window cannot be verified, the product must say setup is required
or the surface is unsupported. It must never claim that another configured
desktop client protects the current window.
