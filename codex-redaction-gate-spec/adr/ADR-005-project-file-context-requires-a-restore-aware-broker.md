# ADR-005: Project File Context Requires a Restore-Aware Broker

## Status

Proposed

## Context

The current Windows desktop product protects the selected Codex/ChatGPT composer submit path. It can sanitize prompt text, pasted text, plain-text attachment snippets, and explicit `file_snippet` content parts when an adapter passes those text parts through the sanitizer before cloud submission.

That is not the same as protecting a full coding-agent workflow. In that workflow the cloud boundary can be crossed when the agent reads local project files, receives file snippets as model context, proposes patches, or uses tool outputs derived from project files. A keyboard or UI submit interceptor sees the user's prompt, but it does not automatically see every file read, attachment upload, tool output, or patch payload that the Codex runtime may send to the model.

If Code Sanitizer claims project-file protection while only guarding the composer, the user can reasonably believe that internal domains, usernames, paths, customer names, or secrets inside repository files are protected when they are not.

## Decision

Do not claim end-to-end protection for project files until Code Sanitizer owns a file-context boundary.

The target design for protected coding workspaces is a restore-aware project file broker:

```text
Codex requests project context
  -> local broker reads supported files
  -> sanitizer scans file content, paths, and generated tool output
  -> unsupported or unreadable content fails closed
  -> only sanitized virtual file content crosses the cloud boundary
  -> model returns sanitized edits or explanations
  -> broker restores restorable pseudonyms locally before writing files, when policy allows
  -> audit records raw-free source ids, entity counts, actions, and write decisions
```

Until that broker exists, Code Sanitizer may only say that explicit text attachments or file snippets are protected when they are passed through the sanitizer pipeline by the active adapter. It must not imply that arbitrary repository reads by Codex, ChatGPT, MCP tools, or connectors are covered.

## Consequences

Positive:

- The product boundary stays honest and testable.
- Composer protection can continue independently from project-file protection.
- The sanitizer engine and vault remain reusable for future file-context work.
- Unsupported file types can fail closed before they become cloud context.

Negative:

- Full protection for coding tasks needs deeper Codex/tool integration than Windows keyboard interception.
- File writes require restore-aware patch handling, not blind text replacement.
- The broker must handle paths, filenames, encodings, large files, generated files, and binary/document formats carefully.
- Verification cannot rely only on UI smoke tests; it needs raw-free evidence of every outgoing file-context payload.

## Guardrails

- Treat project files, filenames, paths, tool outputs, and diffs as potential cloud-bound content.
- Never read and forward unsupported file content as raw context.
- Never write restored sensitive values into logs, audit events, screenshots, or cloud-visible diagnostics.
- Restore generated edits only in the local writer, after validating that the edit is intended for the same protected workspace.
- Secrets remain non-restorable by default even when found in files.
- Fail closed for protected workspaces when the file broker is unavailable, disabled, or cannot prove coverage.
- Keep the user-visible status separate: `composer_protected` is not the same as `project_files_protected`.

## Open Questions

1. Which Codex Desktop or Codex runtime extension point can force all project file reads through a local broker?
2. Can attachments uploaded through the desktop app be intercepted before upload, or must they be blocked unless imported through Code Sanitizer?
3. Should restored file writes be automatic for identifiers, or should the user approve restored diffs before they touch disk?
4. How should binary, PDF, Office, image, archive, and generated build artifacts be represented in protected workspaces?
5. Should protected workspace mode be opt-in per repository, inherited from global policy, or required by enterprise policy?
