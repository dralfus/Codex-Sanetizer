# Spec: Protected Project File Workflow

## Problem Statement

Code Sanitizer currently protects the selected Codex/ChatGPT Desktop composer submit path and can sanitize explicit file snippets or plain-text attachments when they are routed through the sanitizer pipeline. This is not enough for coding-agent work.

When a user asks Codex to inspect or modify a project, sensitive data can leave the machine through repository file reads, filenames, paths, tool output, direct attachment upload, generated diffs, or patches. The user may reasonably assume that Code Sanitizer is protecting the whole task because the composer is protected, while raw project file content can still bypass that layer.

The product needs an honest and enforceable project-file protection mode that prevents local repository content from becoming model context unless it has been sanitized locally, and that restores restorable identifiers only on the local write path.

## Solution

Add a protected project-file workflow based on a restore-aware file-context broker.

From the user's perspective, protected workspace mode works like this:

1. The user marks a workspace as protected or an enterprise policy requires protection.
2. Codex requests project context through Code Sanitizer instead of direct file reads.
3. Code Sanitizer reads supported text files locally, sanitizes content, paths and filenames, and exposes sanitized virtual files to the model.
4. Unsupported or unreadable files fail closed or require explicit local conversion.
5. The model works with sanitized virtual files and returns sanitized explanations or edits.
6. Code Sanitizer validates returned edits against the protected workspace and sanitized source version.
7. Code Sanitizer restores restorable pseudonyms locally before writing, when policy allows, while keeping secrets non-restorable.
8. Status and diagnostics clearly distinguish `composer_protected` from `project_files_protected`.

Until this broker exists and is verified, the product must continue to say that arbitrary project files are not protected end-to-end.

## User Stories

1. As a Codex coding user, I want project file reads to pass through a local sanitizer, so that sensitive repository content does not bypass composer protection.
2. As a Codex coding user, I want sanitized virtual files to preserve code structure, so that Codex can still make useful changes.
3. As a Codex coding user, I want internal domains in source files to become stable pseudonyms before model visibility, so that infrastructure names remain local.
4. As a Codex coding user, I want usernames and workstation paths in files or filenames to become readable typed pseudonyms, so that local identity is not leaked.
5. As a Codex coding user, I want secrets found in files to be non-restorable by default, so that tokens and passwords are not stored for later restoration.
6. As a Codex coding user, I want unsupported binary, PDF, Office, image and archive files to fail closed, so that unreadable content is not silently treated as safe.
7. As a Codex coding user, I want oversized files to fail closed or require explicit selection, so that the broker does not accidentally send huge raw context.
8. As a Codex coding user, I want direct attachment upload outside Code Sanitizer to be shown as unprotected, so that I understand the bypass.
9. As a Codex coding user, I want sanitized model patches restored locally before writing, so that my real project remains usable.
10. As a Codex coding user, I want to approve restored writes when sensitive values are reintroduced, so that local restoration is deliberate.
11. As a Codex coding user, I want sanitized edits to be validated against the same source version, so that stale or mismatched patches are not applied.
12. As a Codex coding user, I want generated tool output sanitized before model visibility, so that command output cannot leak protected values.
13. As a Codex coding user, I want audit logs to prove file-context protection without raw values, so that I can verify the system worked.
14. As a Codex coding user, I want status to say `project_files_protected` only when the broker is active, so that I do not over-trust composer protection.
15. As a Codex coding user, I want project-file protection to fail closed when the broker is unavailable, so that accidental direct reads do not happen silently.
16. As a security reviewer, I want clear threat boundaries for project files, so that I can tell which channels are covered.
17. As an enterprise admin, I want protected workspace mode to be enforceable by policy, so that sensitive repositories cannot downgrade silently.
18. As a future maintainer, I want the broker to reuse the existing sanitizer, policy, vault, restoration and audit seams, so that project-file protection does not fork the redaction logic.

## Implementation Decisions

- `composer_protected` and `project_files_protected` are separate capability states.
- Composer native submit interception remains the primary Windows desktop prompt protection path, but it is not used as evidence for project-file protection.
- A file-context broker is the required integration layer for protected coding workspaces.
- The broker produces sanitized virtual files: model-visible text representations of local files where sensitive content, filenames and paths have been replaced.
- The original local files are not modified when sanitized virtual files are created.
- Supported text extraction starts narrow: UTF-8 text and source/config formats already accepted by the attachment intake path.
- Unsupported, unreadable, oversized and binary/document formats fail closed until explicit extractors exist.
- File reads, file-derived tool output and write decisions must produce raw-free audit events with source ids, content hashes, entity types, counts, actions and status codes.
- Model-generated edits must be validated against the protected workspace, sanitized source identity and expected target path before local writing.
- Restore-aware local writes restore only restorable pseudonyms from the local mapping vault; secrets remain redacted.
- Enterprise policy may require protected workspace mode and forbid unprotected file-context fallback.
- Direct file upload or connector paths outside the broker remain out of scope unless that adapter proves pre-upload sanitization.

## Testing Decisions

- The highest-value seam is the broker workflow: protected file read -> sanitized virtual file -> sanitized model edit -> restore-aware local write.
- Tests should use synthetic fixture repositories and synthetic sensitive values only.
- Tests should assert externally visible behavior: sanitized virtual file content, decisions, warnings, audit metadata and local write output.
- Tests must prove raw sensitive values do not appear in cloud-visible payload records or audit logs.
- Tests should reuse existing sanitizer, attachment guard, vault, restoration and raw-free audit test patterns.
- Unsupported file tests must prove fail-closed behavior for binary/document types and unreadable paths.
- Status tests must prove that `project_files_protected` is not shown when only composer protection is active.
- Product smoke should simulate a disposable protected workspace before any real Codex Desktop integration attempt.

## Out of Scope

- Claiming end-to-end project-file protection before a verified file-context broker exists.
- Protecting project files read directly by Codex, MCP tools, connectors or shell output that bypasses the broker.
- Full DLP classification for arbitrary document formats.
- OCR, recursive archive scanning, PDF/Office/image extraction and binary parsing in the first slice.
- Sending restored local output back to the cloud automatically.
- Storing or restoring real secrets by default.
- Relying on undocumented private Codex APIs for transparent file-context rewriting.

## Further Notes

The user-facing promise must remain conservative. Code Sanitizer may say that it protects the composer submit path when the selected AI surface and submit binding are verified. It may say that explicit file snippets or text attachments are sanitized only when they are routed through the sanitizer. It may say `project_files_protected` only after the broker can prove coverage for file reads, file-derived tool output and restore-aware writes.
