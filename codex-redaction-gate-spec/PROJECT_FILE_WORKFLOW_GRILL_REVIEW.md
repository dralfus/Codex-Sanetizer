# Project File Workflow Grill Review

## Verdict

Code Sanitizer does not currently provide end-to-end protection for arbitrary project files used by a Codex coding workflow.

The implemented sanitizer can process explicit `file_snippet` and plain-text attachment content parts when an adapter passes those parts through the sanitizer API. The Windows desktop native submit interceptor protects the selected AI app composer submit path. It does not own Codex workspace file reads, model-visible tool output, attachment upload paths, or patch writes.

Therefore the current honest status is:

```text
composer_protected: supported for verified Windows Codex/ChatGPT Desktop surfaces
plain_text_attachment_snippets: supported when routed through sanitizer intake
project_files_protected: not implemented
```

## Grilled Questions

### Where Is the Cloud Boundary?

For typed prompts, the boundary is the selected AI app submit action.

For project files, the boundary is any moment when local file content, file-derived tool output, filenames, paths, diffs, or attachment bytes become model context. That can happen before the user presses Send if the coding runtime preloads context, and it can happen outside the Windows UI interception layer.

### What Would Prove It Works?

The product needs raw-free evidence that every outgoing file-context payload for a protected workspace was produced by Code Sanitizer.

Minimum proof:

- a protected fixture repository contains a fake internal domain, username, path and synthetic secret;
- Codex requests a file read through the local file-context broker;
- the broker audit records source ids, content hashes, entity types, counts and replacement actions without raw values;
- the model-visible virtual file contains only placeholders;
- unsupported files are blocked before upload;
- returned sanitized edits are restored only by the local writer;
- no restored output is sent back to the cloud automatically.

### What Are the Bypass Paths?

- Codex reads repository files directly without a Code Sanitizer broker.
- The user uploads a file through the AI app attachment UI.
- An MCP/filesystem connector sends tool output directly to the model.
- A shell command prints raw file contents and that output is sent as context.
- The model returns placeholders, and the user manually applies them to local files without local restoration.
- Restored local output is copied back into a later prompt.

### What Is the Product Shape?

Project-file protection should be a separate protected workspace mode, not an extension of the keyboard hook.

Target flow:

```text
file read -> sanitized virtual file -> model context -> sanitized edit -> restore-aware local write
```

The local broker must own file reads and writes. The existing sanitizer engine, policy, mapping vault, non-restorable secret redaction, attachment guard and audit chain can be reused, but the current desktop composer adapter is not enough.

## Acceptance Bar

Code Sanitizer may claim `project_files_protected` only when all of these are true:

- all supported text file reads for the protected workspace pass through the broker;
- unsupported, unreadable, oversized, binary, PDF, Office, image and archive inputs fail closed or require explicit local conversion;
- filenames and paths are scanned and pseudonymized when policy requires it;
- shell/tool output derived from protected files is sanitized before model visibility;
- model-visible payload records are raw-free and auditable;
- returned edits are validated against the sanitized source version before local write;
- local write restoration is explicit, restore-aware and keeps secrets non-restorable;
- UI/status distinguishes protected composer submit from protected project files.

## Current Test You Can Run

Today you can test only the sanitizer engine/file-snippet capability, not full Codex project-file protection:

```powershell
dotnet run --project .\src\CodexRedactionGate\CodexRedactionGate.csproj -- --sanitize "Read C:\Users\user1\repo and call test.secret.com"
```

That proves the text sanitizer and mapping vault path. It does not prove that Codex project file reads are intercepted.

## Open Work

- Discover or add a Codex/tool extension point that can force all protected workspace file reads through Code Sanitizer.
- Build the file-context broker and sanitized virtual file model.
- Add restore-aware patch/write handling.
- Add protected workspace status and fail-closed behavior.
- Add a raw-free product smoke for file read, model-visible virtual file, sanitized patch and local restoration.
