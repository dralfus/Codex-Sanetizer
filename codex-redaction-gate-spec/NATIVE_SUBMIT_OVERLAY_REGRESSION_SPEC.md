# Spec: Native Submit Confirmation Overlay Regression

## Problem Statement

The user has a verified `chatgpt-desktop` profile with `submit_binding=Enter`, `newline_binding=Ctrl+Enter`, and `capability_status=protected`. In live testing, the replacement confirmation overlay appeared and worked for one protected prompt, but later protected sends did not consistently show the overlay. When the overlay did appear, it opened behind the active window instead of becoming the active foreground decision surface.

This creates two safety failures:

- the user can press the normal AI app Send key and not get a confirmation decision for sensitive text;
- the user may miss a background confirmation overlay and believe Code Sanitizer did nothing or is stuck.

## Solution

Native submit interception must treat every matching Send gesture in a verified Codex/ChatGPT composer as a fresh protected submit attempt. For every attempt, Code Sanitizer must suppress the original send, capture the composer text, sanitize locally, and then either submit safe text, show an active confirmation overlay for `confirm`, or block/fail closed.

The confirmation overlay must become active and visible in front of the selected AI app whenever a decision is required. The overlay must not be a one-shot window tied to onboarding evidence, the first replacement event, or a stale process-level state. Closing or confirming one overlay must leave the resident hook ready for the next protected Send.

## User Stories

1. As a Codex/ChatGPT Desktop user, I want every press of the verified Send key to be intercepted while protection is enabled, so that one successful replacement does not leave later prompts unprotected.
2. As a Codex/ChatGPT Desktop user, I want the replacement confirmation overlay to become the active foreground window, so that I cannot miss a security decision hidden behind the AI app.
3. As a Codex/ChatGPT Desktop user, I want confirming or canceling one replacement to return Code Sanitizer to a ready state, so that the next protected prompt is handled normally.
4. As a Codex/ChatGPT Desktop user, I want safe prompts, confirmed prompts, canceled prompts, block decisions and failures to leave clear raw-free status, so that I can tell whether protection is still running.
5. As a security reviewer, I want regression coverage proving repeated protected Send attempts trigger independent sanitizer decisions, so that native submit protection is not accidentally one-shot.
6. As a maintainer, I want overlay activation covered at the confirmation UI boundary, so that focus regressions are caught without needing live cloud submissions.

## Implementation Decisions

- Native submit protection remains scoped to verified Windows Codex/ChatGPT Desktop profiles.
- The protected submit flow must not depend on apply-only evidence, onboarding commands, or one-time verification state after a profile is already `protected`.
- The resident native submit hook must continue listening after each suppressed submit flow completes, including confirm, cancel, block and failure paths.
- The confirmation overlay must explicitly request foreground activation when shown for a native submit decision. If Windows refuses foreground activation, Code Sanitizer must surface a visible status instead of silently leaving the user unaware.
- Overlay activation must not cause raw prompt text, raw window titles, screenshots or raw sensitive terms to be logged.
- A protected `confirm` decision must not be submitted until the user approves the active overlay and the sanitized text is verified in the composer.
- If a second protected Send occurs while a confirmation overlay is already open, the product must avoid sending raw input. It may ignore the duplicate, keep the first overlay active, or report an in-progress state, but it must not replay the native submit binding.

## Testing Decisions

- The highest-value seam is the native submit controller plus tray protection flow: tests should trigger multiple matching Send gestures in sequence and assert that each one reaches a fresh sanitizer/confirmation path.
- Confirmation UI tests should verify that the overlay form requests activation/topmost foreground behavior when shown.
- Product smoke should include a repeated-submit regression path using disposable surfaces, without live cloud submission or raw sensitive data.
- Regression tests should cover confirm, cancel, block and failure returning the resident hook to ready state for the next Send.
- Tests must remain raw-free: fixture sensitive values should not appear in diagnostics, audit, status text or test artifacts.

## Out of Scope

- Expanding support beyond Windows Codex/ChatGPT Desktop.
- Guaranteeing foreground activation in every Windows policy scenario where the OS refuses focus stealing.
- Sending real prompts to a live cloud service as part of automated tests.
- Changing dictionary matching or sanitizer detection rules.

## Further Notes

This regression is separate from profile onboarding. A profile can be correctly verified as `protected`, but the runtime submit path still fails if the overlay is not active or the hook behaves as one-shot. Release verification must check both readiness and repeated runtime behavior.
