# OS Adapter UX Demo Checklist

## Scope

This checklist is for the Windows Codex/ChatGPT desktop app UX demo. The safe live path is composer-specific: a matching window title is not enough, and the adapter must verify the focused composer before reading or writing text.

## Automated Preview

The raw-free demo seam can still be exercised without a live desktop app:

```powershell
dotnet run --project src\CodexRedactionGate\CodexRedactionGate.csproj -- --os-profiles-list
dotnet run --project src\CodexRedactionGate\CodexRedactionGate.csproj -- --os-compatibility-matrix
dotnet run --project src\CodexRedactionGate\CodexRedactionGate.csproj -- --os-surface-diagnostic
dotnet run --project src\CodexRedactionGate\CodexRedactionGate.csproj -- --os-composer-diagnostic
dotnet run --project src\CodexRedactionGate\CodexRedactionGate.csproj -- --os-composer-diagnostic-delay 5
Use `Set up prompt protection` from the installed tray UI, select the real Send-key pair, and focus the target composer when the setup window hides.
dotnet run --project src\CodexRedactionGate\CodexRedactionGate.csproj -- --os-demo-dry-run "Connect to 192.168.10.25"
dotnet run --project src\CodexRedactionGate\CodexRedactionGate.csproj -- --os-demo-smoke
dotnet run --project src\CodexRedactionGate\CodexRedactionGate.csproj -- --os-demo-send-gate
```

Expected behavior:

- profile listing shows `codex-desktop`, `chatgpt-desktop` and `redaction-gate-demo`;
- compatibility matrix says v1 supports only Windows Codex/ChatGPT desktop focused composers and names `read_only_diagnostic`, `dry_run` and `apply_only` evidence;
- surface diagnostic prints raw-free foreground-window status and capability metadata;
- composer diagnostic prints `supported_composer` when the focused element is a writable composer or a focused Electron/Chromium composer exposing UI Automation `TextPattern`, including Chrome/XAML `ControlType.Group` composer surfaces;
- delayed composer diagnostic lets the user start the command, switch focus to Codex/ChatGPT, and then capture the actual composer instead of the terminal window;
- delayed native profile verification lets the user start onboarding from their desktop session, focus the target Codex/ChatGPT composer, and save a protected profile only when that real composer verifies;
- dry-run prints a local confirmation preview with highlighted sanitized placeholders;
- smoke reports dry-run, apply-only, opt-in send, cancel, block and write-failure paths;
- send gate is `safety_disabled` until supported Codex/ChatGPT apply-only evidence exists and local send mode is explicitly enabled with `--send-mode-enable`.

## Windows Desktop Compatibility Matrix

| Profile | App | Channel | Required evidence | Supported scope | Status |
| --- | --- | --- | --- | --- | --- |
| `codex-desktop` | Codex | Windows desktop | read-only diagnostic, dry-run, apply-only | focused composer only | manual verification required |
| `chatgpt-desktop` | ChatGPT | Windows desktop | read-only diagnostic, dry-run, apply-only | focused composer only | manual verification required |

Unsupported v1 scope: browser, Chrome, PWA and whole-window capture. These surfaces must fail closed with `unsupported_surface`; do not treat a matching page title as sufficient evidence.

## Unsafe Legacy Path

The old foreground-window keyboard/clipboard fallback is not safe for live Codex/ChatGPT testing. A dry-run must not send `Ctrl+A`, `Ctrl+C`, `Ctrl+V` or `Enter` to a real Codex/ChatGPT window. If composer-specific capture is unavailable, the live demo must fail closed. Apply-only may use verified keyboard paste only after the adapter has proved the focused element is the composer and the user has confirmed the sanitized prompt.

## Disposable Local Demo Target

Use the disposable target before touching a real Codex/ChatGPT task:

```powershell
dotnet run --project src\CodexRedactionGate\CodexRedactionGate.csproj -- --os-demo-local-target
dotnet run --project src\CodexRedactionGate\CodexRedactionGate.csproj -- --os-demo-hotkey
dotnet run --project src\CodexRedactionGate\CodexRedactionGate.csproj -- --os-demo-hotkey-apply
```

Manual steps:

1. Start `--os-demo-local-target`.
2. Start `--os-demo-hotkey` in another console.
3. Focus the demo composer and trigger Ctrl+Enter.
4. Verify dry-run shows the overlay/status and the composer text is unchanged.
5. Stop dry-run, start `--os-demo-hotkey-apply`, repeat the hotkey and confirm.
6. Verify the demo composer contains only `sanitized_text` and nothing is submitted.
7. Verify cancel and block leave the demo composer unchanged.

## Codex/ChatGPT Apply-Only Demo

Try a real Codex/ChatGPT app only after the disposable target works:

1. Create or open a throwaway Codex/ChatGPT task, not a real development task.
2. Focus the normal composer.
3. Run `--os-composer-diagnostic-delay 5` from the terminal, immediately switch back to the Codex/ChatGPT composer, and wait for the diagnostic to finish.
4. Continue only if it reports `status: supported_composer`.
5. Start `Set up prompt protection` from the tray UI, select the real Send-key pair, then focus the same composer before verification completes.
6. Continue only if `--native-profiles-status` reports the selected profile as `protected`.
7. Start `--os-demo-hotkey`.
8. Trigger Ctrl+Enter and verify dry-run does not modify selection, clipboard, focus or composer text.
9. Stop dry-run, start `--os-demo-hotkey-apply`.
10. Trigger Ctrl+Enter, confirm, and verify the composer contains exactly `sanitized_text`. For Electron composers without writable `ValuePattern`, this write-back can use verified keyboard paste after confirmation.
11. Verify cancel, block, focus loss, stale element, write failure and verification mismatch leave the app unsubmitted.
12. Repeat the same protected Send flow at least three times in the same task and verify the replacement overlay appears every time sensitive text is detected.
13. Verify each replacement overlay becomes the active foreground window; if Windows refuses focus activation, verify Code Sanitizer shows a visible raw-free status and sends nothing raw.

Record app/channel/version evidence without prompt text:

| Date | Profile | App version/channel | Diagnostic status | Dry-run result | Apply-only result | Notes |
| --- | --- | --- | --- | --- | --- | --- |
| YYYY-MM-DD | `codex-desktop` | Windows desktop / version noted locally | `supported_composer` | pass/fail | pass/fail | raw-free notes only |
| YYYY-MM-DD | `chatgpt-desktop` | Windows desktop / version noted locally | `supported_composer` | pass/fail | pass/fail | raw-free notes only |

## Confirm-And-Send Gate

`--os-demo-hotkey-send` is intentionally disabled by default. It should print a raw-free disabled status until both conditions are true:

- apply-only evidence exists locally from a successful Codex/ChatGPT desktop apply-only demo;
- local send mode is enabled explicitly with `--send-mode-enable`.

Use `--send-mode-show` to inspect the gate and `--send-mode-disable` to turn the setting off again. Do not enable live send from a real development task. Use a throwaway task only, and only after apply-only evidence is present.

## Fail-Closed Checks

- Unsupported active window must report `unsupported_surface`.
- Focused non-composer elements must report `not_composer`.
- Unreadable composer must report `capture_failed` or `not_composer`.
- Write-back failure must not submit.
- Verification mismatch after write-back must not submit.
- Block decisions must not modify or submit.
- Repeated protected Send attempts must not become one-shot; each matching Send must run the sanitizer/confirmation path again.
- Replacement confirmation overlay must request active foreground display when shown.
- Diagnostics and audit must not include raw prompt text, screenshots or full window contents.

## Future Adapter Notes

Linux desktop support should reuse the same interaction contracts with a Linux-specific surface discovery and text I/O adapter. CLI support should be wrapper mode, such as `safe-codex` or `safe-claude`, not terminal keystroke interception.
