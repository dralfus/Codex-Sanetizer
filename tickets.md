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
- [ ] Full tests, and installer/release smoke pass with raw-free artifacts.

## 256. Add production integration tests for single-instance enforcement

**What to build:** Add tests that verify the single-instance behavior at the production entry point (`CodexRedactionGate.Tray.exe`). Tests should verify that launching the tray twice results in one hook-owning process and one tray icon, with the second launch exiting cleanly after attempting to activate the existing instance.

**Blocked by:** 252. Wire per-user single-instance enforcement into the installed tray entry point.

**Do not:** Test only the helper class in isolation; rely on mock mutexes; or skip testing the exit code path for second launches.

- [ ] Test verifies second launch detects existing instance via `IsAnotherInstanceRunning("tray")`
- [ ] Test verifies second launch calls `ActivateExistingInstance("tray")` before exiting
- [ ] Test verifies second launch exits with code 0 (raw-free) not 1 (error)
- [ ] Test verifies first instance retains hook ownership and tray icon
- [ ] Test verifies behavior on abnormal first-instance termination (mutex cleanup)

## 257. Implement actual window activation in ActivateExistingInstance

**What to build:** Implement real window activation in `ActivateExistingInstance` so that when a second launch occurs, the user's existing tray window is brought to the foreground. This requires storing the tray window handle in a shared location (e.g., Windows message clipboard or named shared memory) when the first instance starts.

**Blocked by:** 252. Wire per-user single-instance enforcement into the installed tray entry point.

**Do not:** Return true without actual activation; rely on process ID alone; or expose window handles in a way that allows unauthorized access.

- [ ] First instance stores its main window handle in a per-user shared location on startup
- [ ] Second instance retrieves the stored handle and activates the window via Win32 API
- [ ] Activation failure returns false; success returns true with actual foreground activation
- [ ] Shared handle storage uses proper ACLs to allow only the same user to access it
- [ ] Cleanup removes the stored handle on normal exit

## 258. Add user notification when second tray instance is blocked

**What to build:** Add user-facing notification when a second launch of the tray is blocked by single-instance enforcement. The notification should briefly inform the user that the tray is already running and has been activated.

**Blocked by:** 252. Wire per-user single-instance enforcement into the installed tray entry point.

**Do not:** Show a modal dialog that requires user interaction; log to event viewer without user feedback; or leak window titles or process IDs in the message.

- [ ] Second launch shows a non-modal, auto-dismissing notification (toast or balloon)
- [ ] Notification text is localized and contains no sensitive information
- [ ] Notification is suppressed if activation succeeded silently
- [ ] Notification includes a link to "Open diagnostics" for troubleshooting

## 259. Add Global\ mutex option for multi-user system support

**What to build:** Add support for global mutex (with `Global\` prefix) to enable single-instance enforcement across user sessions on multi-user systems. This should be optional and configurable via a registry setting or config file.

**Blocked by:** 252. Wire per-user single-instance enforcement into the installed tray entry point.

**Do not:** Use global mutex by default (requires elevation); change existing per-user behavior; or expose the setting to end users without clear explanation.

- [ ] Add `--global` command-line flag or registry setting to enable global mutex
- [ ] Global mutex uses `Global\CodexRedactionGate_tray` name
- [ ] Per-user mutex continues to use `CodexRedactionGate_tray` (no prefix)
- [ ] Code validates elevation before allowing global mutex creation
- [ ] Documentation explains the elevation requirement and use cases

## 260. Fix IsAnotherInstanceRunning to handle mutex reentrancy correctly

**What to build:** Fix the mutex ownership check in `IsAnotherInstanceRunning` so it correctly detects whether another process owns the mutex, not just whether the current process can acquire it. The current implementation using `WaitOne(TimeSpan.Zero)` fails when the mutex is already owned by the current thread (re-entrant case) or when checking from a different context.

**Blocked by:** 252. Wire per-user single-instance enforcement into the installed tray entry point.

**Do not:** Change the existing API signature; rely on `Mutex.WaitOne()` without proper ownership tracking; or introduce race conditions between check and use.

- [ ] `IsAnotherInstanceRunning` correctly identifies when another process owns the mutex
- [ ] Implementation handles re-entrant scenarios where current thread already holds the mutex
- [ ] Edge case: mutex is released but not closed (handle recycling scenario)
- [ ] Tests cover: normal case, same-thread re-entrancy, process crash recovery
- [ ] No race condition between `IsAnotherInstanceRunning` and `SingleInstanceEnforcement` constructor

## 261. Make user notification configurable (optional/suppressible)

**What to build:** Make the user notification shown when a second tray instance is blocked configurable via a registry setting or config file. Users should be able to suppress notifications entirely or choose notification type (toast vs balloon vs none).

**Blocked by:** 258. Add user notification when second tray instance is blocked.

**Do not:** Hard-code notification behavior; require recompilation for configuration changes; or expose setting to end users without clear documentation.

- [ ] Add registry key `HKEY_CURRENT_USER\Software\CodexRedactionGate\SingleInstance` with notification settings
- [ ] Setting `DisableNotification` (DWORD) suppresses all user notifications
- [ ] Setting `NotificationType` (REG_SZ) allows: `toast`, `balloon`, `messagebox`, `none`
- [ ] Default value shows notification with message box for compatibility
- [ ] Configuration is read once at startup and cached

## 262. Add unit tests for SingleInstanceEnforcement crash recovery

**What to build:** Add unit tests that verify `SingleInstanceEnforcement` behaves correctly when a process crashes or is terminated unexpectedly while holding the mutex. The tests should verify that a subsequent launch can still detect and work correctly.

**Blocked by:** 256. Add production integration tests for single-instance enforcement.

**Do not:** Rely on actual process termination (flaky tests); skip testing mutex cleanup timing; or assume immediate cleanup after process exit.

- [ ] Test simulates crash by letting OS clean up mutex on process exit
- [ ] Test verifies second launch successfully detects new single instance
- [ ] Test verifies `IsFirstInstance` is true for the new instance
- [ ] Test verifies no stale state persists between crashes
- [ ] Test covers multiple rapid crash-restart cycles

## 263. Improve ActivateExistingInstance documentation and limitations

**What to build:** Add comprehensive documentation to `ActivateExistingInstance` explaining the current implementation limitations and what would be required for full window activation. Include code comments about window handle storage requirements and Win32 API dependencies.

**Blocked by:** 257. Implement actual window activation in ActivateExistingInstance.

**Do not:** Remove current simplified implementation; add implementation details to public API; or document as "TODO" without concrete guidance.

- [ ] XML documentation explains why window activation is not implemented
- [ ] Comments describe required window handle storage mechanism (named shared memory)
- [ ] Documentation references Win32 API functions needed: `FindWindow`, `SetForegroundWindow`
- [ ] Document thread-safety requirements for handle storage
- [ ] Add note about elevation requirements for cross-user scenarios

## 264. Add localization support for user notifications

**What to build:** Add localization infrastructure for user-facing notification messages. Notifications should be displayed in the user's preferred language based on system locale or registry setting.

**Blocked by:** 258. Add user notification when second tray instance is blocked.

**Do not:** Hard-code all message strings in English; require resource files for every language; or add complexity before localization is needed.

- [ ] Notification messages stored in external resource files (`.resx`)
- [ ] Default fallback to English if locale not supported
- [ ] Supported locales: English (en-US), Russian (ru-RU), Chinese (zh-CN)
- [ ] ResourceManager loads appropriate message based on current UI culture
- [ ] No localization required for technical error details (stack traces, etc.)
