# ADR-006: Continuously Proven Protected Send Path

## Status

Accepted

## Context

The prompt-protection product has individual mechanisms for input interception,
surface recognition, UI Automation, local sanitization, confirmation, text
replacement, and Send replay. Each mechanism can pass isolated tests while the
real Windows desktop path fails between components: a dialog opens on the wrong
thread, foreground activation is refused, the composer changes, text is not
written, or a replay reaches a different control.

For a security boundary, a collection of green component tests is insufficient.
The release must be able to show that one selected ChatGPT Desktop configuration
executes a complete protected Send path and blocks every broken path locally.

## Decision

Code Sanitizer treats every protected Send as a single correlated resident
operation. The operation owns an opaque `attempt_id`, the immutable resident
snapshot generation, and the captured target identity. It publishes a raw-free
transition trace from interception to a terminal outcome.

```text
send_detected
  -> target_matched
  -> composer_read
  -> sanitized
  -> overlay_created
  -> overlay_foreground_confirmed
  -> approved | cancelled
  -> text_written
  -> send_injected
  -> sent_safely | blocked(reason)
```

Safe prompts use an explicitly recorded no-overlay branch. Every other omitted,
stale, duplicate, or out-of-order transition blocks the original Send. The trace
contains identifiers, state codes, target fingerprints, and durations only; it
never contains prompt text, mappings, sensitive values, file paths, UI control
names, or exception messages.

The resident runtime owns one dedicated UI thread and serialized overlay queue.
The low-level input callback does only fast classification, suppression and
scheduling. It never directly creates a dialog or waits for user interaction.

Release acceptance is limited to a pinned ChatGPT Desktop compatibility
fingerprint. The fingerprint contains raw-free app/package version, process and
window identity signals, composer UI Automation shape, configured Send/newline
pair and any supported Send-control evidence. A different surface is
`unsupported_surface` until it has its own evidence.

Acceptance has two required layers:

1. A deterministic local reference composer drives the real Windows hook, UI
   Automation, overlay, text replacement and replay mechanisms, without a live
   cloud service.
2. A repeatable live ChatGPT Desktop contract run records the same raw-free
   trace for the pinned fingerprint and configured Send binding.

The product may claim the pinned path is `protected` only when both layers pass
for the shipped build. This decision does not change the separate status of
project-file protection, which remains unsupported until a file-context boundary
exists.

## Consequences

Positive:

- Failures become identifiable as one missing or failed stage in a specific
  protected Send attempt.
- Tests cover the actual Windows integration path instead of only component
  contracts.
- Overlay lifetime and foreground behavior have one owner.
- Compatibility claims become precise: a supported fingerprint, not an
  unbounded claim about every ChatGPT Desktop version.

Negative:

- The native submission layer needs a correlated-operation contract and trace
  recorder, plus a long-lived overlay dispatcher.
- A local reference composer must be maintained as an acceptance fixture.
- A ChatGPT Desktop update can temporarily downgrade the profile to
  `unsupported_surface` until its fingerprint is re-verified.

## Guardrails

- Do not treat a trace as proof if its terminal outcome is missing or not
  `sent_safely`.
- Do not write raw data to traces, screenshots, diagnostics or acceptance
  artifacts.
- Do not create one overlay thread per Send attempt or block the input-hook
  callback waiting for it.
- Do not claim protection for a fingerprint that has not passed both required
  acceptance layers.
- Do not use a live cloud response as the sole evidence of Send; the local
  deterministic reference-composer proof remains mandatory.
