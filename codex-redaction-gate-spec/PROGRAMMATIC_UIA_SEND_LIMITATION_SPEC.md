# Programmatic UI Automation Send Limitation

## Problem Statement

Code Sanitizer can suppress a selected Codex Desktop or ChatGPT Desktop Send gesture when it originates from the verified keyboard or mouse path. Windows UI Automation can expose an `Invoke()` observation for a third-party Send control, but that observation is provider-timed and cannot cancel the already initiated action. Reporting that path as protected would give users a false assurance that sensitive data cannot reach the cloud.

## Solution

For the Windows desktop product, programmatic UI Automation `Invoke()` is explicitly unsupported until Code Sanitizer has a verified pre-action enforcement boundary. The resident profile continues to report its verified keyboard/mouse Send capability independently. Every profile and tray/CLI diagnostic also publishes the raw-free status `programmatic_uia_invoke_unsupported`; it must never be represented as `protected` or as a post-action safety result.

## User Stories

1. As a Codex or ChatGPT Desktop user, I want to know which Send paths Code Sanitizer protects, so that I do not assume unsupported automation is safe.
2. As a security reviewer, I want an explicit raw-free status for programmatic UI Automation Send, so that product diagnostics cannot overstate enforcement.
3. As a user who sends prompts with the verified keyboard shortcut, I want that protection status to remain available, so that an unsupported automation path does not disable ordinary protected work.
4. As a user who clicks the verified Send control, I want the same protected flow as keyboard Send, so that normal mouse use remains covered.
5. As an operator, I want tray and CLI status to distinguish manual protected Send from programmatic UI Automation `Invoke()`, so that readiness evidence is actionable without exposing prompt or window data.
6. As a maintainer, I want one capability contract rather than an attempted post-action interception, so that later pre-action integrations can replace the unsupported status safely.

## Implementation Decisions

- The selected product decision is the explicit-unsupported option, not a cloud-egress proxy or an in-client extension.
- `programmatic_uia_invoke_unsupported` is a stable raw-free status identifier.
- The status is published at the resident profile/tray/CLI capability seam, alongside the normal keyboard/mouse protection status.
- A verified keyboard or mouse Send path may remain `protected`; this does not imply coverage of programmatic UI Automation `Invoke()`.
- Code Sanitizer does not subscribe to UI Automation `InvokedEvent` as a prevention mechanism and does not claim post-action observation as protection.
- A future supported implementation requires a verified pre-action boundary inside the selected client or before its cloud submission boundary. That work is outside this decision.

## Testing Decisions

- Test the public raw-free diagnostics and tray/CLI status contract, not a synthetic UI Automation event that cannot prove prevention.
- A good test proves that a profile with protected manual Send still reports `programmatic_uia_invoke_unsupported` for the automation path.
- Tests must prove that the unsupported status contains no prompt text, window title, automation identifier, or exception text.
- Extend the existing profile diagnostic and tray status formatter tests, which are the highest stable seam for resident capability reporting.

## Out of Scope

- Blocking, sanitizing, or observing programmatic UI Automation `Invoke()` after the action has begun.
- A network proxy, TLS interception, cloud-egress gateway, or modifications inside Codex Desktop or ChatGPT Desktop.
- Broadening low-level keyboard or mouse hooks to unrelated applications.

## Further Notes

This decision is intentionally conservative. It removes a misleading coverage claim without changing the verified keyboard/mouse protection contract. A future pre-action boundary can replace this status only after tests prove that a programmatic activation is prevented before cloud submission.
