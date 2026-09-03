# Windows Sandbox: test execution handoff

## Default path for UI-sensitive tests

Run tests that can show WinForms/WPF/UIAutomation windows in the already-open
Windows Sandbox, not on the main Windows desktop. This keeps the user's active
desktop usable while the agent can still request and read test runs.

The normal repository remains:

`S:\6. DevSecOps\Codex security`

Ticket 355 uses its own writable worktree:

`C:\SandboxWorkspaces\CodexRedactionGate-355`

Do not copy the repository with `robocopy` for routine test runs and do not run
the full UI-sensitive suite on the main desktop.

## Existing host setup

- Versioned configuration template: `scripts\CodexTests.wsb`
- Active Sandbox configuration: `C:\Users\alexey.andreev\Desktop\CodexTests.wsb`
- Project mount: `C:\SandboxProject` (read/write, mapped from the dedicated
  worktree)
- .NET SDK mount: `C:\HostDotnet` (read-only, host SDK 10.0.400)
- Git mount: `C:\HostGit` (read-only)
- Sandbox networking and clipboard redirection are enabled.

The configuration has a `LogonCommand` that starts:

`C:\SandboxProject\scripts\SandboxTestWorker.ps1`

Before opening it, copy the versioned configuration template to the active
Desktop location. The Sandbox is disposable; closing it stops the worker.
After opening it, wait for
`.sandbox-jobs\worker.json` in the dedicated worktree to report `State: ready`.

## Agent-to-Sandbox test protocol

The worker is intentionally a narrow command boundary. It accepts JSON files
in:

`C:\SandboxWorkspaces\CodexRedactionGate-355\.sandbox-jobs\inbox`

It accepts only these shapes:

```json
{ "Id": "restore-unique-id", "Kind": "restore" }
```

```json
{
  "Id": "test-unique-id",
  "Kind": "test",
  "Filter": "FullyQualifiedName~SomeTestClass"
}
```

The worker fixes the project to
`src/CodexRedactionGate/CodexRedactionGate.csproj`; it does not accept a
job-supplied command, executable, working directory, PowerShell, or Git action.
Use a new safe `Id` for every request. Results are written to:

`C:\SandboxWorkspaces\CodexRedactionGate-355\.sandbox-jobs\results\<Id>.json`

and the complete console output is in the adjacent `<Id>.log`. Exit code `0`
is the success condition. The worker archives leases left by a terminated
Sandbox session into `.sandbox-jobs\interrupted`; this preserves audit data
without blocking a later retry. The `.sandbox-jobs/` directory is ignored by
Git.

## Network and NuGet

Inside Sandbox, `127.0.0.1` refers to Sandbox itself, not to the host. NuGet
must use the host proxy exposed through the Sandbox default gateway. The worker
discovers that gateway and sets, for its `dotnet` child process only:

```text
HTTP_PROXY=http://<Sandbox-default-gateway>:10808
HTTPS_PROXY=http://<Sandbox-default-gateway>:10808
ALL_PROXY=http://<Sandbox-default-gateway>:10808
NO_PROXY=localhost,127.0.0.1
```

The host proxy must be listening on port `10808` for the Sandbox virtual
network, with access restricted to the Sandbox subnet. Do not configure a
proxy URL with credentials in job files or logs.

## Reproducible host setup

Create the dedicated worktree from the current repository baseline, deploy the
versioned WSB file to the Desktop, then start the Sandbox. Do not repoint a
live configuration at a dirty worktree. Each ticket gets a separate worktree
and an explicit code snapshot before tests are queued.

The worker and bootstrap are versioned at:

- `scripts\SandboxTestWorker.ps1`
- `scripts\SandboxWorkerBootstrap.ps1`
- `scripts\SandboxWorkerBootstrap.cmd`

The bootstrap log is `.sandbox-jobs\worker-startup.log`; it contains only
timestamps and error types/messages. It is diagnostic evidence, never a test
acceptance result.

## Verified execution-channel evidence

On 2026-09-02 the worker completed:

- `restore`: exit code `0` using `http://172.26.176.1:10808`.
- `test` with `FullyQualifiedName~ProtectedComposerSessionTests`: exit code
  `0`; 35 passed, 0 failed, 0 skipped.

These runs prove the execution channel, not the entire project suite. Request
the needed targeted or full test job and inspect its own result before making a
ticket claim.

## Scope boundary

Do not mount the host ChatGPT/Codex application profile or its authenticated
state into Sandbox. Deterministic project tests do not need it. A real
installed-application acceptance run requires a separate isolated Windows user
or a persistent VM with a dedicated test account.
