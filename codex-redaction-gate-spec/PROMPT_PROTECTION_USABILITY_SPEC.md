# Prompt Protection Usability Specification

## Problem Statement

Code Sanitizer exposes separate Codex Desktop and ChatGPT Desktop verification
actions even though a user normally works in one active desktop client. The
verification console can take focus away from the target composer. The tray
also presents dense diagnostic status text and engineering commands, while
ordinary status text cannot be selected for copying. Most importantly, the
native hook must never make normal typing or editing unusable.

The current setup experience can also complete without confirming which app was
recognized, whether the binding was saved, whether the resident hook became
active, or why a later protected Send was blocked. A user who chooses
`Ctrl+Enter`, presses verification, and focuses a composer must not have to
guess whether to wait, retry, or stop sending sensitive information.

## Solution

The installed tray application provides one normal setup action: `Set up prompt
protection`. It asks for the real Send-key pair, temporarily gets out of the
way, detects the focused supported Codex Desktop or ChatGPT Desktop composer,
and verifies that profile locally. On a fresh installation it opens
automatically until one profile is verified.

The keyboard hook performs expensive composer checks only for a potential Send
gesture. Ordinary characters, navigation, clipboard shortcuts, and editing are
passed directly to Windows. A recognized selected client with no verified
binding remains fail-closed for Enter while setup is incomplete. If a composer
check takes longer than the hook's immediate budget, the original Send remains
blocked and the verified check continues outside the hook; a successful result
then completes one protected Send rather than silently dropping the request.

The tray menu is a user surface: it contains protection controls, setup,
sensitive terms, restore, and a readable protection-status window. Audit,
diagnostics, and command-reference commands remain CLI-only. Status rows use
selectable read-only text controls and the tray summary is short and readable.

Setup is a visible resident-owned workflow. It shows the current step and a
durable raw-free result: waiting for a composer, composer recognized, binding
verification, resident-hook activation, or one specific next action when it
cannot complete. A protected Send uses the same resident state to say why it
was blocked; different failures must not collapse into only `Send blocked`.

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
11. As a user, I want the configured Send key to send a safe prompt even when
    the composer takes time to inspect, so that protection does not turn my
    Send key into a dead key.
12. As a user, I want the tray menu to show the emergency bypass combination,
    so that I can use the exceptional raw-send route only deliberately.
13. As a user, I want the tray menu to tell me why setup is incomplete or
    verification failed and what action to take, so that a generic active
    status never makes me assume the current application is protected.
14. As a user, I want setup to show that it is waiting for my composer and
    which supported application it recognized, so that focusing the right
    window is a verifiable action rather than a guess.
15. As a user, I want to see whether the selected Send and newline keys were
    saved and whether the resident interception hook became active, so that I
    know when protection is genuinely ready.
16. As a user, I want every setup failure to name one safe next action, so
    that I can recover without sending a sensitive prompt to test it.
17. As a user, I want a blocked protected Send to name its raw-free cause,
    such as setup not saved, composer changed, verification unavailable, or
    local protection unavailable, so that I do not retry blindly.

## Implementation Decisions

- `FirstRunSetupController` owns focused-profile verification and profile-store
  updates. It records one detected protected profile as sufficient for normal
  first-run completion.
- The resident tray owns launching the single setup workflow and reloading the
  resident runtime only after that workflow succeeds.
- `WindowsNativeSubmitHookHost` owns the fast keyboard prefilter. Its
  fail-closed state is a potential Send gesture in a recognized selected client
  when configuration is missing or the protected classification cannot finish.
  A delayed classification delivers its final result once to the resident
  controller outside the low-level hook; the controller alone decides whether
  the protected Send flow can run.
- The UI renders only the resident protection-state projection. It does not
  infer readiness from local flags or from a successful dictionary operation.
- User-facing status details are raw-free and selectable. CLI remains the
  support surface for audit and detailed diagnostics.
- The tray summary identifies the configured protected desktop profile and its
  Send binding when available. If setup is missing, verification is not
  confirmed, or local protection needs repair, it gives one raw-free reason and
  directs the user to the corresponding menu action. It must not say simply
  `active` for a different or unusable profile.
- `Ctrl+Alt+Shift+Pause` is the visible emergency bypass combination. It is
  displayed as an exceptional, temporary raw-send route and is never presented
  as a normal way to submit prompts.
- The resident protection snapshot owns the setup-verification lifecycle,
  recognized profile identity, selected bindings, and terminal result. The
  setup form, tray summary, and local-status form only render that snapshot.
- The setup lifecycle is `idle` -> `waiting_for_focus` ->
  `composer_recognized` -> `verifying_binding` -> `activating_protection` ->
  `protected`, or one terminal raw-free failure with an explicit next action.
  A repeated setup attempt may replace only an earlier terminal result.
- The protected-Send lifecycle retains a stable raw-free reason code and
  recommended action for every blocked result. User-visible text is a fixed
  allowlist and never includes prompt content, dictionary values, mappings,
  paths, or exception messages.

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
- Test delayed Send classification through an injected hook seam: the original
  input is blocked, the completed guarded result starts exactly one protected
  flow, and a failed or unverified result remains blocked without cloud
  submission. The test must not require a live desktop client or cloud access.
- Test the tray summary for a ready protected profile, incomplete setup, and
  local-protection repair. Assertions use only public raw-free wording and the
  visible emergency-bypass combination.
- Test setup progress through an injected focused-composer verifier and runtime
  factory. Assert every lifecycle step and terminal result reaches the setup
  form, tray summary, and local-status view without timers, live focus, or a
  cloud request.
- Test every blocked protected-Send reason through the resident snapshot and
  public rendered text. Verify it gives the matching next action and remains
  raw-free.

## Out of Scope

- Browser, PWA, and unsupported desktop surfaces.
- Programmatic UI Automation Send, which remains explicitly unsupported.
- Project-file ingress protection, which remains a separate architecture track.
- Removing CLI diagnostics or audit functionality from the installed package.

## Further Notes

If the active window cannot be verified, the product must say setup is required
or the surface is unsupported. It must never claim that another configured
desktop client protects the current window.

The visible result `Send blocked` alone is insufficient for acceptance. It may
be used as a heading only when accompanied by a specific raw-free reason and
one next action.
