# Windows Sandbox: test execution handoff

## Default path for UI-sensitive tests

Run tests that can show WinForms/WPF/UIAutomation windows in the already-open
Windows Sandbox, not on the main Windows desktop. This keeps the user's active
desktop usable while the agent can still request and read test runs.

The normal repository remains:

`S:\6. DevSecOps\Codex security`

Завершённый Ticket 355 использовал собственный writable worktree:

`C:\SandboxWorkspaces\CodexRedactionGate-355`

Это исторический путь 355, а не текущий completion gate. Для новых
UI-sensitive задач создавайте отдельный worktree и новый evidence contract.

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

`C:\SandboxProject\scripts\SandboxWorkerBootstrap.ps1`

The bootstrap creates one visible small WinForms supervisor on the Sandbox user
desktop, requests activation, and records whether its opaque HWND is the real
foreground window. Its UI thread owns a durable `Application.Run` message loop;
the constrained worker runs concurrently in a separate runspace of that same
PowerShell OS process. Therefore the worker's `dotnet` child has the supervisor
process as its foreground GUI parent while the supervisor remains responsive.
This is an execution-channel precondition only: fixture foreground and
UIAutomation predicates remain the acceptance authority.

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

A full-project test is receipt-bound, has no filter, and supplies a bounded
foreground deadline in whole seconds (`1..120`):

```json
{
  "Id": "test-unique-id",
  "Kind": "test",
  "ReceiptId": "receipt-unique-id",
  "foreground_wait_seconds": 30
}
```

Before that job is placed in `inbox`, the controller writes exactly one permit
at `.sandbox-jobs\\permits\\<Id>.permit.json`. It binds the job and receipt id
to `SandboxDllPath`, SHA-256, MVID, `WorkerScriptSha256`, and the complete
`SourceFileSha256` map from `.sandbox-jobs\\identity\\<ReceiptId>.json`.
The receipt's DLL path is fixed to the canonical Sandbox artifact
`C:\\SandboxProject\\src\\CodexRedactionGate\\bin\\Debug\\net10.0-windows\\CodexRedactionGate.dll`.

For legacy filtered tests the worker fixes the project to
`src/CodexRedactionGate/CodexRedactionGate.csproj`. A receipt-bound full suite
accepts no project target: after validating permit, receipt, DLL SHA-256, MVID,
and worker-script SHA, it invokes `dotnet test` on the fixed receipt DLL. Any
identity failure is `identity_receipt_mismatch`, occurs before the lease claim,
and launches neither build nor test. The job never supplies a command,
executable, working directory, PowerShell, or Git action.
Use a new safe `Id` for every request. Results are written to:

`C:\SandboxWorkspaces\CodexRedactionGate-355\.sandbox-jobs\results\<Id>.json`

and the complete console output is in the adjacent `<Id>.log`. Exit code `0`
is the success condition. The worker archives leases left by a terminated
Sandbox session into `.sandbox-jobs\interrupted`; this preserves audit data
without blocking a later retry. The `.sandbox-jobs/` directory is ignored by
Git.

`worker.json` and every result additionally record the raw-free fields
`InteractiveHostPresent`, `InteractiveHostForegroundReady`, and
`InteractiveHostAttemptedAtUtc`, plus the monotonic `InteractiveHostAttempt`.
Before every `test` job, the worker requests activation of its existing
supervisor HWND and verifies `GetForegroundWindow == supervisor HWND` within a
bounded interval. Receipt-bound jobs require `foreground_wait_seconds` as an
integer in the closed range `1..120`. Their exact DLL SHA-256/MVID identity
check and atomic interactive lease claim happen only after foreground is
verified, immediately before `dotnet test <receipt DLL>`. A deadline without
foreground returns `Status: blocked`, `Failure: interactive_foreground_timeout`,
and the raw-free projection `executed=0 claim=0`; it performs no identity check,
lease claim, build, restore, or test. The earlier one-shot legacy result
`interactive_foreground_unavailable` remains documented for filtered jobs and
also performs no execution or claim. `restore` jobs remain outside this
foreground gate. The worker does not use input-queue attachment, foreground
permission overrides, keyboard/mouse injection, or synthetic acceptance
evidence.

Start `scripts\CodexTests.wsb` manually from a foreground PowerShell session.
Bootstrap creates or reuses one exact-GUID session id in Sandbox-local
non-mapped storage. Each fresh Sandbox filesystem therefore grants exactly one
UI-sensitive `test` lease, while a repeated Bootstrap in the same live Sandbox
uses the same id. For receipt-bound jobs, all permit and artifact checks finish
before the worker atomically creates the claim immediately before `dotnet`.
The claim is therefore not created on a receipt mismatch. For all admitted jobs,
the worker atomically creates
the mapped `.sandbox-jobs\interactive-leases\<session-id>.json` claim. An
existing claim blocks a test before `dotnet` with
`fresh_interactive_session_required`; pass, failure, and a worker reinitialization
do not restore the lease. `restore` remains ungated and never creates a claim.
`worker.json` and results record only the raw-free session token, lease state,
consumption timestamp, and schema-valid job id.

## Local non-interactive development suite

Use the following local command for ordinary development feedback:

```powershell
.\scripts\Invoke-NonInteractiveSuite.ps1
```

It excludes only NUnit tests marked `interactive-fixture`: those methods
instantiate the reference-composer WinForms fixture, persistent fixture host,
or interactive release runner. The script writes a timestamped TRX result under
`artifacts\non-interactive`, fails when the TRX is absent, and fails when its
reported total is zero. It is development feedback, а не generic
interactive/release evidence. Ticket 355 закрыт отдельным сохранённым local
non-interactive receipt и current-build interactive release-matrix receipt.

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
