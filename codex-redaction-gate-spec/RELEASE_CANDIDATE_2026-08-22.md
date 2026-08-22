# Release Candidate 2026-08-22

## Build identity

- Source commit: `3622ef22` (`Fix resident workflow review findings`)
- Installer: `artifacts/installer/CodexRedactionGateSetup-0.1.20260822.t1325.exe`
- Installer version: `0.1.20260822.t1325`
- Tray and console version: `0.1.20260822.t1325`
- Installer SHA-256: `4BA41A754BD9AD446A723DEEDA16BB9AACF963E36D8BE38D45F75AE69DB9986C`
- Scanner package: intentionally absent; smoke reports `safe_disabled_missing`

The installer is generated from the same source commit as the release gates.
The binary is under `artifacts/`, which is ignored by Git; retain this record
as the reproducible local handoff for manual acceptance.

## Automated evidence

- Full suite: `1759/1759` passed.
- `--self-test`: passed.
- `--product-smoke`: passed.
- Reference-composer matrix: both runs passed every scenario, raw-free checks
  and cleanup.
- Project-file protection: remains unsupported; `project_files_protected` must
  remain `false`.

## Manual acceptance checklist

1. Stop any currently running resident instance only through its confirmed Exit
   action, or answer the installer confirmation so it can stop the matching
   installed processes.
2. Run the installer and leave `Launch Codex Redaction Gate` enabled.
3. Open the tray menu and verify the version is `0.1.20260822.t1325`.
4. Open protection status. Confirm local DPAPI is ready and prompt protection
   is not reported as protected until the selected desktop composer completes
   setup.
5. Complete the automatic setup flow for the one selected Codex/ChatGPT Desktop
   composer. Verify the saved Send binding is the same binding used in that
   composer.
6. Add a harmless test term such as `test.secret.com`, enter it in the selected
   composer and send with the configured keyboard binding. The replacement
   window must be active; accepting it must send only sanitized text.
7. Repeat with Cancel. Correct the prompt and send again. A canceled attempt
   must not allow a later sensitive prompt to bypass interception.
8. Type and press Enter in an unrelated application. Ordinary input must remain
   unaffected.
9. Confirm the status UI still says project-file protection is unsupported/not
   configured. Do not use the demonstrator broker as proof of client file
   protection.

## Installer-matched release proof

After installation, run the installed console executable, not the source build:

```powershell
$app = "$env:LOCALAPPDATA\Programs\CodexRedactionGate\CodexRedactionGate.exe"
& $app --reference-composer-release-acceptance
```

The scenario matrix may report all scenarios passed, but release acceptance is
complete only when it also reports:

```text
reference_proof_recorded: true
```

Until that line is recorded for this installer, ticket 348 remains acceptance
pending even though its source implementation and deterministic tests pass.
