# Codex Redaction Gate

Codex Redaction Gate is a Windows-first local privacy guard for Codex and ChatGPT Desktop workflows. It detects sensitive data before cloud submission, replaces local identifiers with stable pseudonyms, keeps the mapping vault on the user's machine, and provides local restoration for sanitized responses.

The current product slice focuses on the Windows Codex/ChatGPT desktop composer. The target product workflow is native submit interception for selected AI apps: the user presses the AI app's normal Send shortcut, Code Sanitizer intercepts it before cloud submission, and only safe or approved sanitized text is sent. Hotkeys remain secondary diagnostics/manual features. Browser, Chrome, PWA, whole-window capture, and unsupported surfaces fail closed by design.

## What It Protects

- Internal URLs, domains, private IPs, CIDR ranges, emails, paths, usernames, customer/product/project names, and technical identifiers.
- Secrets such as passwords, tokens, private keys, connection strings, and API keys. Secrets are non-restorable by default and become `SECRET_REDACTED`.
- Large pasted text and plain-text attachment snippets that pass through the sanitizer pipeline.

The current product does not yet provide end-to-end protection for arbitrary project files read by a coding agent. It can sanitize explicit file snippets or plain-text attachments only when they are routed through Code Sanitizer before cloud submission. Full project-file protection needs a file-context broker; see `codex-redaction-gate-spec/PROJECT_FILE_WORKFLOW_GRILL_REVIEW.md` and `codex-redaction-gate-spec/adr/ADR-005-project-file-context-requires-a-restore-aware-broker.md`.

The product does not claim to remove data that was already sent to a cloud service, and it does not protect users who intentionally bypass the gate.

## Repository Layout

```text
src/CodexRedactionGate/          Main CLI, sanitizer, Windows UIA adapter, tray app, tests
src/CodexRedactionGate.Tray/     WinExe tray entrypoint
scripts/                         Release, install, uninstall, and installer build helpers
packaging/windows/               Inno Setup manifest
codex-redaction-gate-spec/       Product specs, architecture, threat model, ADRs, and spike notes
tickets.md                       Local implementation tracker
```

## Requirements

- Windows.
- .NET 10 SDK for building from source.
- .NET 10 Desktop Runtime or SDK on the target machine. Release builds are currently framework-dependent and do not download runtimes.
- Optional: Inno Setup 6 if you want to build the `.exe` installer.
- Optional: a packaged Gitleaks scanner under `artifacts/scanners/gitleaks/` for release builds:
  - `gitleaks.exe`
  - `gitleaks-provenance.json`

If the scanner package is missing, release builds and runtime readiness report a raw-free safe-disabled scanner state instead of downloading anything at runtime.

## Quick Start

Build and test:

```powershell
dotnet build .\src\CodexRedactionGate\CodexRedactionGate.csproj -nologo -p:UseAppHost=false
dotnet test .\src\CodexRedactionGate\CodexRedactionGate.csproj -nologo -p:UseAppHost=false
dotnet run --project .\src\CodexRedactionGate\CodexRedactionGate.csproj -- --self-test
```

Run the product smoke:

```powershell
dotnet run --project .\src\CodexRedactionGate\CodexRedactionGate.csproj -- --product-smoke
```

Build the installable release:

```powershell
.\scripts\build-release.ps1
```

Install and launch the resident tray app for the current user:

```powershell
.\scripts\install-user.ps1
```

The normal app entrypoint is `CodexRedactionGate.Tray.exe`. `CodexRedactionGate.exe` remains the CLI and diagnostics companion. You should not need `dotnet run` for normal protected operation after installation.

## Common CLI Commands

Sanitize text:

```powershell
dotnet run --project .\src\CodexRedactionGate\CodexRedactionGate.csproj -- --sanitize "Connect to 192.168.10.25"
```

Manage local sensitive dictionary entries:

```powershell
.\CodexRedactionGate.exe --dictionary-ui
dotnet run --project .\src\CodexRedactionGate\CodexRedactionGate.csproj -- --dictionary-add domain corp.example.local
dotnet run --project .\src\CodexRedactionGate\CodexRedactionGate.csproj -- --dictionary-list
dotnet run --project .\src\CodexRedactionGate\CodexRedactionGate.csproj -- --dictionary-remove <entry-id>
```

Installed users can open the same editor from the tray menu: `Open sensitive terms`.

Restore local placeholders from model responses:

```powershell
.\CodexRedactionGate.exe --restore-text "DOMAIN_C195C3D8E8F3"
```

Installed users can open the same restore window from the tray menu: `Open local restore`. Restored output is local-sensitive; sanitize it again before sending it to any cloud app.

Manage policy rules:

```powershell
dotnet run --project .\src\CodexRedactionGate\CodexRedactionGate.csproj -- --policy-add-url-prefix https://internal.example.local/
dotnet run --project .\src\CodexRedactionGate\CodexRedactionGate.csproj -- --policy-add-regex username "C:\\Users\\[^\\>]+"
dotnet run --project .\src\CodexRedactionGate\CodexRedactionGate.csproj -- --policy-diagnostics
```

Inspect readiness and audit:

```powershell
dotnet run --project .\src\CodexRedactionGate\CodexRedactionGate.csproj -- --doctor
dotnet run --project .\src\CodexRedactionGate\CodexRedactionGate.csproj -- --audit-view
dotnet run --project .\src\CodexRedactionGate\CodexRedactionGate.csproj -- --audit-verify
```

Sanitize a project file through the local broker demo:

```powershell
dotnet run --project .\src\CodexRedactionGate\CodexRedactionGate.csproj -- --project-file-sanitize .\src\example.cs
dotnet run --project .\src\CodexRedactionGate\CodexRedactionGate.csproj -- --project-workspace-protect .
dotnet run --project .\src\CodexRedactionGate\CodexRedactionGate.csproj -- --project-file-sanitize .\src\example.cs --protected-workspace .
```

This broker demo proves local sanitized virtual file generation. It still reports `project_files_protected: false` for live Codex because end-to-end Codex project-file interception starts in the next tickets.

Configure the secondary hotkey and optional autostart:

```powershell
dotnet run --project .\src\CodexRedactionGate\CodexRedactionGate.csproj -- --hotkey-show
dotnet run --project .\src\CodexRedactionGate\CodexRedactionGate.csproj -- --hotkey-set "Ctrl+Shift+F9"
dotnet run --project .\src\CodexRedactionGate\CodexRedactionGate.csproj -- --autostart-enable
dotnet run --project .\src\CodexRedactionGate\CodexRedactionGate.csproj -- --autostart-disable
```

The hotkey above is only a secondary `manual scan/apply` feature. It is not proof that native submit interception is protecting the selected AI app.

Protected Send currently covers the verified keyboard shortcut and mouse activation of the selected Send control. A third-party programmatic Windows UI Automation `Invoke()` cannot be cancelled before provider notification, so it is reported as `programmatic_uia_invoke_unsupported`, not as a protected submission path.

The installed tray app normally enforces one resident instance for the current Windows user. For an elevated, multi-user deployment only, start it with `--tray-app --global`; this uses a `Global\` mutex and does not grant cross-session UI control.

Second-launch notifications can be configured under `HKCU\Software\CodexRedactionGate\SingleInstance`: set `DisableNotification` to a non-zero DWORD to suppress them, or set `NotificationType` to `balloon`, `toast`, or `none`. The default is `balloon`; `toast` uses the native balloon fallback in this Windows Forms release. A legacy `messagebox` value is treated as `balloon`, so a second launch never requires a modal acknowledgement.

### Second Tray Launches

When the tray executable is started again for the same Windows user, the new process exits after asking the resident instance to restore its activation window. The activation handle is stored only in that user's HKCU registry hive and is validated before use. Windows may reject foreground activation, and a `--tray-app --global` mutex does not permit cross-session UI control. In either case, the second process shows one short non-modal, localized notification that directs the user to the resident tray icon for local diagnostics. A missing or stale handle is cleared; it never changes protection ownership or sends any prompt data.

## Windows Desktop Flow

Supported v1 targets are Codex Desktop and ChatGPT Desktop on Windows.

The production direction is selected-AI native submit interception, not hotkey-first operation. A surface is `protected` only when the selected AI app, focused composer and active submit binding are verified. If submit binding discovery is unavailable, the app must show `binding_unknown` or `degraded_hotkey_only` instead of claiming protection.

Recommended local validation sequence before trying a real Codex/ChatGPT prompt:

```powershell
dotnet run --project .\src\CodexRedactionGate\CodexRedactionGate.csproj -- --os-compatibility-matrix
dotnet run --project .\src\CodexRedactionGate\CodexRedactionGate.csproj -- --os-composer-diagnostic-delay 5
dotnet run --project .\src\CodexRedactionGate\CodexRedactionGate.csproj -- --native-profile-verify codex-desktop Enter Ctrl+Enter
dotnet run --project .\src\CodexRedactionGate\CodexRedactionGate.csproj -- --native-profiles-status
```

If your Codex/ChatGPT Desktop sends with a different shortcut, replace `Enter` with the real Send binding and keep the newline binding as the shortcut that inserts a new line. For example, when `Enter` sends and `Ctrl+Enter` inserts a newline, Code Sanitizer must intercept `Enter` only in the verified composer and pass `Ctrl+Enter` through.

Before sending a sensitive test prompt, `--native-profiles-status` should show a selected profile with:

```text
readiness=protected
protected_send_binding=Enter
newline_binding=Ctrl+Enter
```

If the status is `not_configured`, `binding_unknown`, `surface_unverified`, or `degraded_hotkey_only`, do not treat the AI app as protected yet.

`--os-demo-hotkey`, `--os-demo-hotkey-apply`, and `--os-demo-hotkey-send` are legacy/demo diagnostics. They are not the normal product flow.

## Install

Publish a user-scope build:

```powershell
.\scripts\build-release.ps1
```

Install it for the current user and launch the tray app:

```powershell
.\scripts\install-user.ps1
```

If Code Sanitizer is already running, the install script asks before stopping it because selected AI apps are unprotected during the update. For unattended local updates, pass the explicit confirmation flag:

```powershell
.\scripts\install-user.ps1 -StopRunning
```

Install without launching:

```powershell
.\scripts\install-user.ps1 -NoLaunch
```

Install and enable user-scope autostart:

```powershell
.\scripts\install-user.ps1 -EnableAutostart
```

Installed files go to:

```text
%LOCALAPPDATA%\Programs\CodexRedactionGate
```

Start Menu shortcuts are created for:

- `Codex Redaction Gate` - launches `CodexRedactionGate.Tray.exe`.
- `Diagnostics` - runs CLI diagnostics.
- `Audit viewer` - opens the raw-free audit viewer.

Stopping protection, exiting the tray app, or unloading the resident process requires explicit confirmation. Canceling the confirmation keeps protection running. Managed policy may block unload entirely.

Uninstall but keep local sensitive data:

```powershell
.\scripts\uninstall-user.ps1
```

Delete local sensitive data only with the explicit flag:

```powershell
.\scripts\uninstall-user.ps1 -DeleteLocalData
```

Build an Inno Setup installer when `ISCC.exe` is available:

```powershell
.\scripts\build-installer.ps1
```

The installer is an Inno Setup executable named like:

```text
artifacts\installer\CodexRedactionGateSetup-0.1.20260721.t1530.exe
```

You can set the version explicitly:

```powershell
.\scripts\build-installer.ps1 -BuildVersion "0.1.20260721.t1530"
```

The Inno installer launches `CodexRedactionGate.Tray.exe` after setup and can register the same tray executable in HKCU autostart. During upgrades, if Code Sanitizer is already running, setup shows an explicit warning that resident protection must stop temporarily, then stops `CodexRedactionGate.Tray.exe` before replacing files and starts it again after setup. Release builds are self-contained for `win-x64`, so the installed application does not require `dotnet` to be available in `PATH`.

## Local Data

Default local data root:

```text
%LOCALAPPDATA%\CodexRedactionGate
```

Important subdirectories:

- `policy/` - managed dictionary and active policy.
- `vault/` - local pseudonym mapping vault.
- `audit/` - raw-free tamper-evident audit events.
- `settings/` - hotkey and send-mode settings.

Uninstall keeps this data by default. Cleanup requires explicit user action.

## Documentation

Start with:

- `codex-redaction-gate-spec/README.md`
- `codex-redaction-gate-spec/SPEC.md`
- `codex-redaction-gate-spec/ARCHITECTURE.md`
- `codex-redaction-gate-spec/SANITIZER_DESIGN.md`
- `codex-redaction-gate-spec/THREAT_MODEL.md`

## Security Notes

- Raw prompts, raw entity values, restored values, screenshots, and full window text must not be written to audit logs.
- Mapping vault, policy files, and dictionaries are local sensitive artifacts.
- Scanner/runtime failures must fail closed or safe-disable with explicit raw-free diagnostics.
- Browser/PWA support is out of scope for v1.
