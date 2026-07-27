---

# Tickets: Codex Redaction Gate - SurfaceMetadata and Native Submit Improvements

All tickets 238-250 completed. Frontier now at tickets 251-255.

Work the **frontier**: any ticket whose blockers are all done. For a purely linear chain that means top to bottom.

## 251. Make selected-profile setup and binding changes fail closed in the resident hook

**What to build:** Make first-run setup and Send-binding changes transactional for every explicitly selected AI profile. While setup or a binding change is incomplete, the resident hook must guard the selected app's configured Send path; after verification succeeds it must atomically reload the new profile and binding into the live hook without requiring a tray restart.

**Blocked by:** 250. Restore verified composer identity after SurfaceMetadata migration.

**Do not:** Pass through because the startup/default profile is disabled, treat one protected profile as setup completion for a different selected unprotected profile, retain `protected` while a new pair is awaiting verification, silently default to `Enter`, or swallow setup failures without a visible raw-free fail-closed status.

- [ ] A selected but unconfigured profile suppresses its matching Send gesture with `setup_required`; unrelated apps and non-Send input continue to pass through.
- [ ] Setup completion is evaluated for the selected profile set, not merely because any profile is protected.
- [ ] Selecting a new Send/newline pair immediately invalidates the old protected profile; only the successfully verified pair becomes protected.
- [ ] The tray replaces/restarts the live native controller and hook with the newly verified profile, and tray status reports the same active pair.
- [ ] Cancellation, timeout, storage failure, and unexpected setup exception leave the app fail-closed with raw-free diagnostics.
- [ ] Tests cover empty store, two selected profiles, binding change from protected state, resident reload, setup cancel/failure, and no raw submission.

## 252. Wire per-user single-instance enforcement into the installed tray entry point

**What to build:** Use the existing single-instance boundary at the actual tray executable entry point so a second launch cannot register a competing native keyboard hook or create a second tray icon. The second launch must activate the existing resident UI when possible and otherwise exit with a raw-free status.

**Blocked by:** None — can start immediately.

**Do not:** Leave the mutex as test-only code, kill the existing protection process without explicit user confirmation, create a second hook as a fallback, or rely on installer shutdown behavior as normal runtime single-instance protection.

- [x] Starting `CodexRedactionGate.Tray.exe` twice leaves one hook-owning process and one tray icon.
- [x] The second launch signals/foregrounds the existing resident instance or exits cleanly with a raw-free result.
- [x] The mutex lifetime covers the actual tray message loop and is released safely on normal exit, startup failure, and abandoned-instance recovery.
- [x] Installer upgrade and explicit Exit remain compatible with the single-instance boundary.
- [x] Tests exercise the production entry-point integration, not only the helper class.

## 253. Connect selected-app Send controls to native interception without blocking other controls

**What to build:** Integrate production UI-control identification and Send-button activation with native interception. CS must guard the identifiable Send button for a selected protected AI profile, including mouse/UI Automation activation, while skill pickers and other non-Send controls retain normal keyboard behavior.

**Blocked by:** 251. Make selected-profile setup and binding changes fail closed in the resident hook.

**Do not:** Classify every foreground window as a composer or Send control, suppress ordinary `Enter` in a skill picker, leave mouse Send unguarded, or permit a raw fallback when control identity is unavailable.

- [ ] Focused-control discovery distinguishes verified composer, identifiable selected-app Send control, and non-Send controls using raw-free UI Automation evidence.
- [ ] Keyboard activation of an identifiable Send control and mouse/UI Automation Send activation enter the same suppress-first protected flow.
- [ ] Non-Send controls and the configured newline shortcut pass through unchanged.
- [ ] Unknown control identity on a selected protected Send path fails closed without exposing prompt/window/control text.
- [ ] Tests cover composer, skill picker, Send button, mouse activation, selected versus unselected apps, and overlay-originated replay.

## 254. Make crash and failure diagnostics structurally raw-free

**What to build:** Replace all persistence and outward diagnostics of arbitrary exception messages/stack traces with one local raw-free crash-report boundary. It must retain only an allowlisted component, exception type/category, build version, timestamp, and safe status code while preserving fail-closed behavior.

**Blocked by:** None — can start immediately.

**Do not:** Serialize or print `Exception.Message`, `StackTrace`, raw paths, window titles, prompt text, configuration contents, or scanner findings; duplicate crash-report schemas across sanitizer, readiness, and native-submit paths; or turn a diagnostic write failure into a send path.

- [ ] All crash reports use one schema and one writer with allowlisted raw-free fields only.
- [ ] Orchestrator, native-submit, sanitizer, and DPAPI/readiness failures return raw-free diagnostics without exception text.
- [ ] Tray/CLI crash viewing shows only the safe summary.
- [ ] Tests inject exceptions containing synthetic prompt, path, and window-title values and prove none reach reports, status, audit, or CLI output.

## 255. Make release smoke exercise the real protected-send invariants and remove committed test run artifacts

**What to build:** Replace smoke placeholders with executable assertions for setup enforcement, binding transitions, composer identity, single-instance startup, and selected Send-control handling. Remove committed ad-hoc test output files and prevent them from returning; clear nullable warnings introduced by the reviewed code so a clean build is a meaningful release signal.

**Blocked by:** 250. Restore verified composer identity after SurfaceMetadata migration; 251. Make selected-profile setup and binding changes fail closed in the resident hook; 252. Wire per-user single-instance enforcement into the installed tray entry point; 253. Connect selected-app Send controls to native interception without blocking other controls; 254. Make crash and failure diagnostics structurally raw-free.

**Do not:** Set smoke statuses to constants, treat a passing unit-test suite as proof of the resident hook, retain generated console logs as source artifacts, hide warnings, or add raw-sensitive fixtures to release output.

- [ ] Product smoke executes and fails on setup enforcement, persisted/reloaded bindings, composer identity mismatch, Send-button handling, raw-free failures, and single-instance behavior.
- [ ] Smoke output reflects actual assertions; no security status is hard-coded to `true`.
- [ ] Tracked ad-hoc `test_*.txt` and `all_tests_output*.txt` files are removed and ignored as generated evidence.
- [ ] Build has zero new nullable warnings in production and test code.
- [ ] Full tests, product smoke, and installer/release smoke pass with raw-free artifacts.
