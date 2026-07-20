# Codex Redaction Gate

Codex Redaction Gate is a Windows-first local privacy guard for Codex and ChatGPT Desktop workflows. It detects sensitive data before cloud submission, replaces local identifiers with stable pseudonyms, keeps the mapping vault on the user's machine, and provides local restoration for sanitized responses.

The current product slice focuses on the Windows Codex/ChatGPT desktop composer. Browser, Chrome, PWA, whole-window capture, and unsupported surfaces fail closed by design.

## What It Protects

- Internal URLs, domains, private IPs, CIDR ranges, emails, paths, usernames, customer/product/project names, and technical identifiers.
- Secrets such as passwords, tokens, private keys, connection strings, and API keys. Secrets are non-restorable by default and become `SECRET_REDACTED`.
- Large pasted text and plain-text attachment snippets that pass through the sanitizer pipeline.

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
- .NET 10 SDK.
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

Start the tray app:

```powershell
dotnet run --project .\src\CodexRedactionGate\CodexRedactionGate.csproj -- --tray-app
```

## Common CLI Commands

Sanitize text:

```powershell
dotnet run --project .\src\CodexRedactionGate\CodexRedactionGate.csproj -- --sanitize "Connect to 192.168.10.25"
```

Manage local sensitive dictionary entries:

```powershell
dotnet run --project .\src\CodexRedactionGate\CodexRedactionGate.csproj -- --dictionary-add domain corp.example.local
dotnet run --project .\src\CodexRedactionGate\CodexRedactionGate.csproj -- --dictionary-list
dotnet run --project .\src\CodexRedactionGate\CodexRedactionGate.csproj -- --dictionary-remove <entry-id>
```

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

Configure hotkey and optional autostart:

```powershell
dotnet run --project .\src\CodexRedactionGate\CodexRedactionGate.csproj -- --hotkey-show
dotnet run --project .\src\CodexRedactionGate\CodexRedactionGate.csproj -- --hotkey-set "Ctrl+Shift+F9"
dotnet run --project .\src\CodexRedactionGate\CodexRedactionGate.csproj -- --autostart-enable
dotnet run --project .\src\CodexRedactionGate\CodexRedactionGate.csproj -- --autostart-disable
```

## Windows Desktop Flow

Supported v1 targets are Codex Desktop and ChatGPT Desktop on Windows.

Recommended validation sequence:

```powershell
dotnet run --project .\src\CodexRedactionGate\CodexRedactionGate.csproj -- --os-compatibility-matrix
dotnet run --project .\src\CodexRedactionGate\CodexRedactionGate.csproj -- --os-composer-diagnostic-delay 5
dotnet run --project .\src\CodexRedactionGate\CodexRedactionGate.csproj -- --os-demo-hotkey
dotnet run --project .\src\CodexRedactionGate\CodexRedactionGate.csproj -- --os-demo-hotkey-apply
```

`--os-demo-hotkey-send` is an advanced mode and is disabled by default. It requires explicit local enablement with `--send-mode-enable` after supported Codex/ChatGPT desktop apply-only evidence exists.

## Install

Publish a user-scope build:

```powershell
.\scripts\build-release.ps1
```

Install it for the current user:

```powershell
.\scripts\install-user.ps1
```

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
