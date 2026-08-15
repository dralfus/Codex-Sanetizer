---

# Tickets: Codex Redaction Gate - SurfaceMetadata and Native Submit Improvements

All tickets 238-250 completed. The convergence frontier starts with ticket 273.

Work the **frontier**: any ticket whose blockers are all done. For a purely linear chain that means top to bottom.

## Required Contract For Every New Ticket

Every ticket added after this section must state these four items before implementation begins:

- **State owner:** the one component that publishes the authoritative state for this behavior. UI and persisted records may project it, but cannot independently decide protection.
- **Fail-closed state:** the exact externally visible state and cloud-submission behavior when evidence, activation, recovery, or storage is uncertain.
- **Allowed transitions:** the permitted state changes, their triggering commands/events, and the condition for publishing each new state.
- **Deterministic proof:** the highest available test seam and the assertions that prove the transition without timer polling, foreground focus, or a live cloud submission.

If one of these cannot be stated, the work is an architecture-discovery ticket and must be resolved before a feature ticket is implemented.

## 273. Publish atomic resident protection snapshots

**What to build:** Make the resident native adapter execute every interception event from one immutable, versioned protection snapshot rather than separately mutable profile, binding, hook, controller, runner, and reload fields. The snapshot is the only runtime source for classification and guarded completion; display state remains derived from that runtime state rather than a competing copy.

**Blocked by:** 250. Restore verified composer identity after SurfaceMetadata migration.

**Do not:** Publish profile data before its guarding hook/controller is ready, replace fields one by one during reload, let a failed candidate remove working protection, let concurrent reloads restore each other's stale state, let a callback fall back to mutable runtime fields after reading a snapshot, or use locking that can block a low-level input callback on UI Automation or sanitization work.

- [x] The published snapshot contains all data needed to classify and guard a selected-profile submit, including generation, selected profiles/bindings, hook readiness, guarded submit flow, and target-identity contract.
- [x] Keyboard and pointer hook callbacks take one memory-safe snapshot read at their entry boundary; all classification, runtime lookup, guarded completion, and status publication for that event use only that captured generation.
- [x] A reload validates the complete candidate, starts its required hook resources, and publishes it through one memory-safe operation only after successful activation. The previous hook/runtime remains usable until this publication.
- [x] Candidate validation and activation failure stop only candidate resources and retain the previous complete snapshot, hook, controller, runner, profiles, bindings, and status without any mixed or stale displayed state.
- [x] Reload operations are serialized or otherwise made linearizable: two concurrent reloads cannot interleave publication, rollback, or hook stop/start operations.
- [x] Snapshot composition avoids manually duplicated mutable UI/runtime fields; the resident status exposed to the tray agrees with the published generation.
- [x] Tests simulate successful reload, activation failure/rollback, concurrent event classification during reload, and concurrent reload requests without timing sleeps or a live cloud submission. The tests assert that every event used one generation and that no raw submission path is released.

## 274. Make selected-client uncertainty and target identity explicit

**What to build:** Implement the resident decision matrix that separates selected AI-client uncertainty from unrelated application input and carries the initiating composer/window identity into deferred sanitize, confirmation, and replay work.

**Blocked by:** 273. Publish atomic resident protection snapshots.

**Do not:** Treat an unrecognized selected AI control as unrelated, re-read the foreground window after suppression to choose a replay target, pass through a selected-client Send because UI Automation or hook status is uncertain, or suppress ordinary input in an unrelated application.

- [x] Selected verified Send suppresses and enters the protected flow; selected verified non-Send/newline passes through.
- [x] Uncertain composer, Send-control identity, UI Automation, hook health, setup, or target validity inside a selected AI client suppresses with a raw-free status.
- [x] Uncertain input outside selected AI clients continues normally.
- [x] The deferred flow carries snapshot generation and captured composer/window identity; an invalid or changed target aborts raw-free and cannot submit or redirect to the current foreground window.
- [x] Tests cover the complete matrix, focus change after suppression, classifier exceptions, and repeated events after cancellation.

## 251. Make selected-profile setup and binding changes fail closed in the resident hook

**What to build:** Make first-run setup and Send-binding changes transactional for every explicitly selected AI profile. While setup or a binding change is incomplete, the resident hook must guard the selected app's configured Send path; after verification succeeds it must atomically reload the new profile and binding into the live hook without requiring a tray restart.

**Blocked by:** 273. Publish atomic resident protection snapshots.

**Do not:** Pass through because the startup/default profile is disabled, treat one protected profile as setup completion for a different selected unprotected profile, retain `protected` while a new pair is awaiting verification, silently default to `Enter`, or swallow setup failures without a visible raw-free fail-closed status.

- [x] A selected but unconfigured profile suppresses its matching Send gesture with `setup_required`; unrelated apps and non-Send input continue to pass through.
- [x] Setup completion is evaluated for the selected profile set, not merely because any profile is protected.
- [x] Selecting a new Send/newline pair immediately invalidates the old protected profile; only the successfully verified pair becomes protected.
- [x] The tray replaces/restarts the live native controller and hook with the newly verified profile, and tray status reports the same active pair.
- [x] The previous resident hook remains active until a replacement hook has started successfully; a replacement failure restores the prior protected runtime.
- [x] Cancellation, timeout, storage failure, and unexpected setup exception leave the app fail-closed with raw-free diagnostics.
- [x] Tests cover empty store, two selected profiles, binding change from protected state, resident reload, setup cancel/failure, and no raw submission.

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

**What to build:** Integrate production UI-control identification and native Send-button activation with native interception. CS must guard the identifiable Send button for a selected protected AI profile while skill pickers and other non-Send controls retain normal keyboard behavior. Programmatic UI Automation `Invoke()` is explicitly out of this ticket's scope and is addressed by ticket 275.

**Blocked by:** 251. Make selected-profile setup and binding changes fail closed in the resident hook; 274. Make selected-client uncertainty and target identity explicit.

**Do not:** Classify every foreground window as a composer or Send control, suppress ordinary `Enter` in a skill picker, leave mouse Send unguarded, or permit a raw fallback when control identity is unavailable.

- [x] Focused-control discovery distinguishes verified composer, identifiable selected-app Send control, and non-Send controls using raw-free UI Automation evidence.
- [x] Keyboard activation of an identifiable Send control and native mouse Send activation enter the same suppress-first protected flow. Programmatic UI Automation `Invoke()` remains explicitly unsupported until ticket 275 supplies a pre-action boundary.
- [x] Non-Send controls and the configured newline shortcut pass through unchanged.
- [x] Unknown control identity on a selected protected Send path fails closed without exposing prompt/window/control text.
- [x] Once the foreground window is identified as a selected AI client, an unrecognized or transiently unavailable Send-control identity cannot release the original click.
- [x] Tests cover composer, skill picker, Send button, mouse activation, selected versus unselected apps, and overlay-originated replay.

## 254. Make crash and failure diagnostics structurally raw-free

**What to build:** Replace all persistence and outward diagnostics of arbitrary exception messages/stack traces with one local raw-free crash-report boundary. It must retain only an allowlisted component, exception type/category, build version, timestamp, and safe status code while preserving fail-closed behavior.

**Blocked by:** None — can start immediately.

**Do not:** Serialize or print `Exception.Message`, `StackTrace`, raw paths, window titles, prompt text, configuration contents, or scanner findings; duplicate crash-report schemas across sanitizer, readiness, and native-submit paths; or turn a diagnostic write failure into a send path.

- [x] All crash reports use one schema and one writer with allowlisted raw-free fields only.
- [x] Orchestrator, native-submit, sanitizer, and DPAPI/readiness failures return raw-free diagnostics without exception text.
- [x] Tray/CLI crash viewing shows only the safe summary.
- [x] Tests inject exceptions containing synthetic prompt, path, and window-title values and prove none reach reports, status, audit, or CLI output.

## 256. Add production integration tests for single-instance enforcement

**What to build:** Add tests that verify the single-instance behavior at the production entry point (`CodexRedactionGate.Tray.exe`). Tests should verify that launching the tray twice results in one hook-owning process and one tray icon, with the second launch exiting cleanly after attempting to activate the existing instance.

**Blocked by:** 252. Wire per-user single-instance enforcement into the installed tray entry point.

**Do not:** Test only the helper class in isolation; rely on mock mutexes; or skip testing the exit code path for second launches.

- [x] Test verifies second launch detects existing instance via `IsAnotherInstanceRunning("tray")`
- [x] Test verifies second launch calls `ActivateExistingInstance("tray")` before exiting
- [x] Test verifies second launch exits with code 0 (raw-free) not 1 (error)
- [x] Test verifies first instance retains hook ownership and tray icon
- [x] Test verifies behavior on abnormal first-instance termination (mutex cleanup)

## 257. Implement actual window activation in ActivateExistingInstance

**What to build:** Implement real window activation in `ActivateExistingInstance` so that when a second launch occurs, the user's existing tray window is brought to the foreground. This requires storing the tray window handle in a shared location (e.g., Windows message clipboard or named shared memory) when the first instance starts.

**Blocked by:** 252. Wire per-user single-instance enforcement into the installed tray entry point.

**Do not:** Return true without actual activation; rely on process ID alone; or expose window handles in a way that allows unauthorized access.

- [x] First instance stores its main window handle in a per-user shared location on startup
- [x] Second instance retrieves the stored handle and activates the window via Win32 API
- [x] Activation failure returns false; success returns true with actual foreground activation
- [x] Shared handle storage uses proper ACLs to allow only the same user to access it
- [x] Cleanup removes the stored handle on normal exit

## 258. Add user notification when second tray instance is blocked

**What to build:** Add one user-facing notification when a second launch of the tray is blocked by single-instance enforcement. It must briefly report that Code Sanitizer is already running; it remains visible whether foreground activation succeeds or falls back.

**Blocked by:** 252. Wire per-user single-instance enforcement into the installed tray entry point.

**Do not:** Show a modal dialog that requires user interaction; log to event viewer without user feedback; or leak window titles or process IDs in the message.

- [x] Second launch shows one non-modal, auto-dismissing notification (toast or balloon), regardless of whether foreground activation succeeds
- [x] Notification text is localized and contains no sensitive information
- [x] No second-instance path opens a modal dialog or relies on a hidden activation form as the only user-visible outcome
- [x] Notification directs the user to the resident tray icon for local diagnostics without exposing data

## 259. Add Global\ mutex option for multi-user system support

**What to build:** Add support for global mutex (with `Global\` prefix) to enable single-instance enforcement across user sessions on multi-user systems. This should be optional and configurable via a registry setting or config file.

**Blocked by:** 252. Wire per-user single-instance enforcement into the installed tray entry point.

**Do not:** Use global mutex by default (requires elevation); change existing per-user behavior; or expose the setting to end users without clear explanation.

- [x] Add `--global` command-line flag or registry setting to enable global mutex
- [x] Global mutex uses `Global\CodexRedactionGate_tray` name
- [x] Per-user mutex continues to use `CodexRedactionGate_tray` (no prefix)
- [x] Code validates elevation before allowing global mutex creation
- [x] Documentation explains the elevation requirement and use cases

## 260. Fix IsAnotherInstanceRunning to handle mutex reentrancy correctly

**What to build:** Fix the mutex ownership check in `IsAnotherInstanceRunning` so it correctly detects whether another process owns the mutex, not just whether the current process can acquire it. The current implementation using `WaitOne(TimeSpan.Zero)` fails when the mutex is already owned by the current thread (re-entrant case) or when checking from a different context.

**Blocked by:** 252. Wire per-user single-instance enforcement into the installed tray entry point.

**Do not:** Change the existing API signature; rely on `Mutex.WaitOne()` without proper ownership tracking; or introduce race conditions between check and use.

- [x] `IsAnotherInstanceRunning` correctly identifies when another process owns the mutex
- [x] Implementation handles re-entrant scenarios where current thread already holds the mutex
- [x] Edge case: mutex is released but not closed (handle recycling scenario)
- [x] Tests cover: normal case, same-thread re-entrancy, process crash recovery
- [x] No race condition between `IsAnotherInstanceRunning` and `SingleInstanceEnforcement` constructor

## 261. Make user notification configurable (optional/suppressible)

**What to build:** Make the non-modal notification shown when a second tray instance is blocked configurable via a registry setting. Users should be able to suppress notifications entirely or choose its non-modal presentation.

**Blocked by:** 258. Add user notification when second tray instance is blocked.

**Do not:** Hard-code notification behavior; require recompilation for configuration changes; or expose setting to end users without clear documentation.

- [x] Add registry key `HKEY_CURRENT_USER\Software\CodexRedactionGate\SingleInstance` with notification settings
- [x] Setting `DisableNotification` (DWORD) suppresses all user notifications
- [x] Setting `NotificationType` (REG_SZ) allows: `toast`, `balloon`, `none`; a legacy `messagebox` value is treated as `balloon` and never opens a modal dialog
- [x] Default value shows a balloon notification
- [x] Configuration is read once for the short-lived second-launch process and retained for its notification decision

## 262. Add unit tests for SingleInstanceEnforcement crash recovery

**What to build:** Add unit tests that verify `SingleInstanceEnforcement` behaves correctly when a process crashes or is terminated unexpectedly while holding the mutex. The tests should verify that a subsequent launch can still detect and work correctly.

**Blocked by:** 256. Add production integration tests for single-instance enforcement.

**Do not:** Rely on actual process termination (flaky tests); skip testing mutex cleanup timing; or assume immediate cleanup after process exit.

- [x] Test simulates crash by letting Windows clean up mutex ownership when the owner thread exits
- [x] Test verifies the next launch successfully detects the available single-instance slot
- [x] Test verifies `IsFirstInstance` is true for the recovered launch
- [x] Test verifies no stale ownership persists between cycles
- [x] Test covers multiple rapid crash-restart cycles
- [x] The abandoned-mutex regression waits through a bounded recovery window instead of assuming immediate kernel handoff after the owner thread exits.

## 263. Improve ActivateExistingInstance documentation and limitations

**What to build:** Document the implemented per-user activation path, its Windows foreground limitations, storage lifecycle, and the safe fallback when activation cannot be completed.

**Blocked by:** 257. Implement actual window activation in ActivateExistingInstance.

**Do not:** Claim cross-user or cross-session activation; expose stored window handles to other users; or document the fallback as raw pass-through.

- [x] XML documentation explains the per-user handle registration, validation, and cleanup lifecycle
- [x] Documentation references the `IsWindow`, `ShowWindow`, and `SetForegroundWindow` Win32 calls and their failure semantics
- [x] Document thread-safety and stale-handle cleanup requirements for the shared store
- [x] README states the same-user/session boundary and the visible notification fallback

## 264. Add localization support for user notifications

**What to build:** Add localization infrastructure for user-facing notification messages. Notifications should be displayed in the user's preferred language based on system locale or registry setting.

**Blocked by:** 258. Add user notification when second tray instance is blocked.

**Do not:** Hard-code all message strings in English; require resource files for every language; or add complexity before localization is needed.

- [x] Notification messages stored in external resource files (`.resx`)
- [x] Default fallback to English if locale not supported
- [x] Supported locales: English (en-US), Russian (ru-RU), Chinese (zh-CN)
- [x] ResourceManager loads appropriate message based on current UI culture
- [x] No localization required for technical error details (stack traces, etc.)

## 265. Keep first-run fail-closed protection active while onboarding is displayed

**What to build:** Start the resident message loop before opening first-run setup, so the native hook continues to suppress a selected app's Send path while the user verifies profiles.

**Blocked by:** 251. Make selected-profile setup and binding changes fail closed in the resident hook; 274. Make selected-client uncertainty and target identity explicit.

- [x] Setup is scheduled after the tray message loop begins and does not block hook dispatch.
- [x] A selected Send during setup is suppressed with a raw-free `setup_required` result.
- [x] An unexpected setup-worker failure produces a visible raw-free retry path while protection remains blocked.
- [x] Regression test covers the real application-context lifecycle without a live cloud submission.

## 266. Route resident interception across every selected verified profile

**What to build:** Replace first-profile selection with profile routing so Codex Desktop and ChatGPT Desktop can both be selected and protected concurrently.

**Blocked by:** 251. Make selected-profile setup and binding changes fail closed in the resident hook; 273. Publish atomic resident protection snapshots; 274. Make selected-client uncertainty and target identity explicit.

- [x] The resident controller dispatches by the active selected profile.
- [x] Each enabled profile owns its verified binding and a failure for one does not unprotect another.
- [x] The profile selected during interception is carried into the deferred sanitize/send flow; a focus change cannot redirect the flow to a different composer.
- [x] Hook callbacks read one atomically published runtime snapshot; reload and rollback cannot expose a mixed controller/profile/runner state.
- [x] The deferred flow uses the captured composer/window identity, not a later focused-window lookup, and aborts raw-free if that target is no longer valid.
- [x] Tests cover both profiles in one persisted store.

## 267. Make selected Send-control classification fail closed and bounded

**What to build:** Persist verified UI Automation evidence for each selected Send control, support localized evidence, and prevent a UIA error or hook timeout from releasing a selected Send click.

**Blocked by:** 253. Connect selected-app Send controls to native interception without blocking other controls; 274. Make selected-client uncertainty and target identity explicit.

- [x] A selected Send candidate is never passed through because UIA discovery failed or its localized label changed.
- [x] Keyboard and mouse callback exceptions after a selected Send candidate fail closed rather than calling through to the original input.
- [x] UIA work is bounded outside the low-level hook callback and hook-loss is surfaced as a raw-free degraded state.
- [x] An unproven callback failure in an unrelated application does not suppress ordinary keyboard or mouse input.
- [x] Candidate classification distinguishes selected-app uncertainty from unrelated input: selected-app uncertainty blocks Send, unrelated input continues normally.
- [x] Tests cover localized evidence, transient UIA failure, and non-Send controls.

## 268. Remove raw exception messages from all interactive UI failure paths

**What to build:** Replace `Exception.Message` in tray, dictionary, and local-restore dialogs with safe status text and centralized local crash capture.

**Blocked by:** 254.

- [x] No interactive failure dialog includes an arbitrary exception message, path, prompt, title, or rule value.
- [x] DPAPI and other storage exceptions use stable public status codes; raw causes remain only as local inner exceptions.
- [x] Tests inject synthetic sensitive values and prove UI output remains raw-free.

## 269. Exercise installed resident runtime paths in release smoke

**What to build:** Make release smoke execute the application-context/native-hook lifecycle seams for profile reload, setup blocking, selected Send control failure, and second-launch behavior.

**Blocked by:** 265, 266, 267.

- [x] Smoke fails when any listed resident boundary is disconnected or pass-through.
- [x] The smoke result contains only raw-free evidence.
- [x] Smoke launches the actual application context and proves hook registration, setup gating, runtime reload, selected Send failure handling, and second-instance behavior without a cloud submission.
- [x] Smoke does not treat a fake hook host, a constant, or application-file presence as proof that a resident hook/lifecycle boundary worked.

## 278. Fail closed when the bounded native callback fallback itself faults

**What to build:** Ensure that an exception in the low-level hook's bounded selected-target fallback cannot turn a previously selected Send candidate into pass-through. The fallback must preserve normal input for a known unrelated target while emitting a raw-free failure result for a selected target.

**Blocked by:** 277. Bind native Send decisions to the captured target before callback timeout.

- [x] A selected cached target remains suppressed when its bounded fallback throws.
- [x] An unrelated cached target continues normally when the fallback throws or has no identity.
- [x] The callback records only a stable raw-free status; it does not expose exception text.
- [x] Tests inject the failure at the hook-host boundary for both keyboard and pointer input.

## 279. Correct the native mouse-hook entry point

**What to build:** Register the low-level mouse Send hook through the real Win32 `SetWindowsHookEx` API so selected-app mouse Send interception can start without crashing the tray process.

**Blocked by:** None.

**Do not:** Catch and hide a missing Win32 entry point, degrade startup to raw pass-through, or claim mouse Send is protected when the hook was never registered.

- [x] Mouse hook registration invokes the actual `user32!SetWindowsHookEx` entry point with the low-level mouse callback signature.
- [x] Tray startup reaches the first-instance native-hook path without `EntryPointNotFoundException`.
- [x] Regression tests cover tray startup with the production mouse-hook host.

## 270. Make second-instance activation visibly useful

**What to build:** Ensure every second launch gives a visible non-modal outcome even if foreground activation of the resident activation window succeeds.

**Blocked by:** 257.

- [x] A second launch produces a visible non-modal user outcome without relying on the invisible activation form.
- [x] Tests cover activation success and fallback with exactly one notification decision.

## 271. Centralize crash bootstrap and local crash-directory resolution

**What to build:** Remove duplicated creation of the crash diagnostics directory/writer from CLI and UI startup so every crash boundary uses the same raw-free initialization path.

**Blocked by:** 254.

- [x] CLI, UI-thread, and readiness crash capture call one shared initialization path.
- [x] A capture failure never changes the protected-send decision or prints raw exception text.
- [x] Crash-view CLI resolves its reports directory through the same shared default path API.
- [x] Focused tests cover the shared bootstrap path.

## 272. Replace setup-form control discovery with typed profile-card state

**What to build:** Make binding selection and verification status independent of label text and nested WinForms control traversal, so localization and layout changes cannot update the wrong profile.

**Blocked by:** 251.

- [x] Each visible profile has a typed state/control reference rather than label-text lookup.
- [x] Binding selection and verification status update the intended profile without label-text matching.
- [x] Tests cover both desktop profiles and a localized display label.

## 275. Establish a pre-action enforcement boundary for programmatic UI Automation Send

**What to build:** Publish third-party programmatic UI Automation `Invoke()` as explicitly unsupported until a verified pre-action enforcement boundary exists. UIA providers can raise `InvokedEvent` at provider-dependent times, but the external client API cannot cancel a third-party application's `Invoke()` action. The verified keyboard/mouse Send path remains separately protected.

**Blocked by:** None. Product decision: publish the unsupported path now; do not implement a cloud-egress or in-client boundary in this release.

**Do not:** Treat `InvokedEvent` observation as prevention, report a raw Send as protected after it has happened, or silently broaden low-level keyboard/mouse hooks to unrelated applications.

- [x] The profile is explicitly reported as `programmatic_uia_invoke_unsupported` for that path.
- [x] Protected status and diagnostics distinguish manual pre-action enforcement from programmatic UIA non-prevention using raw-free statuses.
- [x] Tests prove a protected keyboard/mouse profile cannot report programmatic UIA activation as successfully protected when no pre-action boundary is active.
- [x] Documentation records the Windows UIA limitation and the selected unsupported-path design.

## 277. Bind native Send decisions to the captured target before callback timeout

**What to build:** Carry the low-level hook's captured window identity through focused Send discovery and the bounded callback fallback. A focus change must fail closed for the original selected Send path, while unrelated windows keep their normal input. The fallback must use a precomputed raw-free selected-profile identity and must not perform UIA or unbounded process/window discovery on the hook callback thread.

**Blocked by:** 253. Connect selected-app Send controls to native interception without blocking other controls; 267. Make selected Send-control classification fail closed and bounded.

**Do not:** Re-read the foreground window as proof for an earlier key/click, pass through when selected-identity resolution throws, block unrelated input merely because identity is unavailable, or report programmatic UIA `Invoke()` as protected.

- [x] Focused Send discovery receives and verifies the hook-captured target; identity mismatch suppresses the original Send raw-free.
- [x] Timeout and exception fallback use only bounded captured/precomputed identity, without UIA or foreground re-discovery on the hook callback thread.
- [x] A captured selected submit remains suppressed when the resolver fails; an unrelated captured window passes through.
- [x] First-run focused Send activation returns `setup_required` rather than `binding_unknown`.
- [x] Tests cover focus switching during classification, resolver failure, selected/unselected profiles, and focused Enter/Space during onboarding.

## 276. Test the live tray message-loop onboarding lifecycle

**What to build:** Exercise the actual Windows tray application context on an STA message loop and prove that native interception is registered before first-run setup work begins, with no cloud submission.

**Blocked by:** 265. Keep first-run fail-closed protection active while onboarding is displayed.

- [x] The test creates the real tray application context on an STA thread and runs its message loop.
- [x] The test proves native hook startup occurs before setup work and setup is dispatched only after the loop accepts posted work.
- [x] A cancelled setup leaves the native hook registered and completes without a live cloud submission or blocking dialog; failed setup continues to use the visible raw-free retry path from ticket 265.
- [x] The test has a bounded timeout and cleans up its notification icon, activation window, and temporary storage.

## 255. Make release smoke exercise the real protected-send invariants and remove committed test run artifacts

**What to build:** Make the final release smoke consume the real resident-lifecycle evidence from the protected-send harness, then verify package hygiene and raw-free release output. This is the only ticket that can declare the current native-protection frontier release-ready.

**Blocked by:** 252. Wire per-user single-instance enforcement into the installed tray entry point; 254. Make crash and failure diagnostics structurally raw-free; 269. Exercise installed resident runtime paths in release smoke; 272. Replace setup-form control discovery with typed profile-card state.

**Do not:** Set smoke statuses to constants, infer resident protection from file presence, treat a passing unit-test suite as proof of the resident hook, retain generated console logs as source artifacts, hide warnings, or add raw-sensitive fixtures to release output.

- [x] Product smoke consumes executable lifecycle assertions for setup enforcement, atomic reload/rollback, composer identity mismatch, selected Send handling, target-change abort, raw-free failures, and single-instance behavior.
- [x] Smoke output reflects actual assertions and raw-free evidence; no security status is hard-coded to `true`.
- [x] Tracked ad-hoc `test_*.txt` and `all_tests_output*.txt` files are removed and ignored as generated evidence.
- [x] Build has zero new nullable warnings in production and test code.
- [x] Full tests, installer smoke, and the final release smoke pass with raw-free artifacts.

## 280. Isolate self-test from user-local DPAPI protection state

**What to build:** Make `--self-test` validate the deterministic sanitizer and restore workflows without reading, creating, rotating, or deleting the current user's production DPAPI secret or mapping vault. A failed production DPAPI readiness check belongs to `--doctor`; it must not make the isolated self-test unavailable.

**Blocked by:** None — can start immediately.

**Do not:** Delete, replace, or silently rotate a user-local DPAPI secret or mapping vault; downgrade production DPAPI protection; treat an isolated self-test pass as proof that the installed user's vault is ready; or emit raw paths, exception text, prompt data, or protected values.

- [x] `--self-test` uses an isolated in-memory test vault and succeeds even when the production sanitizer factory would fail with a DPAPI error.
- [x] The self-test path does not call the production sanitizer factory or touch the default storage layout.
- [x] Production `--doctor` continues to report a raw-free DPAPI readiness failure instead of being hidden by self-test isolation.
- [x] Tests prove both the isolated self-test success and raw-free production DPAPI failure reporting.

## 281. Recover unreadable local DPAPI protection safely and prevent partial secret writes

**What to build:** Give a user whose local DPAPI HMAC secret or mapping vault cannot be opened a confirmed, raw-free recovery workflow. The workflow must preserve the old protected artifacts for investigation, explain that previous restorable mappings may no longer be recoverable, and create a fresh protected local state only after the user explicitly approves it. New HMAC secret creation must be crash-safe, so an interrupted write cannot itself leave a truncated secret file.

**Blocked by:** None - can start immediately.

**Do not:** Delete, overwrite, or silently rotate the existing secret or vault; expose raw paths, exception messages, mappings, prompts, or protected values; treat `--self-test` success as proof that the user's local vault is ready; or downgrade to plaintext storage.

- [x] `--doctor` and the tray present one stable recovery-required status when the production DPAPI secret or vault is unreadable, while normal protected Send remains fail-closed.
- [x] Recovery requires explicit local confirmation, preserves the previous secret and vault as a recoverable backup/quarantine, and warns that old pseudonyms may not be restorable after recovery.
- [x] A confirmed recovery creates a fresh user-scoped DPAPI secret and vault; a follow-up doctor check reports the new local state accurately without leaking paths or values.
- [x] Secret provisioning uses an atomic write/replace path and tests cover interrupted/contended creation, cancellation, and the unreadable-secret recovery path.

## 282. Separate broker evidence from live project-file protection status

**What to build:** Make readiness, tray diagnostics, CLI output, and product smoke distinguish a tested local file-context broker from actual live Codex project-file enforcement. A successful in-memory broker exercise must never make the released product report that the live Codex file channel is protected.

**Blocked by:** None - can start immediately.

**Do not:** Change the current live capability to `true` without a verified pre-cloud client integration; hide the unsupported status behind a generic `ready` result; or remove the useful broker workflow tests.

- [x] `--product-smoke` reports broker-workflow evidence separately and reports live `project_files_protected: false` until a real client boundary is verified.
- [x] `--doctor`, tray status, and README use one consistent capability vocabulary that distinguishes composer protection, broker-demo capability, and live project-file protection.
- [x] Regression tests prove no aggregate readiness or release-success field can imply live project-file protection when the broker is only exercised in a temporary test workspace.

## 283. Prove a supported live ingress boundary for protected project files

**What to build:** Establish a real, pre-cloud integration boundary through which a selected coding workspace's supported file reads, attachments, and file-derived tool output must pass before model visibility. If the selected Codex/Desktop surface has no such supported boundary, keep the capability explicitly unsupported rather than implying protection from UI observation or a local broker demo.

**Blocked by:** 282. Separate broker evidence from live project-file protection status; a verified supported Codex/Desktop integration surface or an approved local gateway design.

**Do not:** Claim protection from post-action UI Automation events, scrape raw project content from the client after it has been sent, or turn every file in a task into a sequence of blocking confirmation dialogs.

- [ ] A disposable protected workspace demonstrates one real pre-cloud file-context operation entering the local broker and produces raw-free evidence that the model-visible payload is sanitized.
- [x] When the boundary is unavailable, protected-workspace mode fails closed for its local attachment and unmanaged-connector channels and reports `unsupported` rather than silently allowing those channels through Code Sanitizer.
- [ ] The user experience presents one operation-level batch summary with a navigable per-file list; it does not require accepting a separate popup for every file.
- [x] Live `project_files_protected` remains false unless a real ingress proof and its automated regression test exist.

**Current blocker:** The Windows Codex/ChatGPT Desktop surface exposes no verified pre-cloud integration point for repository reads, attachments, or file-derived tool output. Code Sanitizer therefore reports `project_file_ingress_unsupported`; it does not claim to block direct desktop-client file reads. The remaining two criteria require a supported client extension point or an approved local gateway that owns those operations before the client reaches the cloud.

## 284. Enforce source-whitespace hygiene in the release check

**What to build:** Remove current source whitespace defects and add a lightweight repeatable release check so trailing whitespace and extra end-of-file blank lines do not create noisy diffs or conceal substantive security changes.

**Blocked by:** None - can start immediately.

- [x] Tracked source and test files have no current `git diff --check` whitespace errors.
- [x] The documented release verification includes a non-interactive whitespace check that fails before packaging when new defects are introduced.

## 285. Show local protection capabilities and active state in the tray UI

**What to build:** Add a clear local protection-status view reachable from the tray. It must tell the user both which capabilities the installed product contains and which of them are currently active for this Windows session: DPAPI-backed local storage, automatic selected-app prompt protection, and live project-file protection.

**Blocked by:** 281. Recover unreadable local DPAPI protection safely and prevent partial secret writes; 282. Separate broker evidence from live project-file protection status.

**Do not:** Present DPAPI as an unsafe on/off switch; claim that a successful self-test proves production DPAPI readiness; collapse `broker demo`, `unsupported`, and `live protected` into one green file-protection label; or include raw paths, sensitive terms, prompts, mappings, or exception details in the status view.

- [x] The tray offers a dedicated local status view with separate rows for `Local DPAPI protection`, `Automatic prompt protection`, and `Project-file protection`.
- [x] Each row shows a stable capability state and an operational state: DPAPI is `ready`, `recovery required`, or `unavailable`; prompt protection is `active`, `setup required`, `degraded`, or `disabled`; project-file protection is `live protected`, `broker demo only`, `unsupported`, or `not configured`.
- [x] The view explains the immediate consequence of every non-green state and offers only safe relevant actions, such as profile verification, opening recovery, or opening protected-file management.
- [x] Tray status updates after profile verification, protection enable/disable, DPAPI recovery, and file-policy changes without requiring a restart; tests prove the displayed states remain raw-free and truthful.

## 286. Exclude selected files, including .env, from cloud file context

**What to build:** Let the user select one or more exact local files - including `.env` - for an `exclude from cloud` policy. Once live project-file interception is available, Codex/Desktop must not receive the contents, filename, path, attachment representation, or file-derived tool output of an excluded file. The user must see locally that the file was excluded and why.

**Blocked by:** 283. Prove a supported live ingress boundary for protected project files; 285. Show local protection capabilities and active state in the tray UI.

**Do not:** Treat a filename suffix match as proof that a file was excluded; sanitize and forward an explicitly excluded file; expose selected raw paths in cloud-bound logs or diagnostics; silently fall back to direct reads/uploads when interception is unavailable; or mark a file protected based only on a local preference without a verified pre-cloud enforcement path.

- [ ] The local UI can add, review, and remove one or more exact files from the exclusion policy, including `.env`; local display is allowed, while persisted/cloud-bound diagnostics use protected or raw-free identities.
- [ ] For a protected workspace with a live ingress boundary, every supported path that could expose an excluded file - file read, direct attachment, file-derived tool output, diff, or patch context - is blocked before model visibility with a raw-free `file_excluded_from_cloud` status.
- [ ] The operation-level status view shows that an excluded file was withheld, identifies the policy outcome locally, and keeps the rest of the task/file batch usable without a per-file confirmation-dialog storm.
- [ ] If the live ingress boundary is missing, unhealthy, or cannot classify the selected file, the affected channel fails closed and the UI reports `unsupported` or `degraded`; it never claims the exclusion is enforced.
- [ ] Automated tests use synthetic `.env` and arbitrary-file fixtures to prove no raw contents, paths, or filenames reach model-visible payload records, audit output, or cloud-bound diagnostics.

## 287. Make DPAPI recovery rollback non-destructive when quarantine fails

**What to build:** Make confirmed local-protection recovery transactional even when the old secret or vault cannot be moved into quarantine. A failed repair must leave every pre-existing artifact intact and keep protected Send fail-closed, rather than deleting a file that was never successfully quarantined.

**Blocked by:** None - can start immediately.

**Do not:** Delete an original secret or vault during cleanup unless the recovery operation can prove that it created that specific replacement; treat a partial quarantine as a successful backup; expose file paths, exception messages, mappings, prompts, or protected values; or enable protected Send after any recovery failure.

- [x] The recovery workflow distinguishes original artifacts from replacements created during the current attempt and deletes only confirmed replacements during rollback.
- [x] A failure moving the first artifact and a failure moving the second artifact both return one raw-free recovery-failed status and retain byte-identical original secret and vault data.
- [x] A recovery cleanup or restore failure is handled fail-closed, leaves all remaining artifacts discoverable locally, and never escapes as an unhandled exception.
- [x] Automated tests cover the move-failure and rollback paths plus a successful recovery followed by an accurate raw-free doctor status.

## 288. Keep DPAPI recovery fail-closed across incomplete and concurrent attempts

**What to build:** Make local-protection recovery a single durable transaction. If rollback or cleanup cannot complete, or a second CLI/tray recovery runs while the first is incomplete, inspection and protected Send must remain in recovery-required mode until one explicit confirmed recovery has completed successfully. Existing artifacts must remain discoverable locally.

**Blocked by:** 287. Make DPAPI recovery rollback non-destructive when quarantine fails.

**Do not:** Auto-initialize a fresh secret or vault merely because an incomplete recovery temporarily leaves both normal paths absent; let a second recovery bypass the original confirmation; expose file paths, exception details, mappings, prompts, or protected values; or enable protected Send after any incomplete recovery state.

- [x] A local recovery transaction is serialized across CLI and tray invocations, records incomplete recovery durably before moving artifacts, and clears that state only after a fully successful confirmed recovery.
- [x] `--doctor`, tray startup, and the protected Send gate report one raw-free recovery-required status when an incomplete transaction or recovery backup accompanies an incomplete normal state; they do not create fresh local protection implicitly. A backup retained after a verified successful recovery remains locally discoverable without disabling the fresh ready state.
- [x] Cleanup and restore failures, including unexpected local file-operation failures, are contained and return one raw-free recovery-failed result rather than escaping an exception.
- [x] Automated tests prove byte-for-byte preservation of both secret and vault, failed-rollback follow-up inspection/Send blocking, and a competing recovery invocation that cannot initialize state without the completed confirmed transaction.

## 289. Atomically replace the resident protection runtime after confirmed DPAPI recovery

**What to build:** After a confirmed local DPAPI repair, keep Code Sanitizer resident and safely replace the complete sanitizer, local mapping vault, apply-only path, and native-submit runtime as one fail-closed transaction. The tray status must then report the new ready state without requiring an application restart.

**Blocked by:** 288. Keep DPAPI recovery fail-closed across incomplete and concurrent attempts.

**Do not:** Re-enable only one hook while another path still holds the old recovery-blocked sanitizer; permit a prompt during replacement; silently use a partially created vault; claim recovery is ready if the new runtime cannot be fully activated; or expose raw paths, prompts, mappings, exception text, or protected values.

- [x] Resident recovery enters a visible raw-free replacement state that blocks selected-app Send until the entire new runtime is ready.
- [x] A successful repair constructs a fresh production sanitizer and atomically swaps every resident submission path, then updates the tray and local-status view to `ready` without restarting the process.
- [x] A failed reload leaves the previous fail-closed runtime active and reports one stable raw-free degraded or recovery-required state.
- [x] Tests prove apply-only and native-submit paths both use the same new vault after recovery, no in-flight send bypasses replacement, and UI status updates remain raw-free.

## 290. Make tray profile remediation an explicit verification or retry workflow

**What to build:** Make the local-status action truthful for both `setup required` and `degraded` prompt protection. When setup is required it must run the focused verification flow; when setup is already complete but the live hook is degraded it must clearly retry activation rather than claim that the profile was re-verified.

**Blocked by:** 289. Atomically replace the resident protection runtime after confirmed DPAPI recovery.

**Do not:** Freeze the tray UI during focused verification; say a profile was verified when only a runtime reload occurred; create parallel setup flows; or enable protected Send after a failed retry.

- [x] The status view exposes distinct safe actions and wording for setup verification and degraded-hook retry.
- [x] Each action runs without blocking the tray UI and prevents duplicate concurrent attempts.
- [x] The relevant status is refreshed after completion and always reflects actual hook activation, not only the persisted profile record.
- [x] Tests cover setup required, verified-but-degraded, retry failure, cancellation, and raw-free public failure text.

## 291. Add end-to-end tray local-status lifecycle coverage

**What to build:** Add deterministic WinForms/tray integration coverage for the local protection status view so status rendering and refresh behavior are verified through the tray context rather than only through an injected row mapper.

**Blocked by:** 289. Atomically replace the resident protection runtime after confirmed DPAPI recovery.

**Do not:** Depend on a real Codex or ChatGPT window, user focus, timers, raw status diagnostics, or nondeterministic desktop timing in automated tests.

- [x] Tests prove the status command is reachable from the tray and opens one modeless status window.
- [x] Tests prove enabling/disabling protection and a persisted project-file policy change refresh the rendered rows without recreating the tray process.
- [x] Tests exercise disposal/close behavior so repeated status refreshes do not retain controls or timers.
- [x] Raw-free tests prove no rendered UI string contains synthetic paths, prompts, sensitive terms, mappings, or exception text.

## 292. Centralize single-flight execution for tray remediation actions

**What to build:** Keep profile verification and prompt-protection retry on one shared tray-action execution path so their background dispatch, UI callback, raw-free failure handling, and release of duplicate-action guards cannot drift apart.

**Blocked by:** 290. Make tray profile remediation an explicit verification or retry workflow.

**State owner:** The published resident protection snapshot; the tray action executor only requests remediation and projects the resulting state.

**Fail-closed state:** A cancellation, worker failure, activation failure, or dispatcher shutdown leaves the selected Send path blocked or degraded and exposes only a raw-free status.

**Allowed transitions:** `setup_required` may enter verification; `degraded` may enter retry; either publishes only the resident result after the worker completes. Duplicate requests do not create a second transition.

**Deterministic proof:** Injected background and UI dispatch queues prove single-flight execution and each completion outcome without timers, focus, or cloud submission.

- [x] Profile verification and prompt-protection retry use one tested single-flight action executor while preserving their distinct remediation behavior and public status text.
- [x] The shared executor releases its guard and refreshes truthful raw-free status after cancellation, runtime-creation failure, activation failure, and UI-dispatch shutdown.

## 293. Prove local status refresh after actual DPAPI recovery

**What to build:** Add a deterministic tray-level recovery seam and tests proving that successful and failed confirmed DPAPI recovery publish the correct local protection row and never enable protected Send early.

**Blocked by:** None - can start immediately.

**State owner:** The resident protection snapshot, including recovery/readiness and native runtime activation; the tray status is a projection of it.

**Fail-closed state:** `recovery_required` or `degraded` blocks protected Send until both confirmed recovery and resident-runtime activation succeed.

**Allowed transitions:** `recovery_required` -> `reloading` -> `ready` only after atomic runtime publication; any recovery or reload failure returns to `recovery_required` or `degraded`.

**Deterministic proof:** An injected recovery operation and resident-runtime factory drive the real tray command and assert rendered state without modal UI, timers, focus, or cloud submission.

- [x] A confirmed successful recovery publishes `reloading` through the resident snapshot, clears the protected-Send claim, reloads and starts the native runtime, and only then publishes ready.
- [x] Recovery, runtime reload, runtime activation, and unexpected recovery-operation failures remain fail-closed: selected-app Send is suppressed while local protection is not ready, while ordinary input remains pass-through, and public text excludes injected raw failure values.
- [x] Local recovery transitions advance the resident snapshot generation. Runtime replacement and native-flow state use compare-and-publish semantics so an older in-flight event cannot overwrite `reloading`, recovery-required, runtime-degraded, or ready state.

## 294. Complete tray status redraw disposal and raw-free coverage

**What to build:** Strengthen deterministic tray-context tests so repeated local-status redraws release old controls and no synthetic path, prompt, sensitive term, mapping, or exception text can reach rendered rows.

**Blocked by:** None - can start immediately.

**State owner:** The resident-state projection supplied to the local-status form; the form owns only disposable visual controls and its refresh timer.

**Fail-closed state:** Any unclassifiable or unsafe status renders a stable raw-free non-green state and never a protected claim.

**Allowed transitions:** A published resident-state change replaces the current rendered rows; closing the form disposes its timer and all current/replaced controls.

**Deterministic proof:** Direct tray-context refreshes retain old controls for disposal assertions and inject synthetic raw values without timers, focus, or cloud submission.

- [x] Tests retain a replaced row control across an explicit refresh and prove it was disposed; closing each status window disposes its refresh timer.
- [x] Tray-context tests inject every raw-value class into state/diagnostics and prove rendered rows remain raw-free.
- [x] Remove the direct `ProjectFileProtectionStatusInspector.Inspect` call from the tray view factory. Publish project-file protection into the resident snapshot first, then render only that snapshot so the status form complies with the sole-state-owner rule.

## 295. Make second-instance activation proof match Windows foreground rules

**What to build:** Replace the environment-sensitive expectation that Windows must foreground an existing tray window with a deterministic activation seam. The product must still attempt activation and always show a raw-free local outcome when foregrounding is refused.

**Blocked by:** None - can start immediately.

**State owner:** The single-instance activation result returned by `SingleInstanceEnforcement`; the second-launch notification projects that result.

**Fail-closed state:** An absent, stale, inaccessible, or foreground-refused window publishes `activation_succeeded=false` and exits without starting a second hook-owning resident instance.

**Allowed transitions:** A detected existing mutex triggers one activation attempt, followed by either `activation_succeeded=true` or the raw-free fallback notification. Neither result enters the normal tray message loop.

**Deterministic proof:** Injected activation-window operations return accepted/refused outcomes without relying on `SetForegroundWindow`, interactive desktop focus, or timing.

- [x] Refactor the single-instance activation dependency behind an injectable seam and cover both accepted and foreground-refused outcomes deterministically.

## 296. Keep protected Send usable and explain its safety state

**What to build:** Make the configured Send binding complete one protected Send
even when composer inspection outlives the low-level hook budget. The tray menu
must show the configured protected profile and binding, the emergency bypass
combination, and a clear raw-free reason plus next action whenever setup is not
saved, verification did not succeed, or local protection needs repair.

**Blocked by:** None - can start immediately.

**State owner:** The resident protection snapshot owns configured profile,
binding, readiness, and the last guarded result. The hook only captures and
suppresses a potential Send; the tray only projects the published snapshot.

**Fail-closed state:** A selected-app Send whose setup is absent, whose
classification fails, or whose local protection is unavailable remains blocked.
The only raw-send route is the visible emergency bypass
`Ctrl+Alt+Shift+Pause`; ordinary typing and unrelated application input remain
pass-through.

**Allowed transitions:** A potential configured Send is either passed through
as normal input, suppressed then completed once after a verified guarded result,
or suppressed with a published raw-free setup or verification remedy. A delayed
classification may publish one final guarded result, never a second Send.

**Deterministic proof:** An injected delayed-classification seam and fake
resident runtime prove one protected completion without a live desktop client,
timers, or cloud submission. Tray-context tests prove public menu wording for
ready, setup-required, verification-unavailable, and local-repair states.

- [x] A configured keyboard Send remains suppressed during slow inspection and
  then starts exactly one protected flow when inspection completes; injected
  replay input is not recaptured.
- [x] A slow failed or unverified inspection remains fail-closed and publishes
  a raw-free state rather than silently dropping the request or sending raw
  content.
- [x] The tray menu shows `Ctrl+Alt+Shift+Pause` as the emergency bypass and
  gives a readable profile/binding summary when protected.
- [x] When setup is missing, a binding is not saved, verification is
  unavailable, or local DPAPI protection needs repair, the menu names the
  condition in plain language and directs the user to setup or repair.

## 297. Make all deferred and pointer Send decisions atomic and fail-closed

**What to build:** Close the remaining resident-state race around deferred
keyboard work and the mouse Send discovery gap. A selected desktop-client Send
must never reach the cloud raw because a button cannot be classified quickly,
and work captured before stop or runtime replacement must not invoke an old
runtime.

**Blocked by:** 296. Keep protected Send usable and explain its safety state.

**State owner:** The resident protection snapshot owns the active runtime and
the selected target identity. Hook callbacks may request work only while that
snapshot remains current; the tray remains a projection.

**Fail-closed state:** An unclassified click in a selected client, a stale
snapshot, a changed target window, or a runtime replacement blocks the original
Send and publishes a raw-free remedy. Ordinary unrelated clicks remain normal.

**Allowed transitions:** A click or key gesture is bound to one resident
generation and target. It either completes one guarded send for that same
generation, or it is rejected without a cloud submission when the identity or
runtime changes.

**Deterministic proof:** Injected pointer discovery and a controllable resident
snapshot replacement prove that no old runner executes, no raw pointer send
passes through, and unrelated input is not captured.

- [x] A pointer Send whose control discovery is slow or fails is fail-closed
  only for the selected desktop client; unrelated application clicks remain
  pass-through.
- [x] Deferred keyboard and pointer results cannot call a runner after Stop or
  resident-runtime replacement, including the compare-and-send boundary.
- [x] Tests cover two desktop windows with the same profile and prove the send
  cannot be redirected to the second window.
- [x] If the profile store cannot be read, the tray shows a raw-free settings
  recovery action rather than offering a setup action that cannot run.

**Review follow-up (2026-08-06):** Invalidation by Stop or runtime replacement
preserves one terminal raw-free `terminal_blocked` trace for a registered
operation. Deferred classification failures now create an operation-owned
raw-free terminal outcome, and keyboard suppression uses a resident
window/process binding verdict without waiting for UI Automation in the
low-level callback. The remaining pointer first-click/cache-miss proof is
tracked in ticket 314; until it is complete, pointer protection is not claimed
for an unknown target.

**Completed follow-up (2026-08-07):** A deterministic pointer timeout/failure
test proves that a cached selected window/process target is suppressed without
submission, while a different application's click passes through. The test
does not broaden the claim to an unknown first-click target; that remains
explicitly blocked by ticket 314.

## 298. Publish a raw-free protected-Send attempt status

**What to build:** Make each configured keyboard Send attempt visible in the
tray's resident status so a user can tell whether Code Sanitizer detected the
gesture, is checking it, sent a safe result, or blocked it with an actionable
reason. The status must never contain prompt text, dictionary terms, mappings,
paths, or exception details.

**Blocked by:** 296. Keep protected Send usable and explain its safety state.

**State owner:** The resident protection snapshot owns the lifecycle of one
protected Send attempt. The native hook and UI publish no independent local
flags; the tray only displays the snapshot.

**Fail-closed state:** An incomplete, failed, stale, unverified, or cancelled
attempt leaves the original Send blocked and publishes one stable raw-free
reason. It must never be reported as sent.

**Allowed transitions:** `detected` -> `checking` -> `sent_safely` or one
terminal raw-free blocked state. A repeated key press while an attempt is in
progress may report `in_progress`, but cannot overwrite the active attempt's
terminal outcome.

**Deterministic proof:** Injected hook classifications and submit runners drive
the real resident snapshot through each transition without a live desktop
client, timers, or cloud submission. Assertions prove public menu/status text
is raw-free.

- [x] A configured `Ctrl+Enter` publishes `detected` and `checking` before the
  protected flow runs, without exposing prompt content.
- [x] Successful safe Send publishes `sent safely`; failed, stale, unverified,
  cancelled, and setup-required outcomes publish distinct plain-language next
  actions.
- [x] The tray summary and local protection status render only the resident
  attempt state, including `in progress`, and never infer success from a
  suppressed key alone.
- [x] Tests cover every terminal transition and synthetic raw values without
  timing, live focus, or cloud access.

## 299. Make prompt-protection setup observable from focus to active protection

**What to build:** Turn `Set up prompt protection` into a visible workflow.
After the user chooses Send and newline keys and begins verification, Code
Sanitizer must show whether it is waiting for focus, recognized a supported
Codex Desktop or ChatGPT Desktop composer, is verifying the binding, is
activating the resident hook, or has reached a terminal result with one clear
next action. The result must remain visible after the setup window closes.

**Blocked by:** None - can start immediately.

**State owner:** The resident protection snapshot owns the setup-verification
lifecycle, recognized profile identity, selected bindings, and terminal result.
The setup form, tray summary, and local-status form only render that snapshot.

**Fail-closed state:** Until the resident runtime confirms activation for the
recognized selected profile, its potential Send remains blocked. A lost focus,
unsupported surface, binding-verification failure, hook-activation failure, or
cancelled setup publishes a raw-free non-ready result and one recovery action.

**Allowed transitions:** `idle` -> `waiting_for_focus` ->
`composer_recognized` -> `verifying_binding` -> `activating_protection` ->
`protected`, or one terminal raw-free failure. A new setup attempt may replace
a terminal result but cannot overwrite an active protected runtime before it is
replaced atomically.

**Deterministic proof:** Injected focused-composer verification and resident
runtime activation drive every state without a live desktop app, timers, or
cloud access. Public UI tests assert the same raw-free state and next action in
the setup window, tray, and local-status view.

- [x] Selecting `Ctrl+Enter`, starting verification, and focusing a supported
  composer visibly advances through waiting, recognition, verification, and
  activation instead of leaving the user with an unexplained dialog.
- [x] A successful setup names the protected app and Send binding, saves them,
  reloads the resident runtime, and remains visibly `protected` after the
  setup window closes.
- [x] Each terminal setup failure names exactly one safe next action and never
  claims that protection is active.
- [x] All rendered setup progress and results remain raw-free and are covered
  without live focus, timing, or cloud submission.

## 300. Replace generic protected-Send blocking text with a specific outcome

**What to build:** When a protected Send is blocked, show its specific
raw-free reason and the matching next action instead of only `Send blocked`.
The explanation must distinguish missing setup, an unrecognized or changed
composer, unavailable verification, unavailable local protection, a duplicate
Send already in progress, cancellation, and the explicit emergency bypass.

**Blocked by:** 299. Make prompt-protection setup observable from focus to
active protection.

**State owner:** The resident protection snapshot owns the protected-Send
attempt result and recommended action. The tray and local-status views render
only that published result.

**Fail-closed state:** Any unrecognized, stale, failed, cancelled, or
unavailable protected-Send result leaves the original message blocked and
publishes a non-success outcome. It must never be displayed as sent safely.

**Allowed transitions:** A protected Send reaches `sent_safely` or one stable
terminal reason. A later attempt gets a new result only after the current
attempt has reached a terminal state; a duplicate press may report in progress
but cannot erase the terminal reason.

**Deterministic proof:** Injected hook classifications, focused-surface
results, local-protection states, and submit runners exercise every terminal
reason. Public tray and local-status text is asserted raw-free with one
matching next action.

- [x] Every blocked protected-Send outcome gives a distinct user-facing reason
  and one next action rather than only `Send blocked`.
- [x] The setup progress/result and protected-Send result agree because both
  are projections of the same resident snapshot.
- [x] Synthetic prompt text, dictionary terms, mappings, paths, and exception
  messages cannot appear in any explanation.

## 301. Atomically replace a protected Send binding and its resident runtime

**What to build:** Replace the current two-step setup handoff with one atomic
candidate activation: prepare and validate the candidate runtime for the newly
verified binding, publish it as the resident runtime, and only then persist the
new binding as active. If preparation, publication, or persistence fails, the
previous protected binding and runtime remain authoritative. Do not create a
period in which a newly selected Send key can pass through without the resident
gate.

**Blocked by:** 299. The setup lifecycle and terminal activation result must
already be observable.

**State owner:** The resident protection snapshot owns the active runtime,
active binding, and setup activation attempt. Persistent profile storage is a
commit target, never an independent source of active protection while a handoff
is in progress.

**Fail-closed state:** Candidate build, runtime replacement, profile save, or
rollback failure leaves selected-app Send blocked and leaves the previous active
binding unchanged. A candidate binding is never reported as protected before
the resident hook protects it.

**Allowed transitions:** `protected(old)` -> `activating(candidate)` ->
`protected(candidate)`, or `protected(old)` -> `activation_failed(old)`.
Only the matching setup-attempt id may complete or roll back its candidate.

**Deterministic proof:** Injected profile stores, runtime factories, and hook
hosts prove that candidate-key input cannot pass raw during handoff; failure at
every boundary retains or restores the previous runtime and binding without
timers, a live desktop app, or cloud submission.

- [x] Candidate runtime is prepared from an uncommitted verified binding and
  guards both the old and candidate Send binding until atomic publication.
- [x] Persistence happens only after resident activation, and every failure
  keeps the previous active configuration authoritative.
- [x] Tests cover candidate creation, hook start, snapshot publish, persistence,
  rollback, stale attempt completion, and no raw candidate Send pass-through.
- [x] A stale setup attempt cannot reload the runtime, write the completion
  marker, or roll back a newer active candidate; rollback-save failure itself
  becomes an explicit fail-closed resident state.

## 302. Record a correlated, raw-free protected-Send trace

**What to build:** Give each guarded keyboard Send one opaque attempt identifier
and a resident trace that proves its complete local outcome. The tray can show
a safe summary of the current or latest attempt, while the product can prove
that one normal safe prompt completed through the guarded path rather than only
that separate components were enabled.

**Blocked by:** None - can start immediately.

**State owner:** The resident protection snapshot owns the active attempt and
its trace. The hook, sanitizer, overlay, runner, tray, and diagnostics only
append or render transitions through that owner.

**Fail-closed state:** If an attempt cannot create, publish, or complete its
trace, the original selected-app Send remains blocked with the raw-free reason
`trace_unavailable`; it is never inferred to be sent safely.

**Allowed transitions:** `send_detected` -> `target_matched` ->
`composer_read` -> `sanitized` -> `send_injected` -> `sent_safely` for a safe
prompt, or one terminal raw-free blocked result. Every transition carries the
same attempt id and snapshot generation. Missing, duplicated, stale, or
out-of-order transitions are rejected.

**Deterministic proof:** An injected selected keyboard Send, composer reader,
sanitizer, and submit runner drive one real resident snapshot through the safe
sequence without timers, desktop focus, or cloud access. Assertions prove no
raw input, dictionary terms, mappings, paths, control names, or exception text
are traceable or rendered.

- [x] The resident snapshot publishes an opaque attempt id, snapshot generation,
  transition code, raw-free target fingerprint, duration, and terminal outcome.
- [x] A guarded safe keyboard Send produces exactly one ordered trace ending in
  `sent_safely`; every trace error blocks the original Send with one stable
  raw-free status.
- [x] The tray/local status renders the trace outcome only as a projection and
  does not retain an independent success flag.
- [x] Tests reject duplicate, skipped, stale, and out-of-order transitions and
  prove raw values cannot appear in trace or public status output.

## 303. Route one keyboard protected Send through a resident operation

**What to build:** Replace the split deferred keyboard path with one resident
operation that owns a suppressed Send from classification through a terminal
result. The low-level hook returns promptly after scheduling work, while the
operation proves the snapshot is still current before every stage and completes
only once.

**Blocked by:** 301. Atomically replace a protected Send binding and its resident runtime; 302. Record a correlated, raw-free protected-Send trace.

**State owner:** The resident protected-Send operation owns its captured target,
snapshot generation, cancellation token, and trace until it reaches a terminal
outcome. The immutable snapshot remains the authority that admits the operation.

**Fail-closed state:** A stale, stopped, replaced, duplicated, or failed
operation leaves the original selected-app Send blocked and finishes as one
raw-free terminal trace outcome. It cannot call an old runner or begin a second
replay.

**Allowed transitions:** A selected configured keyboard Send is either ordinary
input and passed through, or is suppressed once and moves from `send_detected`
through the correlated operation to exactly one terminal result. A duplicate
gesture while active reports `in_progress` without replacing or sending either
attempt.

**Deterministic proof:** A controllable hook host and snapshot replacement seam
exercise normal completion, runtime replacement, stop, duplicate gesture, and
runner failure without timers, live desktop focus, or cloud access.

- [x] The hook schedules one resident operation after suppressing a matching
  selected keyboard Send and never waits for sanitization or UI work.
- [x] The operation revalidates its original generation and target before each
  side effect and cannot invoke a runner after stop or replacement.
- [x] A duplicate Send, stale operation, runner failure, and cancellation each
  result in one raw-free terminal trace outcome and no raw replay.
- [x] Tests prove unrelated applications and configured newline input continue
  to pass through normally.

**Review follow-up (2026-08-06, resolved):** The resident operation now owns
the attempt identifier, target fingerprint, ordered trace, cancellation, and
completion signal. Stop and runtime replacement cancel pending and active
overlay work, drain the operation with a bounded wait, and preserve a
terminal raw-free trace before disposal. Generation checks prevent completion
or trace publication from entering a newer snapshot. Keyboard suppression
uses a resident selected-target/binding verdict and schedules the operation
without waiting for sanitization or UI work. Production has no untargeted
runner; only the explicit test seam may use one.

## 304. Dispatch replacement overlays from one resident UI owner

**What to build:** Make sensitive keyboard Send display its replacement window
through one long-lived resident UI dispatcher and serialized queue. The user
gets an active confirmation window for the current attempt, and Windows focus
failure is visible and fail-closed rather than becoming a hidden or hung dialog.

**Blocked by:** 303. Route one keyboard protected Send through a resident operation.

**State owner:** The resident overlay dispatcher owns queued and displayed
overlay attempts. The protected-Send operation owns the decision returned for
its own attempt; the tray only displays the published trace outcome.

**Fail-closed state:** Dispatcher startup failure, queue failure, unavailable
foreground activation, wrong attempt id, or a disposed resident runtime blocks
the selected-app Send. It cannot fall back to a per-attempt thread or hidden
dialog.

**Allowed transitions:** A sensitive operation records `sanitized` ->
`overlay_created` -> `overlay_foreground_confirmed` -> `approved` or
`cancelled`, or one terminal blocked result. Only one displayed overlay may own
the dispatcher at a time; later attempts remain explicitly queued or blocked.

**Deterministic proof:** A controllable dispatcher and foreground activator drive
approval, cancellation, foreground refusal, dispatcher shutdown, and queued
attempts without real desktop focus, timing assumptions, or cloud access.

- [x] The resident runtime creates one persistent UI dispatcher rather than one
  overlay thread per Send attempt.
- [x] A sensitive prompt reaches an active foreground confirmation window with
  the same attempt id as its trace, while Cancel publishes a terminal blocked
  result and leaves the next Send ready.
- [x] Foreground refusal, dispatcher failure, or stale overlay result blocks
  submission and reports one raw-free recovery action.
- [x] Tests prove the hook callback is never blocked by dialog lifetime and a
  second sensitive attempt cannot receive or complete the first attempt's
  approval.

## 305. Revalidate the captured target before write and replay

**What to build:** Make an approved sanitized prompt return only to the original
composer/window captured for its Send. The product must verify the target and
snapshot immediately before writing text and again before replaying Send, so a
focus switch, runtime change, or look-alike window cannot redirect content.

**Blocked by:** 297. Make all deferred and pointer Send decisions atomic and fail-closed; 303. Route one keyboard protected Send through a resident operation; 304. Dispatch replacement overlays from one resident UI owner.

**State owner:** The protected-Send operation owns the captured target contract
and compares it with the current target through the resident snapshot. The text
writer and submit runner may act only after that comparison succeeds.

**Fail-closed state:** Changed window, composer, selected profile, snapshot
generation, write result, or replay target blocks the attempt without writing
to another window or injecting Send.

**Allowed transitions:** `approved` -> `text_written` -> `send_injected` ->
`sent_safely` is legal only after both target checks succeed. Any mismatch or
failure ends in one raw-free blocked result; no retry can reuse the approval.

**Deterministic proof:** Two same-profile windows, a controllable composer
writer, and a replay runner demonstrate success for the original window and
block focus changes, write failure, replay failure, and snapshot replacement
without live desktop focus, timers, or cloud access.

- [x] Approved text is written only after the original target contract still
  matches and is never written to a second window with the same profile.
- [x] Replay occurs only after a second successful target and generation check;
  injected replay input is not recaptured as a new attempt.
- [x] Target mismatch, text-write failure, replay failure, and stale generation
  leave the original Send blocked and publish a raw-free terminal trace reason.
- [x] Tests cover keyboard and supported mouse-originated attempts through the
  same compare-before-write-and-send boundary.

**Review follow-up (2026-08-06):** The resident operation must expose the
captured target contract to this boundary and revalidate profile, window, and
generation immediately before write and again before replay. A profile-only
match is insufficient for two same-profile windows.

**Implementation (2026-08-07):** The resident target wrapper validates the
captured profile/window on every rediscovery. After a successful write and
verification, the orchestrator now performs one additional rediscovery directly
before trace publication and replay. A same-profile, different-window result is
reported as raw-free `stale_composer`; no text is written to that second window
and no Send is injected. The existing resident operation guard continues to
bind both checks to the original snapshot generation.

## 316. Add a reference-only hook input source for local acceptance

**What to build:** Add a compiled-in local reference-composer input source that
can exercise the hook-owner callback and suppression path for release
acceptance, while being physically ineligible to target Codex, ChatGPT, or any
user-configured surface.

**Blocked by:** 302. Record a correlated, raw-free protected-Send trace; 303.
Route one keyboard protected Send through a resident operation; 304. Dispatch
replacement overlays from one resident UI owner; 305. Revalidate the captured
target before write and replay.

**State owner:** The immutable resident protection snapshot owns the temporary
reference-acceptance state and captured operation. A reference-composer host
owns the non-persisted capability and its local target identity. Tray UI only
renders published state and cannot enable the source.

**Fail-closed state:** A missing/expired capability, any identity mismatch,
attempt to select Codex/ChatGPT or a persisted/custom profile, missing trace
stage, partial replay injection, or failed cleanup is
`reference_source_unavailable` or another terminal raw-free outcome. It starts
no Send operation and cannot produce `sent_safely`.

**Allowed transitions:** Only an explicit release-smoke acceptance scope may
create the compiled-in reference profile, bind it to one local process/root
window/UI thread and invoke one captured gesture. The scope may run a safe,
approve, cancel, or forced-failure case and then invalidates the capability.
No tray, onboarding, profile persistence, policy import, or normal command
path may activate, retarget, or retain the source.

**Deterministic proof:** A local reference composer creates a fresh
in-memory capability and exercises the hook-owner captured-gesture entry,
production overlay lifecycle, target-owned replay boundary, and terminal
trace twice in one process. It uses no cloud service, timing sleeps, raw prompt
capture, or a direct controller/queue/replay shortcut.

- [x] The reference profile and source are compiled in and cannot be created
  from persisted profiles, configuration, policy, tray UI, onboarding, or a
  normal command; Codex/ChatGPT/custom profile identifiers are rejected.
- [x] Each source gesture validates the live capability, process, normalized
  root window, UI thread and fresh nonce before it can reach suppression; scope
  exit, timeout, or cleanup atomically invalidates it.
- [x] Native keyboard/pointer callbacks and the reference source enter the
  same hook-owned captured-gesture seam before selected/unrelated
  classification, suppression, snapshot capture and deferred scheduling.
  Generic injected replay remains ignored by the native callbacks.
- [x] The source has no overlay, writer, replay or direct-send API. It can only
  enter the resident hook-owned dispatch path; ticket 306 proves the production
  `WindowsConfirmationOverlay`, target-owned replay, terminal trace and
  `replay_indeterminate` outcomes through that path.
- [x] Deterministic tests reject selected/custom/persisted profiles and wrong
  process/window/thread/expired capability, preserve normal injected-event
  rejection, and prove a released source can be replaced without retained
  capability state. Ticket 306 owns the twice-run full trace/cleanup proof.

**Implementation (2026-08-07):** `ReferenceOnlyInputSource` is a compiled-in,
non-persistable capability scoped to the current process, normalized root
window and UI thread. The Windows hook host accepts it only while that
capability is live and sends both keyboard and left-pointer gestures through
the same hook-owned captured-gesture handlers as native callbacks. Profile
load/save/onboarding reject the reserved profile, and scope expiry or disposal
revokes the host capability before dispatch.

## 306. Prove the complete path with a local reference composer

**What to build:** Add a local reference composer that runs the shipped Windows
input hook, UI Automation discovery, resident operation, overlay dispatcher,
text writer, and keyboard/mouse replay together. It provides repeatable local
evidence that the complete protected path works without a ChatGPT cloud account
or live cloud submission.

**Blocked by:** 302. Record a correlated, raw-free protected-Send trace; 303. Route one keyboard protected Send through a resident operation; 304. Dispatch replacement overlays from one resident UI owner; 305. Revalidate the captured target before write and replay; 316. Add a reference-only hook input source for local acceptance.

**State owner:** The real resident protection snapshot owns each reference
composer attempt; the fixture only exposes safe observable outcomes and test
controls.

**Fail-closed state:** A missing trace stage, unconfirmed overlay, target
mismatch, text-write failure, or replay failure means the reference composer
records no send and the test fails. A green component mock cannot substitute
for the end-to-end result.

**Allowed transitions:** The fixture supports a safe no-overlay Send and a
sensitive `approve` or `cancel` Send through the same public resident path.
Every test outcome is backed by one terminal attempt trace.

**Deterministic proof:** The acceptance fixture itself is the proof: it runs in
an STA message loop, uses real Windows mechanisms, and records safe sent text
locally without a cloud endpoint or timing sleeps.

- [x] A safe prompt follows one complete trace to `sent_safely` and appears in
  the fixture only after protected replay.
- [x] A sensitive prompt shows the real replacement overlay; approval sends
  only locally verified sanitized text.
- [x] Cancellation, foreground refusal, target change, text-write failure, and
  replay failure prove that neither raw nor sanitized text is sent.
- [x] The fixture can run repeatedly in release smoke with deterministic cleanup
  of hook, UI dispatcher, windows, and temporary storage.

**Implementation review (2026-08-08):** A real local `ReferenceComposerAcceptanceRunner`
now drives the compiled reference-only source through `WindowsNativeSubmitHookHost`,
the resident protected-Send operation, production `WindowsConfirmationOverlay`, UI
Automation read/write, and `SendKeys` replay into a local WinForms composer. Its
Windows tests prove a safe Send, sensitive sanitized Send after foreground-confirmed
overlay approval, cancellation, and a second clean run. The runner now closes the
overlay, hook, and composer when an attempt times out.

**Closure evidence (2026-08-09):** Tickets 317-321 now exercise the same
reference-composer path for foreground refusal, captured-target change,
UIA-write failure, replay failure, and the interactive release matrix. The
dedicated `--reference-composer-release-acceptance` command runs all nine
scenarios twice, renders only raw-free status lines, and fails closed when no
interactive Windows desktop is available. The headless `--product-smoke`
remains separate and is not used as a substitute.

**Review outcome (2026-08-07):** Not closed. An implementation attempt was
discarded after review because a direct test dispatcher bypassed the low-level
hook callback, local replay recorded a send without exercising production
replay, and a fixture dialog stood in for `WindowsConfirmationOverlay`. Such a
fixture would create a false `sent_safely` proof. The closure must instead:

- exercise the hook callback/suppression boundary through a reference-only,
  explicitly marked input source that cannot be enabled for Codex or ChatGPT;
- use the production overlay or a production-owned deterministic automation
  seam that still runs its foreground validation;
- use a real target-owned keyboard and pointer replay boundary, report partial
  injection as an indeterminate fail-closed result, and release pressed
  modifiers before returning;
- validate the resident terminal trace for every scenario and prove cleanup by
  running the fixture twice in one release-smoke process.

## 317. Prove foreground refusal blocks the reference-composer Send

**Related parent:** 306. Prove the complete path with a local reference composer.

**What to build:** Let the local reference composer deterministically exercise
the production overlay's foreground-refusal result. The original suppressed Send
must stay blocked, the fixture must close cleanly, and its terminal trace must
explain the blocked outcome without exposing prompt content.

**Blocked by:** 304. Dispatch replacement overlays from one resident UI owner;
316. Add a reference-only hook input source for local acceptance.

**State owner:** The resident protected-Send operation owns the attempt and its
trace; the production overlay dispatcher owns only foreground activation.

**Fail-closed state:** Foreground activation is unavailable, denied, stale, or
untraceable. The attempt reaches `terminal_blocked` and records no Send.

**Allowed transitions:** `overlay_created` -> `overlay_foreground_confirmed` ->
`approved` is allowed only after foreground success. Any other foreground result
goes directly to the terminal blocked outcome.

**Deterministic proof:** The reference composer drives the production-owned
foreground validation seam without sleeps, then asserts suppression, no sent
text, raw-free terminal trace, and cleanup.

- [x] Foreground refusal produces a raw-free terminal blocked trace and no Send.
- [x] The fixture releases its hook, overlay dispatcher, reference capability,
  and windows after the refusal.

## 318. Prove target change blocks a reference-composer attempt

**Related parent:** 306. Prove the complete path with a local reference composer.

**What to build:** During a local protected Send, make the verified composer
identity change after capture and before a side effect. The attempt must not
write to or replay into the replacement target.

**Blocked by:** 305. Revalidate the captured target before write and replay;
316. Add a reference-only hook input source for local acceptance.

**State owner:** The resident protected-Send operation owns the captured target
and snapshot generation; the reference fixture supplies only the local target
transition.

**Fail-closed state:** The current target differs from the captured target or
cannot be revalidated. The operation records `terminal_blocked` and no local
composer receives a Send.

**Allowed transitions:** A target may remain valid through write and replay, or
become stale and stop the attempt. It may never be rediscovered and redirected
to another composer.

**Deterministic proof:** The reference fixture changes its actual local target
identity at the prescribed boundary and asserts no write/replay, a raw-free
stale-target terminal trace, and deterministic cleanup.

- [x] A target change before write produces no mutation and no Send.
- [x] A target change before replay preserves the original target and produces
  no Send in either local window.

## 319. Prove UI Automation write failure blocks the reference-composer Send

**Related parent:** 306. Prove the complete path with a local reference composer.

**What to build:** Let the local reference composer make its real UI Automation
write path unavailable after approval. The sanitized text must not be treated as
sent and the original suppressed Send must remain blocked.

**Blocked by:** 304. Dispatch replacement overlays from one resident UI owner;
305. Revalidate the captured target before write and replay; 316. Add a
reference-only hook input source for local acceptance.

**State owner:** The resident protected-Send operation owns the transition from
approval to write and its terminal trace. The reference composer owns only its
local UI state.

**Fail-closed state:** UI Automation cannot write or cannot verify the written
text. The attempt terminates blocked and records no Send.

**Allowed transitions:** `approved` -> `text_written` is allowed only after a
verified local write. Write failure goes directly to the terminal blocked state.

**Deterministic proof:** The fixture changes a real local control into a
non-writable state at the write boundary and asserts no sent text and a raw-free
terminal trace without timers or cloud access.

- [x] A write failure after approval sends neither raw nor sanitized text.
- [x] The trace does not contain `text_written`, `send_injected`, or
  `sent_safely` after the failed write.

## 320. Prove replay failure and partial injection fail closed

**Related parent:** 306. Prove the complete path with a local reference composer.

**What to build:** Use the production keyboard and pointer replay boundary to
detect an unavailable or partial local Send injection. The fixture must release
any pressed modifiers, record an indeterminate raw-free outcome, and never
claim that the prompt was sent.

**Blocked by:** 305. Revalidate the captured target before write and replay;
316. Add a reference-only hook input source for local acceptance.

**State owner:** The resident protected-Send operation owns replay and terminal
trace publication. The replay boundary owns the all-or-nothing injection result
and modifier cleanup.

**Fail-closed state:** Replay cannot begin, inserts only part of the configured
gesture, loses the target, or cannot prove completion. The attempt is blocked
with a raw-free `replay_indeterminate` outcome.

**Allowed transitions:** Only a fully verified replay may reach `send_injected`
and `sent_safely`; any incomplete replay becomes terminal blocked.

**Deterministic proof:** The local reference composer forces each replay result
through the production replay boundary and verifies no local sent-text record,
released modifiers, raw-free terminal trace, and cleanup.

- [x] Keyboard and pointer replay failures produce no Send and no success trace.
- [x] Partial injection reports `replay_indeterminate` and releases all
  modifiers before the fixture returns.

## 321. Run the interactive reference-composer release acceptance

**Related parent:** 306. Prove the complete path with a local reference composer.

**What to build:** Provide one explicitly interactive Windows release-acceptance
command that runs the complete local matrix and returns a raw-free report. It
must state when no interactive desktop foreground is available instead of
silently substituting the headless product smoke.

**Blocked by:** 317. Prove foreground refusal blocks the reference-composer
Send; 318. Prove target change blocks a reference-composer attempt; 319. Prove
UI Automation write failure blocks the reference-composer Send; 320. Prove
replay failure and partial injection fail closed.

**State owner:** The resident protection snapshot owns every acceptance attempt;
the release command only starts the fixture and renders its raw-free result.

**Fail-closed state:** The process lacks an interactive desktop, any required
scenario lacks a terminal trace, or cleanup is incomplete. The command returns
failure and the build cannot publish a protected-path acceptance result.

**Allowed transitions:** `not_run` -> `running` -> `passed` requires every
scenario and a second clean run. Any unavailable or failed scenario ends at
`failed_closed`.

**Deterministic proof:** One interactive local run covers safe Send, sanitized
Send, cancel, foreground refusal, target change, write failure, and replay
failure twice without cloud access, sleeps, or raw prompt artifacts.

- [x] The command produces one raw-free result per scenario and one overall
  pass/fail result for the running build.
- [x] A non-interactive invocation reports an explicit unavailable status and
  exits non-zero without claiming protection.
- [x] The acceptance run proves cleanup by executing the full matrix twice in
  one process.

## 322. Make the reference-composer acceptance runner deterministic without weakening production foreground checks

**Related parents:** 306. Prove the complete path with a local reference composer; 317-321. Reference-composer failure and release scenarios.

**What to build:** Deepen the reference-composer acceptance module around one explicit scenario interface. It owns local compositor lifecycle, its controlled foreground adapter, replay-ready focus restoration, and terminal-result collection. Production `WindowsConfirmationOverlay` keeps its Win32 foreground adapter and fails closed when foreground activation cannot be verified. The acceptance adapter can never be selected for Codex or ChatGPT.

**Blocked by:** 316. Add a reference-only hook input source for local acceptance.

**Blocks:** 317. Prove foreground refusal; 318. Prove target change; 319. Prove UI Automation write failure; 320. Prove replay failure and partial injection; 321. Interactive release acceptance.

**State owner:** The acceptance module owns fixture state and adapters only. The resident protected-send operation remains owner of protection state and terminal trace.

**Fail-closed state:** Unavailable fixture focus, foreground proof, target, UIA write, replay result, or terminal trace yields raw-free `terminal_blocked`; it never converts the outcome to success or lets the original Send through.

**Allowed transitions:** `created -> hook_started -> scenario_running -> terminal_result -> disposed`. A scenario moves the local target only at declared pre-write or pre-replay seams. No timer, focus race, or callback creates an implicit transition.

**Deterministic proof:** Run safe, sensitive, and cancel scenarios twice in one process without cloud access. Each must prove original-key suppression, correct production terminal trace, and cleanup. A failed foreground scenario must produce no Send. The fixture is green before its fault scenarios become release evidence.

- [x] A normal local scenario is repeatable without depending on the current desktop foreground owner.
- [x] Production uses the Win32 foreground adapter; only the local reference-composer receives a fixed acceptance adapter.
- [x] Cleanup leaves no live hook, overlay dispatcher, reference capability, or hidden local window.

## 307. Pin a verified ChatGPT Desktop compatibility fingerprint

**What to build:** Turn the currently verified ChatGPT Desktop surface into an
explicit compatibility fingerprint. The profile is protected only when its
package/app version, process/window identity, composer UI Automation shape,
Send/newline pair, and available pre-action Send-control evidence match the
fingerprint; a mismatch is visibly unsupported rather than optimistically
protected.

**Blocked by:** 305. Revalidate the captured target before write and replay.

**State owner:** The resident protection snapshot owns the active compatibility
fingerprint and its verification result. Persistent profile data is a candidate
input and the tray only renders the published result.

**Fail-closed state:** Missing, changed, incomplete, or unverifiable fingerprint
evidence puts the selected ChatGPT surface in `unsupported_surface` and blocks
its configured Send. It must not fall back to a generic profile match.

**Allowed transitions:** `unverified` -> `verifying` -> `protected` for a full
match, or `unsupported_surface` for any mismatch. A new verified fingerprint
may replace an old one only through the existing atomic resident-runtime
activation path.

**Deterministic proof:** Injected app/package, process/window, UI Automation,
binding, and Send-control evidence exercise full match, one-field mismatch,
missing evidence, and atomic fingerprint replacement without desktop focus,
timers, or cloud access.

- [x] The setup and status flows display a raw-free supported or unsupported
  result for the exact ChatGPT Desktop fingerprint.
- [x] Each fingerprint field participates in verification; a changed field
  blocks protection rather than silently matching a broader surface.
- [x] A verified fingerprint is activated and persisted only after its resident
  runtime is active; failed updates preserve the prior protected runtime.
- [x] Tests prove no prompt text, sensitive values, paths, UI names, or exception
  details are stored or shown with compatibility evidence.

## 308. Gate the ChatGPT Desktop release claim on both acceptance proofs

**What to build:** Make `protected` for the pinned ChatGPT Desktop path a
release-gated claim. The shipped build must have both a passing local reference
composer acceptance run and a repeatable live desktop contract run that records
one complete raw-free trace for the pinned keyboard Send binding.

**Blocked by:** 306. Prove the complete path with a local reference composer; 307. Pin a verified ChatGPT Desktop compatibility fingerprint.

**State owner:** The resident protection snapshot owns the current compatibility
claim and the safe evidence result for the pinned fingerprint. Release tooling
may publish that evidence but cannot derive the release claim `protected` from
separate flags. Resident Send admission is owned separately by local readiness
and the active verified target; see ticket 340.

**Fail-closed state:** Missing, failed, stale, build-mismatched, or fingerprint-
mismatched evidence makes the release compatibility claim `unsupported_surface`
or `degraded`; it cannot display the pinned release path as protected. This
release-evidence state does not independently block a resident target that has
passed its local fail-closed admission checks.

**Allowed transitions:** A shipped build moves from `unproven` to `protected`
only after both required proofs pass for the same build and fingerprint. A later
fingerprint or build change returns it to `unproven` until both proofs are run
again.

**Deterministic proof:** Release tests validate evidence pairing, build and
fingerprint matching, expiration on change, raw-free public rendering, and
rejection of a missing live-contract result. The live run itself is a documented
repeatable operator acceptance procedure, not an automated cloud assertion.

- [x] Release smoke runs the reference composer acceptance fixture and fails
  when any required trace stage or terminal result is absent.
- [x] The live ChatGPT Desktop contract procedure records one raw-free keyboard
  Send trace and exact fingerprint/build pairing without recording prompt text.
- [x] Release claim status calls the pinned ChatGPT path protected only when
  both evidence records match the running build and fingerprint.
- [x] Changed app version, binding, UI Automation shape, missing live evidence,
  or missing reference evidence visibly downgrades the release claim; resident
  Send remains governed by its own active-target and local-readiness checks.

**Implementation (2026-08-09):** Added the paired reference/live proof
evaluator and raw-free proof store. `--product-smoke` now runs the complete
18-scenario reference-composer release matrix and fails unless every terminal
trace is present and raw-free. `--reference-composer-release-acceptance`
records the passing reference proof for the current ChatGPT fingerprint and
build. `--chatgpt-live-contract-arm` arms one explicit live capture; the
resident `sent_safely` trace records the live proof and consumes the arm.
`--chatgpt-protected-claim-status` and release status publish `protected` only
when both records match. Missing, malformed, stale, or mismatched proof remains
degraded as release evidence. The native controller records the claim from the
resident snapshot for diagnostics rather than using proof files as a second
runtime admission gate; every active/reloaded runtime is wired to that snapshot
and candidate profiles cannot use a stale snapshot claim. The standalone CLI
remains diagnostic-only and cannot claim resident protection. Live arm
consumption is single-use and synchronized, with deletion failure remaining
fail-closed. The stored live proof includes the validated raw-free terminal
trace, while the release matrix validates canonical stage order, keyboard and
mouse hook cleanup.
Automated coverage includes pairing, build/fingerprint mismatch, raw-free
persistence, release-claim diagnostics with independent resident admission,
complete trace validation, cleanup, and one-resident-operation live-arm
reservation.

**Regression follow-up (2026-08-10):** The live-contract arm is persistent
release evidence, not a consumable input event. A resident operation may reserve
it for one in-flight capture, but an interrupted, blocked, or failed capture
releases that reservation and leaves the arm available for retry. Only a saved
`sent_safely` proof clears the arm. Claim evaluation projects the persisted arm
as `reference=passed, live=armed`, so the tray cannot lose the ready-to-check
state after a resident refresh.

**Observability follow-up (2026-08-10):** While the live contract is armed,
the local-status view must render the latest raw-free protected-Send attempt
before any generic release-check message. This lets release acceptance
distinguish a hook that never received the configured key from an operation that
reached a specific blocked terminal state, without logging or displaying prompt
text. The message must not turn the release check into a resident Send gate.

The same view must also project the latest safe keyboard-hook boundary signal:
waiting for the configured Send, Enter in an unrelated target, binding mismatch,
or configured Send captured. Unknown values are rendered as unavailable rather
than copied to the UI. The signal is observational only; it cannot permit Send
or alter fail-closed handling.

**Pipeline-failure follow-up (2026-08-10):** A terminal fail-closed operation
must publish an allowlisted, raw-free resident failure code when the local
orchestrator or the resident submit wrapper throws. The status UI must explain
that the original Send remained blocked and distinguish these two cases from a
missing keyboard capture, trace failure, or unsupported surface. It must never
display an exception message, type name, prompt text, or arbitrary diagnostic
value. Deterministic tests inject each known code and an arbitrary value.

## 309. Trace protected pointer Send through the resident operation

**What to build:** Apply the same correlated, raw-free resident attempt trace to
an identified mouse Send in a selected AI app. Pointer Send must not be reported
as protected merely because the keyboard path is traced.

**Blocked by:** 302. Record a correlated, raw-free protected-Send trace; 304.
Dispatch replacement overlays from one resident UI owner.

**State owner:** The resident protection snapshot owns the pointer attempt,
target identity, transitions, and terminal result. Pointer classification only
starts or suppresses the operation.

**Fail-closed state:** Missing, stale, or failed pointer trace blocks the
original mouse action with `trace_unavailable`; unrelated and non-Send clicks
remain pass-through.

**Allowed transitions:** The pointer path uses the same ordered trace and
terminal states as keyboard Send, with no post-hoc synthetic success trace.

**Deterministic proof:** A local selected-composer fixture drives identified
Send, unrelated click, target change, overlay cancellation, approval, and trace
failure without a live cloud or timer.

- [x] Identified pointer Send starts and completes one resident trace before
  any submit side effect.
- [x] Pointer trace failure suppresses the original click and renders a safe
  retry reason; unrelated clicks are not intercepted.
- [x] Pointer approval and cancellation share the keyboard overlay contract.

**Review follow-up (2026-08-06, resolved):** Pointer attempts use the same
resident attempt/trace owner and terminal-trace rule as keyboard attempts;
keyboard trace success is never reused as pointer evidence. Missing pointer
identity creates a fresh `send_detected -> terminal_blocked` trace, and child
window handles are normalized to their root before target comparison.

## 310. Remove the untraced runtime compatibility path

**What to build:** Remove the production-like fallback that executes an
untraced runner and appends synthetic trace stages afterward. Every runtime
accepted by the resident controller must provide a traced runner; controller
tests use an explicit traced test seam.

**Blocked by:** 302. Record a correlated, raw-free protected-Send trace.

**State owner:** The resident trace owner decides whether a runner is eligible
to submit; no runner may infer trace stages from a completed result.

**Fail-closed state:** A missing traced runner produces `trace_unavailable` and
never invokes the submit side effect.

**Allowed transitions:** `unverified` -> `trace_unavailable`, or a complete
pre-side-effect trace -> its terminal outcome. Synthetic post-submit stages are
not allowed.

**Deterministic proof:** Runtime construction tests prove every production
factory supplies a traced runner, while a missing-runner fixture proves no
submit call occurs.

- [x] Production runtime construction requires traced runners.
- [x] Legacy synthetic trace fallback is removed from the production path.
- [x] Tests cover missing traced runner and preserve raw-free diagnostics.

**Review result (2026-08-06):** Resolved. Production runtime no longer
stores an untraced `Runner`; production constructs only the target-aware
resident runner. Only `NativeSubmitRuntime.CreateTest` closes over an
untargeted test runner. The production controller constructor no longer
accepts a raw runner; controller-only fixtures use the explicit
`TrayProtectionController.CreateTest` seam. An incomplete fixture is rejected
at startup with `trace_unavailable`. The resulting status is projected into
resident state, and the lifecycle lease has a deterministic cancellation
blocking test at the side-effect boundary.

## 311. Type and centralize protected-Send trace transitions

**What to build:** Replace repeated string pairs used for trace stages and
result codes with one small domain contract and one resident append/failure
helper. This keeps the state machine readable as it grows and prevents callers
from inventing unvalidated transition tokens.

**Blocked by:** 302. Record a correlated, raw-free protected-Send trace.

**State owner:** The resident trace component owns the typed transition and
  validates it before publishing.

**Fail-closed state:** An unknown transition or result code is rejected and
  leaves the protected Send blocked with `trace_unavailable`.

**Allowed transitions:** The existing trace graph remains unchanged; only the
  representation and publication seam are consolidated.

**Deterministic proof:** Compile-time construction plus transition tests cover
  every valid stage, invalid token, duplicate, stale, and out-of-order event.

- [x] Trace stage/result pairs use a typed domain contract.
- [x] Repeated fail-closed publication logic is centralized.
- [x] Tests prove the public trace remains raw-free.

## 312. Preserve an interrupted Send outcome without mutating a newer runtime

**What to build:** Make a protected-Send attempt that loses its resident
snapshot during runtime replacement leave an explicit raw-free outcome for the
user, while never writing the old attempt into the newer runtime generation.

**Blocked by:** 302. Record a correlated, raw-free protected-Send trace; 303.
Route one keyboard protected Send through a resident operation.

**State owner:** The resident lifecycle owns the current generation; the
interrupted operation may report only through a generation-aware outcome
handoff. The newer snapshot remains authoritative for future Send decisions.

**Fail-closed state:** A stale operation cannot alter the newer runtime, cannot
submit, and cannot make the new runtime appear protected because of the old
attempt. The user receives a raw-free retry reason.

**Allowed transitions:** `active(old generation)` -> `interrupted` is recorded
  only through a generation-safe handoff; the new generation continues from
  its own state. No old trace entry is appended to a new attempt.

**Deterministic proof:** A controlled runtime replacement during each trace
stage proves that the old operation is blocked, the new runtime is unchanged
apart from the safe interruption summary, and no prompt data crosses the
boundary.

- [x] A stale trace failure cannot write `trace_unavailable` into a newer
  runtime snapshot or attempt.
- [x] The interruption remains visible as a raw-free recovery state without
  claiming the new runtime completed the old Send.
- [x] Tests cover replacement during detection, checking, overlay, write, and
  replay stages.

## 313. Prove runtime replacement at every protected-Send stage

**What to build:** Extend the deterministic resident-operation test seam so a
controlled runtime replacement can be requested at detection, checking,
overlay, text-write, and replay stages. Prove that every stale continuation is
blocked and that the replacement publishes only the raw-free interruption
summary from ticket 312.

**Blocked by:** 312. Preserve an interrupted Send outcome without mutating a
newer runtime.

**State owner:** The resident protected-Send operation owns the stage callback;
the resident lifecycle owns the replacement generation and interruption
handoff. The test seam must not become a second state owner.

**Fail-closed state:** A replacement at any listed stage suppresses the stale
continuation, performs no old-generation submit or replay, and leaves the new
runtime usable only for a new protected Send.

**Allowed transitions:** A stage callback may request one controlled runtime
replacement; the old operation may only terminate as interrupted. The new
generation may publish its safe interruption summary and may start a separate
attempt later.

**Deterministic proof:** Parameterized tests run without timers, UI dialogs, or
cloud services and assert generation identity, attempt identity, no old trace
entries in the new snapshot, no stale side effects, and raw-free status for
each stage.

- [x] Detection replacement is covered.
- [x] Checking replacement is covered.
- [x] Overlay replacement is covered.
- [x] Text-write replacement is covered.
- [x] Replay replacement is covered.

## 314. Prove the first pointer Send before UI Automation classification

**What to build:** Close the remaining mouse-hook gap where the first click
arrives before the resident target verdict or send-control evidence has been
cached. The pointer path must decide from resident, precomputed evidence and
must not wait for UI Automation in the low-level callback.

**Blocked by:** 297. Make all deferred and pointer Send decisions atomic and
fail-closed; 309. Trace protected pointer Send through the resident operation.

**State owner:** A resident pointer-target evidence owner publishes the
selected/unrelated verdict and the verified Send-control identity. The low-level
mouse callback only performs bounded lookup by normalized root window and
process identity.

**Fail-closed state:** A selected-client pointer Send with missing or stale
evidence is suppressed and ends as `trace_unavailable`; an unrelated click is
passed through. No live window text, process, or UI Automation lookup is
allowed in the callback.

**Deterministic proof:** A controlled target-evidence fixture covers the first
click after focus change, a child-to-root window transition, slow UI
Automation, an unrelated click, and Stop/runtime replacement without timers,
live cloud access, or raw prompt data.

- [ ] First pointer Send is decided from resident evidence before the mouse
  callback returns.
- [ ] Slow or unavailable UI Automation cannot pass a selected-client Send or
  consume an unrelated click.
- [ ] Tests prove the evidence generation and normalized target identity are
  carried into the resident pointer operation.

**Regression follow-up (2026-08-10):** The shipped global low-level mouse hook
classified every left click in a selected ChatGPT window. A slow UI Automation
lookup then suppressed navigation and other non-Send controls. Native pointer
registration is disabled in the production profile until this ticket provides
resident pre-action Send-control evidence; the resident status must state that
only keyboard Send is protected. Completion requires a deterministic proof that
the actual Send button is suppressed without blocking `Ctrl+C`, chat/project
navigation, skill controls, or any other click outside its verified boundary.

## 315. Make protected-Send trace publication transactional

**What to build:** Remove the remaining split between appending a transition
to the resident operation and publishing that transition into the immutable
protection snapshot. A concurrent generation change must have one explicit
handoff result, never a trace that is silently left only in a completed
operation.

**Blocked by:** 303. Route one keyboard protected Send through a resident
operation; 312. Preserve an interrupted Send outcome without mutating a newer
runtime.

**State owner:** The resident protected-Send operation owns the trace draft;
the resident lifecycle owns the generation-safe publication handoff. No tray
fallback may append a transition on behalf of the operation.

**Fail-closed state:** If append and publication cannot commit to the same
generation, the original Send remains blocked and the handoff publishes one
raw-free `trace_unavailable` outcome or preserves the already-published
terminal trace.

**Deterministic proof:** A controlled CAS invalidation is injected between
each append and publication attempt. Tests prove no transition appears in a
new generation, every interrupted operation has an explicit outcome, and no
raw prompt data is retained.

- [x] Append and snapshot publication use one transactional resident seam or
  an equivalent generation-safe handoff.
- [x] Invalidation between append and publication cannot lose the terminal
  raw-free outcome.
- [x] Tests cover normal, Stop, reload, and same-runtime generation changes.

**Implementation (2026-08-07):** Trace appends now commit their operation draft
only through the same compare-and-swap that publishes the resident snapshot.
Publication failure leaves the draft unchanged; the operation can then publish a
single raw-free terminal outcome. Deterministic transaction tests cover failed
and successful publication, while the existing lifecycle suite covers normal,
Stop, reload, and same-runtime snapshot changes.

**Completed follow-up (2026-08-07):** Deterministic boundary tests now start
real Stop and runtime reload between trace-draft preparation and the
publication CAS. Lifecycle cancellation is visible before the callback may
continue; a captured operation is carried into the successor snapshot even if
the callback completes first. Normal publication rejects a cancelled
operation, while the terminal handoff may publish one raw-free outcome. The
cancellation request and CAS share one publication gate, so a non-terminal
trace cannot be published after cancellation. The tests prove the original
Send is not submitted and every trace entry retains the source, never the
replacement, generation; a test-only successful-CAS observer also proves that
no cancelled intermediate transition was published before the terminal
outcome. A pre-handoff terminal publication is permitted and retained.

## 323. Make ChatGPT compatibility fingerprints explicitly opaque

**What to build:** Replace the ambiguous raw-looking fields of
`SurfaceCompatibilityEvidence` with names or a small value type that state they
contain opaque fingerprints. Hash at one defined producer boundary and avoid
rehashing a value merely to expose it in raw-free diagnostics.

**Related to:** 307. Pin a verified ChatGPT Desktop compatibility fingerprint.

**State owner:** The compatibility-evidence module owns the representation and
raw-free rendering of an opaque fingerprint. Producers supply source evidence;
consumers compare the opaque representation only.

**Fail-closed state:** An invalid, missing, or unrecognised opaque fingerprint
does not compare equal and puts the selected ChatGPT profile in
`unsupported_surface`.

**Allowed transitions:** Source evidence is converted once into an opaque
fingerprint during verification. Persistence and runtime comparison preserve
that value without another representation change.

**Deterministic proof:** Tests verify a full match, one-field mismatch,
round-trip persistence, and raw-free diagnostics without desktop focus, timers,
or cloud access.

- [x] Field names or types state that their contents are opaque fingerprints.
- [x] Each source value is fingerprinted at exactly one boundary.
- [x] Raw-free rendering and runtime comparison use the same stored fingerprint
  representation.

**Implementation evidence (2026-08-15):** `OpaqueFingerprint` validates the
stored 64-hex representation, source evidence is converted once at the
compatibility boundary, and comparison/raw-free diagnostics return the stored
value without rehashing. Missing or incomplete evidence is fail-closed through
`unsupported_surface`. Diagnostic rendering of incomplete evidence does not
manufacture a replacement fingerprint. Full suite: `1726/1726`.

## 324. Centralize the verified ChatGPT discovery fixture schema

**What to build:** Provide one test/support builder for a complete verified
ChatGPT Desktop discovery result, with explicit safe overrides for mismatch
cases. Product smoke and all tests use it instead of repeating the fingerprint
diagnostics dictionary.

**Related to:** 307. Pin a verified ChatGPT Desktop compatibility fingerprint;
323. Make ChatGPT compatibility fingerprints explicitly opaque.

**State owner:** The fixture builder owns the canonical synthetic evidence
schema. Individual tests own only their named overrides.

**Fail-closed state:** A builder result missing a required fingerprint field is
an explicit test fixture failure; production code remains fail-closed for the
same missing field.

**Allowed transitions:** A new required field is added once to the canonical
fixture and individual tests may override it to prove rejection.

**Deterministic proof:** A schema-completeness test ensures that the canonical
fixture can create a protected profile, while each one-field override produces
`unsupported_surface` without desktop focus, timers, or cloud access.

- [x] Product smoke and tests no longer repeat a complete ChatGPT fingerprint
  diagnostics dictionary.
- [x] The builder exposes only intentional, named overrides for negative cases.
- [x] Adding a required evidence field breaks one canonical fixture test rather
  than silently desynchronising multiple copies.

**Implementation evidence (2026-08-15):** `ChatGptDiscoveryFixtureBuilder`
owns the complete synthetic schema and named missing/send-control overrides;
product smoke and ChatGPT tests use the same builder. `--self-test` and
`--product-smoke` passed.

## Manual desktop acceptance gate (effective immediately)

No new manual Codex/ChatGPT Desktop acceptance run may be started until tickets
325 through 329 are complete. Tickets 325 through 329 are now complete in the
current source tree. Manual acceptance remains admitted only through the
resident-proof gate and still cannot claim release readiness without the
separate release/CI evidence.

## 325. Own operational readiness state and write a raw-free journal

**What to build:** Give every desktop action needed for Code Sanitizer to become
or remain operational one resident-owned lifecycle: first-run setup, selected
profile verification, recovery/retry, local readiness, and any local release
prerequisite. The same owner must publish a durable, bounded raw-free journal
that lets support identify the exact failed stage.

**Blocked by:** None - can start immediately.

**State owner:** The resident operational-action lifecycle owns the correlation
identifier, action kind, stage, cancellation, retry eligibility, terminal
outcome, and journal publication. Tray and status windows only project the
published state.

**Fail-closed state:** A required action that is idle before automatic start,
running, failed, cancelled, or missing a journal outcome leaves protected Send
blocked for the selected profile. No tray-local flag may report protection ready.

**Allowed transitions:** `idle -> running(named stage) -> succeeded | failed(code)
| cancelled`; retry starts a new correlated action. Only the lifecycle owner may
publish these transitions.

**Deterministic proof:** A deterministic lifecycle seam drives automatic start,
all terminal outcomes, cancellation, and retry without timers, desktop focus,
or cloud access. Tests assert exactly one correlated raw-free journal record per
terminal action and fail if it contains prompt text, sensitive values, mappings,
paths, window/control names, local addresses, or exception messages.

- [x] One resident action-lifecycle API supplies all operation status instead of
  tray or form-local readiness flags.
- [x] Journal records contain correlation ID, action kind, transition, stage,
  outcome/failure code, duration, attempt count, and build identity only.
- [x] A failed/cancelled/missing action outcome keeps selected Send fail-closed.

**Implementation evidence:** `ResidentOperationalActionLifecycle` is the
resident owner; `OperationalActionJournal` persists bounded safe-token JSONL.
Targeted lifecycle tests and the full `1669/1669` suite cover terminal outcomes,
cancellation, retry correlation, stale attempts, and raw-free records.

## 326. Start required setup and readiness actions automatically

**What to build:** On installation and resident startup, automatically start
the next required first-run setup, profile verification, recovery, or short
local readiness action. The user may provide only the explicitly requested
security input, such as focusing the composer or choosing a Send binding; they
must never need to discover a command or infer an execution order.

**Blocked by:** 325. Own operational readiness state and write a raw-free
journal.

**State owner:** The resident operational-action lifecycle chooses and starts
the next prerequisite from the published protection state. The installer and
tray request startup only; they do not decide which prerequisite is complete.

**Fail-closed state:** If the next prerequisite cannot start or cannot collect
the required user input, selected Send remains blocked with its raw-free action
outcome. The product does not silently fall back to a manual command path.

**Allowed transitions:** Startup selects one next prerequisite and starts it
automatically. A user-required stage may wait for focus/binding input; success
selects the next prerequisite, while failure/cancellation remains retryable but
protected Send stays blocked.

**Deterministic proof:** Tests start a clean installation/runtime state and
prove that the correct next action begins without a tray click or console
command. They cover focus-required verification, retry after failure, and an
already-ready profile without timers, live cloud, or a real desktop.

- [x] No operational prerequisite relies on the user opening a tray command or
  remembering a command-line sequence.
- [x] Startup automatically advances only through verified prerequisite results.
- [x] The one allowed user-input stage is explicit and preserves fail-closed
  Send behavior until it succeeds.

**Implementation evidence:** resident startup schedules first-run setup and
then local readiness without a tray click; attempt guards prevent stale worker
completion from activating protection. Tray retries remain fail-closed.

## 327. Show detailed, usable operational-action progress

**What to build:** Make tray and status UI render the resident action lifecycle
as a readable operation: action name, named current stage, whether input is
automatic or required from the user, elapsed time, cancellation availability,
terminal result, and one safe next action. A long check must never look like a
hung empty window.

**Blocked by:** 325. Own operational readiness state and write a raw-free
journal; 326. Start required setup and readiness actions automatically.

**State owner:** The resident operational-action lifecycle publishes progress;
the tray/status UI is a projection only and owns no readiness decision.

**Fail-closed state:** An unknown, stale, or incomplete progress state cannot
be rendered as `protected`; it is shown as a raw-free blocked/retry state while
the selected Send remains blocked.

**Allowed transitions:** UI updates only follow published lifecycle
transitions. A visible cancel/retry request is routed to the owner and receives
a new published outcome; closing or moving the window does not cancel an action.

**Deterministic proof:** UI tests feed lifecycle states through one dispatcher
seam and assert the visible stage/result/next action, including cancellation and
failure, without timers, foreground focus, or cloud access. They also prove the
window remains usable and does not suppress unrelated navigation, clipboard, or
editing input.

- [x] `checking` is never displayed without a named stage and visible outcome.
- [x] The user can distinguish automatic work from the one input required from
  them and knows exactly what to do next.
- [x] Status UI remains moveable/closeable when no critical decision is pending.

**Implementation evidence:** status projection renders action, stage, input
mode, elapsed time, cancellation, terminal outcome, and next action. The
status and tray-context tests cover running, failure, cancellation, retry, and
unrelated input without timers or live cloud access.

## 328. Separate short installed readiness from release/CI acceptance

**What to build:** Keep the complete reference-composer scenario matrix in the
release/CI acceptance path. The installed tray runs only a short local
readiness action automatically, reports its scoped result, and does not present
the full matrix as an unexplained foreground reference-composer window.

**Blocked by:** 325. Own operational readiness state and write a raw-free
journal; 326. Start required setup and readiness actions automatically.

**State owner:** The resident operational-action lifecycle owns local readiness;
the release/CI acceptance workflow owns the full matrix and its release record.
Neither may use the other result as its own success state.

**Fail-closed state:** A missing, failed, or mismatched local readiness result
keeps the profile blocked. A successful local readiness action cannot claim
release compatibility or substitute for the recorded release/CI matrix.

**Allowed transitions:** Resident startup launches the short local check only.
Release tooling launches the complete matrix explicitly in its CI/release scope.
Both results are correlated and raw-free, but remain separate evidence classes.

**Deterministic proof:** Tests prove the resident path performs only the short
readiness path and the release path performs the complete matrix. They assert
that neither result is accepted as the other, without timers, live cloud, or a
manual desktop.

- [x] Post-install setup never opens the full reference-composer matrix as an
  opaque tray action.
- [x] Local readiness and release/CI evidence have distinct result types and
  cannot be confused in status.
- [x] The complete matrix remains repeatable and raw-free in release/CI.

**Implementation evidence:** tray uses `LocalReadinessWorkflow`; the complete
reference matrix remains in `ReferenceComposerReleaseAcceptanceRunner` and is
not invoked by `WindowsTrayApplicationContext`. Separation is covered by the
source contract, status, product smoke, and full-suite tests.

## 329. Prove the active tray path and reopen manual acceptance

**What to build:** Add the missing same-process proof: start the actual
hook-owning tray runtime, run the installed local readiness action, and prove it
reaches a terminal resident state without deadlock, stale hooks, blocked
unrelated input, or a hung UI. Reopen manual desktop acceptance only after this
proof and tickets 325-328 are complete.

**Blocked by:** 325. Own operational readiness state and write a raw-free
journal; 326. Start required setup and readiness actions automatically; 327.
Show detailed, usable operational-action progress; 328. Separate short
installed readiness from release/CI acceptance.

**State owner:** The resident runtime owns the hook and operational action;
the integration fixture observes only published lifecycle state and raw-free
journal records. The manual-test gate is released only by the completed proof.

**Fail-closed state:** A missing terminal result, hook leak, stale generation,
unrelated-input suppression, UI deadlock, or absent raw-free diagnostic keeps
manual acceptance closed and selected Send blocked.

**Allowed transitions:** The fixture starts one resident tray runtime with one
hook, invokes its automatic/local readiness action, waits deterministically for
a published terminal state, and performs cleanup. Only a passing result enables
the manual-test admission gate.

**Deterministic proof:** The product-level fixture executes in the same process
as the tray and active native hook. It proves success, failure, cancellation,
cleanup, and one unrelated-input pass-through case without timers, live cloud,
or a real ChatGPT/Codex session.

- [x] Same-process tray/hook readiness reaches one observable terminal state.
- [x] The fixture proves no hung UI, stale hook, leaked state, or unrelated
  input suppression.
- [x] Manual desktop acceptance is explicitly blocked before this ticket and
  explicitly enabled only when tickets 325-328 and this proof pass.

**Implementation evidence:** the same-process resident fixture observes the
active hook, runs local readiness, verifies terminal resident state, checks
unrelated-key pass-through and hook cleanup, and records a raw-free proof. The
`--chatgpt-live-contract-arm` command is fail-closed until that current-build
proof and its matching terminal journal record exist; CLI gate tests cover both
blocked and admitted paths.

## 330. Make resident readiness the protected-Send admission decision

**What to build:** A selected app's Send stays blocked until the current
resident local-readiness action has succeeded. A failed, cancelled, missing, or
restarted readiness result must revoke the protected claim before any native
Send flow can proceed.

**Blocked by:** None - can start immediately.

**State owner:** The resident operational-action lifecycle publishes readiness;
the native-submit controller consumes that published admission result.

**Fail-closed state:** Any readiness state other than the current successful
resident result suppresses selected Send with a raw-free status.

**Allowed transitions:** `not_run | running | failed | cancelled -> blocked`;
only `succeeded -> admitted`; starting a new attempt immediately returns to
`blocked`.

**Deterministic proof:** Controller tests exercise every lifecycle terminal
state without desktop focus or cloud access and prove no submit flow is called
while admission is blocked.

- [x] Native selected Send consumes lifecycle readiness rather than a UI-only flag.
- [x] Starting, failing, cancelling, or losing readiness blocks Send immediately.

**Implementation evidence:** resident runtime controllers receive a readiness
admission provider. The active lifecycle result and matching resident proof are
required before a selected Send can leave the hook path; same-process tests
cover blocked-before-readiness and admitted-after-readiness behavior.

## 331. Reject workers when their lifecycle action did not start

**What to build:** Startup and retry paths must not queue setup/readiness work
when the resident lifecycle cannot write its start record. The visible result
must remain a raw-free retry state.

**Blocked by:** None - can start immediately.

**State owner:** The resident operational-action lifecycle owns start success;
the tray only dispatches workers for a returned non-zero attempt.

**Fail-closed state:** A failed lifecycle start leaves Send blocked and does not
run a worker with an uncorrelated attempt.

**Allowed transitions:** `start rejected -> terminal blocked`; `start accepted
-> one matching worker`.

**Deterministic proof:** Inject a journal/start failure and assert no setup or
readiness worker is queued and no wildcard completion is accepted.

- [x] Tray checks lifecycle start results before dispatch.
- [x] Attempt ID zero is never a wildcard for worker completion.

**Implementation evidence:** startup returns without queuing its worker after a
rejected lifecycle start; lifecycle terminal transitions require one positive,
matching attempt ID.

## 332. Bind readiness proof to the active resident hook path

**What to build:** Only the active hook-owning resident controller can record
manual-acceptance readiness proof, after a matching successful lifecycle
terminal record. A lifecycle unit test alone cannot open the manual gate.

**Blocked by:** 330. Make resident readiness the protected-Send admission decision.

**State owner:** The resident tray controller owns proof publication after it
observes both its active runtime and the lifecycle terminal result.

**Fail-closed state:** Missing active hook, stale attempt, failed/cancelled
readiness, or failed proof write keeps manual acceptance closed.

**Allowed transitions:** `local_readiness running -> succeeded -> resident proof
recorded`; all other outcomes clear or retain no proof.

**Deterministic proof:** Same-process fixture covers success, failure,
cancellation, stale completion, unrelated key pass-through, and hook cleanup.

- [x] Lifecycle completion alone cannot admit manual acceptance.
- [x] Active resident runtime records proof only for its matching success.

**Implementation evidence:** proof recording moved from lifecycle completion to
the active tray controller, which requires a ready hook and matching terminal
attempt. Same-process tests cover success, cancellation, stale worker
completion, unrelated input, and hook cleanup.

## 333. Preserve exact composer identity for write and replay

**What to build:** Replacement and replay target the exact composer captured
before interception, not another focused control in the same window.

**Blocked by:** None - can start immediately.

**State owner:** The captured composer identity belongs to the protected-send
operation.

**Fail-closed state:** A changed or unresolvable composer identity aborts write
and replay with a raw-free terminal result.

**Allowed transitions:** `captured identity -> exact identity found -> write`;
any mismatch -> `terminal_blocked`.

**Deterministic proof:** Surface tests simulate another focused control in the
same window and prove it is never selected as a fallback.

- [x] Runtime identity is checked before write/replay fallback.
- [x] Same-window focus change is blocked, not redirected.

**Implementation evidence:** production composer write/replay resolves the
captured runtime-id hash. A different focused control, including one in the
same window, is rejected; only the isolated reference-only fixture retains its
own local test behavior.

## 334. Render terminal readiness and lifecycle results

**What to build:** Local status displays successful, failed, and cancelled
resident action outcomes with one raw-free next action, so automatic work never
looks hung or disappears after success.

**Blocked by:** 330. Make resident readiness the protected-Send admission decision.

**State owner:** The resident lifecycle is the source; status UI is projection only.

**Fail-closed state:** An unknown or incomplete terminal state renders blocked,
never protected.

**Allowed transitions:** Published lifecycle terminal states replace progress in
the status projection.

**Deterministic proof:** Status-view tests cover success, failure, cancellation,
and retry without a desktop.

- [x] Successful local readiness remains visibly recorded.
- [x] Terminal lifecycle outcomes are distinguishable and raw-free.

**Implementation evidence:** status projection renders completed, failed, and
cancelled local-readiness rows, plus terminal lifecycle rows instead of hiding
success.

## 335. Strengthen operational journal token validation

**What to build:** Journal and proof values accept only the defined lifecycle
vocabulary and opaque identifiers, not arbitrary strings that resemble domains,
addresses, or sensitive values.

**Blocked by:** None - can start immediately.

**State owner:** The operational journal owns validation before persistence.

**Fail-closed state:** Invalid journal/proof values are rejected and do not
produce a readiness proof.

**Allowed transitions:** Allowlisted lifecycle fields and opaque IDs are
persisted; unknown values are rejected.

**Deterministic proof:** Tests reject domain-like, IP-like, and arbitrary
free-form values while accepting valid lifecycle records.

- [x] Raw-free validation has field-specific allowlists.
- [x] Journal never persists domain-like or address-like values.

**Implementation evidence:** journal entries require a generated correlation
ID, current build identity, and snake-case lifecycle tokens. Targeted tests
reject domain-like and IP-like fields.

## 336. Unify release-acceptance admission ownership

**What to build:** One release-acceptance workflow owns the reference proof and
live-contract arming path; no command can bypass the resident manual gate.

**Blocked by:** 332. Bind readiness proof to the active resident hook path.

**State owner:** The release-acceptance workflow owns release evidence; the
manual gate owns whether that workflow may begin.

**Fail-closed state:** Missing resident proof blocks every release/manual arm
entry point.

**Allowed transitions:** `manual gate admitted -> reference proof -> live
contract armed`; otherwise a raw-free blocked result.

**Deterministic proof:** CLI/workflow tests prove all entry points use the same
gate and preserve raw-free failure output.

- [x] There is one gated release-acceptance path.
- [x] Direct workflow invocation cannot bypass resident admission.

**Implementation evidence:** the unused alternate `RunAndArm` workflow was
removed. The only user-facing live-contract arming command evaluates the
resident manual gate; the reference matrix remains a separate CI/release
evidence command and cannot arm a live contract.

## 337. Type operational lifecycle vocabulary

**What to build:** Replace free-form lifecycle action/status/stage strings at
the ownership boundary with a small typed vocabulary, while preserving
raw-free serialized records and public status text.

**Blocked by:** 330. Make resident readiness the protected-Send admission decision;
331. Reject workers when their lifecycle action did not start; 332. Bind
readiness proof to the active resident hook path.

**State owner:** The resident lifecycle owns typed transitions and exposes only
valid raw-free projections.

**Fail-closed state:** An invalid transition cannot be represented or persisted.

**Allowed transitions:** Only the declared lifecycle transition table may
produce a new state.

**Deterministic proof:** Lifecycle tests enumerate legal and illegal
transitions without timers or a desktop.

- [x] Lifecycle ownership boundary rejects values outside the raw-free
  lifecycle-token grammar and requires a generated correlation ID/current build.
- [x] Existing journal/status compatibility remains raw-free and tested.

**Implementation evidence:** lifecycle persistence now validates correlation,
build, and lifecycle token fields independently; full suite coverage preserves
the existing serialized status contract.

## 338. Unify automatic OpenAI Desktop onboarding

**What to build:** Setup has no Codex-versus-ChatGPT selector. It automatically
waits for one compatible OpenAI Desktop composer, discovers its internal
surface variant, and activates exactly that detected target after a successful
verification.

**Blocked by:** None - can start immediately.

**State owner:** The setup verification operation owns the one detected target
for an attempt; the tray only projects its published result.

**Fail-closed state:** No compatible focused composer, unsupported focus, or an
incomplete verification leaves Send blocked. An internal profile label must
never require the user to decide which product is running.

**Allowed transitions:** `waiting_for_focus -> compatible composer discovered
-> verified -> activation`; every other discovery result terminates as blocked.

**Deterministic proof:** A discovered Codex or ChatGPT surface is stored as the
one detected target; unsupported discovery stores nothing. UI tests prove no
manual product selector is present and show the waiting status.

- [x] Setup does not present Codex/ChatGPT product selection to the user.
- [x] Automatic setup waits for and verifies one compatible foreground composer.
- [x] The resulting status uses a product-neutral display name and records the
      internal surface variant only in raw-free diagnostics.

**Implementation evidence:** first-run and tray retry publish the same
`awaiting_user_focus` stage before verification. The setup UI names only
`OpenAI Desktop`; it discovers the internal surface after the user confirms the
Send/newline binding, which remains explicit because a guessed binding could
intercept the wrong key.

**Diagnostic evidence:** In build `t1604`, startup completed setup in 80 ms
because a previously protected ChatGPT profile satisfied the global condition,
while later manual attempts followed a different lifecycle. The prior manual
selector approach was therefore rejected and removed.

## 339. Make automatic setup require the active OpenAI Desktop target

**What to build:** Startup distinguishes an active verified target from an
unrelated previously protected internal profile. When there is no current
target, it starts the same automatic waiting flow rather than completing setup
because any old profile exists.

**Blocked by:** 338. Unify automatic OpenAI Desktop onboarding.

**State owner:** Resident setup state owns the verified target identity; the
tray reads and projects that state.

**Fail-closed state:** Missing, unreadable, stale, or unprotected target keeps
Send blocked and requests setup; a protected unrelated profile cannot satisfy
it.

**Allowed transitions:** `no active target -> waiting_for_focus -> detected
target verified -> startup complete`; an incompatible change returns to setup
required.

**Deterministic proof:** A stored protected profile without a current verified
target causes startup setup-required; repeated startup and manual retry use the
same lifecycle records, without timers or a desktop.

- [x] Automatic startup does not confuse an old internal profile with a current
      target.
- [x] Status identifies the current automatic stage and a safe next action.

**Implementation evidence:** `.active_prompt_protection_target` persists only
the detected internal profile ID. Missing, unreadable, or invalid target data
produces setup-required. Candidate activation publishes the target temporarily
and restores the preceding target whenever runtime activation or profile commit
fails. Focused tests cover a protected unrelated profile, a persisted target,
startup setup launch, and rollback.

## 340. Make verified onboarding enable protected Send on the current build

**What to build:** A freshly installed current build that completes OpenAI
Desktop onboarding must make its configured Send binding reach the resident
native-submit path. Release/reference acceptance remains useful diagnostic and
CI evidence, but it must not silently block a user-verified resident target or
require legacy demo commands before the first protected Send can work.

**Blocked by:** 338. Unify automatic OpenAI Desktop onboarding; 339. Make
automatic setup require the active OpenAI Desktop target.

**State owner:** The resident protection admission state owns whether a verified
active target may enter the native-submit path. Release acceptance proof is a
separate diagnostic input and does not independently decide runtime admission.
The tray only projects the published admission state.

**Fail-closed state:** Missing resident readiness, missing active verified
target, changed surface fingerprint, failed hook activation, or failed write /
replay keeps the original Send suppressed. A missing or stale release proof
must not leave a verified current-build Send silently blocked with no actionable
status.

**Allowed transitions:** `setup_required -> waiting_for_focus -> verified
active_target -> resident_ready -> protected_send_active`. Release-proof states
may be `not_run`, `stale`, or `passed` throughout those transitions and are
reported separately; they do not reopen setup or downgrade native-send
admission by themselves.

**Deterministic proof:** Build a current-build fixture with a verified ChatGPT
profile, active target, resident readiness proof, and no reference/live release
proof. Assert that `Ctrl+Enter` enters the native interception path. Separately
prove that each true resident safety failure remains fail-closed and that the
status explains the blocking reason without raw prompt text or a manual
release-command instruction.

- [x] A current-build verified target can activate native protected Send without
      `--os-demo-*`, `--send-mode-enable`, reference-composer, or live-contract
      command prerequisites.
- [x] Release/reference evidence is displayed as diagnostics and CI evidence,
      not as a hidden runtime admission gate for a verified resident target.
- [x] Fresh-install flow exposes one visible progress/result state and reaches
      either `protected_send_active` or a concrete resident safety failure.
- [x] Automated tests reproduce the reported state: verified `Ctrl+Enter`
      profile plus stale/missing release proof still enters native interception;
      missing target/readiness/fingerprint/write/replay proof remains blocked.

**Implementation evidence:** The resident callback now records ChatGPT release
claim diagnostics without suppressing a gesture or downgrading resident state.
`ChatGptProtectedClaimTests` proves both the callback and tray runtime with a
verified `Ctrl+Enter` profile and an empty proof store; `LocalProtectionStatus`
proves stale release evidence is labelled diagnostic-only while an actual
interrupted Send remains retryable. Release compatibility claim evaluation and
the explicit live-capture path remain unchanged.

**Diagnostic evidence:** On installed build `0.1.20260811.t1719`, setup and
resident readiness completed and the active target was `chatgpt-desktop`, but
the stored reference proof belonged to `0.1.20260810.t2128` and no live contract
was recorded. The protected-claim gate therefore suppressed `Ctrl+Enter` before
the replacement overlay could appear.

## 341. Deepen the resident protection runtime behind one published interface

**What to build:** Make the resident protection runtime the single deep module
for prompt-protection lifecycle. Tray UI and Windows input adapters must use a
compact interface for start, reload, stop, captured-gesture handling, and the
published protection snapshot, without learning snapshot mutation, operation
lease, hook activation, or trace-persistence ordering.

**Blocked by:** 340. Make verified onboarding enable protected Send on the
current build.

**State owner:** The resident protection runtime owns the immutable snapshot,
active protected-Send operation, hook lifecycle, reload transaction, and
published raw-free state. The tray is a projection only.

**Fail-closed state:** Candidate construction, hook activation, reload,
operation cancellation, trace persistence, or state publication uncertainty
retains the last complete safe snapshot or suppresses selected Send. No caller
may reconstruct protected state from local flags.

**Allowed transitions:** `stopped -> starting -> active(snapshot)`;
`active(old) -> reloading(candidate) -> active(candidate)`; or
`active(old) -> activation_failed(active old)`. A selected captured gesture is
handled only by its captured active snapshot and reaches one terminal outcome.

**Deterministic proof:** A lifecycle harness injects hook hosts, runtime
candidates, captured gestures, and trace stores. It proves start, reload,
rollback, cancellation, repeated Send, and tray projection without timers,
foreground focus, or cloud submission.

- [x] The runtime exposes one compact production interface; tray and hook
      adapters no longer depend on controller-internal transition methods.
- [x] The current fail-closed snapshot/reload/trace semantics remain unchanged
      and are exercised through the new interface.
- [x] Tests prove that no stale callback, failed candidate, or parallel reload
      can release raw selected-app Send or publish a mixed status.

**Implementation progress (2026-08-15):** `IResidentProtectionRuntime` is now
the only controller-facing boundary used by `WindowsTrayApplicationContext`.
The tray reads its state from the published snapshot and submits lifecycle,
disable, diagnostics, reload, and operation requests through the resident port.
The coordinator now consumes the internal workflow port, while the tray uses a
four-operation UI port plus the immutable snapshot. `ResidentProtectionRuntimeUiPort_...`,
`ResidentProtectionRuntimeWorkflowPort_ParallelReloads...`, failed-reload,
in-flight stale callback, and tray-context tests provide the deterministic
evidence. Ticket 341 is complete; workflow acceptance remains tracked by 345.

## 342. Make the Windows tray a thin projection of resident protection state

**What to build:** Keep the Windows tray usable while reducing it to a UI
adapter that renders the published resident snapshot and submits explicit user
intent. Setup/retry progress presentation, local commands, and tray
composition must be separated internally so the application context no longer
owns protection decisions or workflow ordering.

**Blocked by:** 341. Deepen the resident protection runtime behind one
published interface.

**State owner:** The resident runtime and its operational-action lifecycle own
protection and action state. The tray owns only local UI lifetime and rendering
of published raw-free projections.

**Fail-closed state:** A missing, stale, or unreadable snapshot renders a
non-ready state and offers only safe remediation; the tray cannot mark
protection active from persisted settings or local UI flags.

**Allowed transitions:** User intent is dispatched to the resident runtime;
the tray renders only its resulting `idle -> running -> terminal` action
projection. Closing a non-critical status window does not alter resident
protection.

**Deterministic proof:** UI-adapter tests use a published-state fixture and
intent dispatcher to prove status refresh, retry, cancellation, and window
lifetime without NotifyIcon-dependent timing, a desktop target, or cloud
submission.

- [x] The tray context contains only UI composition and intent dispatch; no
      protection-ready decision is derived in the UI adapter.
- [x] Operational progress and terminal result remain visible, raw-free, and
      usable while ordinary OpenAI Desktop navigation and clipboard use remain
      unaffected.
- [x] Tests prove the tray renders resident state faithfully and cannot enable
      or release Send independently.

**Implementation evidence (2026-08-15):** The tray holds only
`IResidentProtectionRuntime`; the workflow coordinator owns readiness, setup,
retry, recovery, cancellation, and their terminal publications. Status-window
tests cover refresh and lifetime, while `WindowsTrayContext_StoresOnlyTheResidentUiPort`
prevents a future direct controller field. Ticket 342 is complete.

## 343. Separate profile verification from low-level native input handling

**What to build:** Split the native submit area into two real adapters: a
profile adapter for verified bindings and compatibility evidence, and an input
adapter for captured keyboard/pointer gestures and bounded callback fallback.
The low-level input path must not load profiles, inspect storage, or perform
unbounded discovery after input capture.

**Blocked by:** 341. Deepen the resident protection runtime behind one
published interface.

**State owner:** The profile adapter owns persisted binding/evidence validity.
The resident runtime owns the selected-profile snapshot supplied to the input
adapter. The input adapter owns only captured-gesture dispatch.

**Fail-closed state:** Missing, invalid, changed, or unavailable profile
evidence causes the resident runtime to suppress the selected Send with a
raw-free status. A callback failure suppresses only a precomputed selected
target; known unrelated input continues normally.

**Allowed transitions:** `profile evidence changed -> candidate snapshot built
-> candidate hook activated -> snapshot published`; input dispatch uses only
the captured verdict for that generation. Generic replay remains ignored.

**Deterministic proof:** Profile-storage fixtures and captured-gesture fixtures
prove that profile changes cannot affect callback latency, selected/unrelated
classification, replay rejection, or fail-closed behaviour without UIA,
timers, or a cloud submission.

- [x] Profile persistence/verification and input callback code have separate
      production adapters with explicit, minimal interfaces.
- [x] The input adapter consumes only the resident-provided captured verdict
      after callback entry and preserves unrelated-app input.
- [x] Regression tests cover profile reload, selected/unrelated fallback,
      keyboard and pointer Send, reference-only input, and replay rejection.

**Implementation progress (2026-08-15):** The production profile adapter now
builds `NativeSubmitProfileSnapshot` before the hook is started. The callback
uses only that immutable profile status and pending binding; the former
controller path that read setup/profile storage after input capture was
removed. Tests cover `protected`, `native_submit_setup_required`, and
`profiles_unavailable` snapshots plus a failing profile-store adapter. The
remaining acceptance coverage stays open until it proves the complete
keyboard/pointer/reference-only/replay matrix through the resident runtime.

**Acceptance evidence (2026-08-15):** `NativeSubmitProfileSnapshotAdapter`
loads profile status and the ChatGPT live-contract arm before hook startup.
The callback owns neither a profile/proof-store reference nor a storage read;
it only observes the immutable snapshot and resident providers. Missing selected
profiles resolve to `profiles_unavailable`. Targeted tests cover profile
status/arm/missing-profile snapshots (16), pointer dispatch (15), and the
reference-composer/replay matrix (10). Ticket 343 is complete.

## 344. Make the full automated suite independent from an installed tray instance

**What to build:** Isolate `SingleInstanceEnforcementTests` from a real,
already-running Code Sanitizer tray application. Tests must use unique,
per-test instance IDs and activation-window keys rather than the shared
production value `tray`.

**Blocked by:** None. This is a test-environment isolation task and can run in
parallel with the resident architecture work.

**State owner:** Each test fixture owns its generated instance ID and cleans up
only that ID. Production owns the fixed application ID.

**Fail-closed state:** If a test cannot acquire or release its generated mutex
and activation key, it fails without touching the installed tray process.

**Allowed transitions:** `test instance absent -> acquired -> message loop or
second-instance case -> released`. No transition may observe or stop the real
installed instance.

**Deterministic proof:** Run the affected fixture while an installed tray is
running. The fixture passes and the installed process remains alive.

- [x] Replace shared `tray` IDs in single-instance tests with generated test IDs.
- [x] Prove the complete suite runs while the installed tray application remains running.

**Diagnostic evidence:** On 2026-08-14 the full suite reached 1679/1684. The
five failures were all `SingleInstanceEnforcementTests` using the shared `tray`
mutex while `CodexRedactionGate.Tray` was running from the installed path.

**Acceptance evidence (2026-08-15):** `WindowsTrayApp.ProductionInstanceId`
is the only production ID. Each `SingleInstanceEnforcementTests` case generates
its own ID, and `WindowsTrayApp_RunWithTestInstanceIdDoesNotObserveAnUnrelatedRunningInstance`
holds an independent running mutex while the test runtime starts and exits.
The fixture passes 30/30 without acquiring, stopping, or activating the
production tray identity. Ticket 344 is complete.

## 346. Publish immutable resident admission evidence before native callbacks

**What to build:** Replace native-submit callback-time resident providers with
one immutable, generation-bound admission-evidence snapshot. It contains the
selected profile's readiness admission and ChatGPT proof diagnostics. The
resident publishes a replacement snapshot whenever readiness or proof evidence
changes; the hook callback only reads that already-published value.

**Blocked by:** 343. Separate profile verification from low-level native input
handling.

**State owner:** The resident runtime owns the evidence snapshot and publishes
it atomically with the matching runtime generation. The input callback owns no
profile/proof-store access and cannot recalculate admission.

**Fail-closed state:** Missing, stale, mismatched, or unavailable evidence
suppresses the selected Send. Unrelated application input still passes through.

**Allowed transitions:** `resident state/proof change -> evidence snapshot
built off-callback -> matching runtime generation publishes -> callback reads`.
An old worker may not overwrite a newer generation's evidence.

**Deterministic proof:** Inject profile/proof stores that throw when touched
after hook startup. Prove callback handling of keyboard, pointer, and replay
performs zero store reads; prove readiness and proof transitions publish a new
generation and stale publications remain fail-closed, without timers, UIA, or
cloud access.

- [x] Remove callback-time `SetResidentReadinessAdmissionProvider` and
      `SetResidentProtectedClaimProvider` evaluation from the production path.
- [x] Publish an immutable evidence snapshot before hook activation and on each
      resident readiness/proof transition.
- [x] Cover zero-I/O callback handling plus stale-generation and unrelated-input
      behaviour with throwing store fixtures.

**Review finding (2026-08-15):** Ticket 343 removed direct profile and
release-proof store reads from `NativeSubmitInterceptionController`. The current
resident providers are still evaluated on the callback path, and the readiness
provider may read `ResidentOperationalReadinessProofStore`. Track this
separately so the already-completed profile/input boundary remains small while
the stricter no-I/O callback invariant receives a focused proof.

**Implementation evidence (2026-08-15):** `ProtectionSnapshot` now carries
`NativeSubmitResidentEvidence`. Each snapshot is enriched before publication;
only a successful CAS then publishes the immutable evidence to its runtime
controllers. Keyboard and pointer dispatch pass the captured evidence directly,
and the old callback-time providers were removed. Tests cover admitted/blocked
evidence, same-process readiness transition, repeated callbacks after a single
profile-adapter load (with a throwing post-snapshot adapter), stale reload
safety, and unrelated input. Full suite: `1723/1723`; `--self-test` and
`--product-smoke` passed. Ticket 346 is complete.

## 345. Move resident setup, retry, and recovery workflows into one coordinator

**What to build:** A tray user can start setup, retry protected Send, or repair
local protection through one explicit intent. The resident workflow coordinator
runs the complete operation, publishes each raw-free lifecycle stage and the
terminal result, and the tray only renders that published result.

**Blocked by:** 341. Deepen the resident protection runtime behind one
published interface.

**State owner:** The resident workflow coordinator owns workflow ordering,
attempt correlation, runtime candidate activation/rollback, and terminal
publication. `WindowsTrayApplicationContext` owns only WinForms lifetime,
intent dispatch, and rendering of the resident snapshot.

**Fail-closed state:** A coordinator start, candidate build, activation,
rollback, recovery, or terminal publication failure retains the current safe
runtime or blocks selected Send. The tray cannot infer success or start an
uncorrelated worker.

**Allowed transitions:** `intent -> resident running(attempt) -> terminal`
for setup, retry, and recovery. Only the matching active attempt can publish a
terminal result. A stale completion is ignored and cannot roll back a newer
runtime.

**Deterministic proof:** Inject setup/retry/recovery workers, candidate runtime
factories, and a published-state observer. Prove success, activation failure,
cancellation, stale completion, and tray projection without WinForms timing,
desktop focus, or cloud submission.

- [x] Setup, retry, and local recovery workflow ordering move out of
      `WindowsTrayApplicationContext` into one resident coordinator.
- [x] Tray menu/status actions dispatch intents and render published state only.
- [x] Tests prove correlated success/failure/cancellation and stale completion
      behaviour through the coordinator port.

**Implementation progress (2026-08-15):** `ResidentProtectionWorkflowCoordinator`
now owns the setup, retry, and local-protection recovery state machine. The
WinForms tray forwards user intent, performs only the confirmation dialog for
destructive local repair, and renders resident-published state. The former tray
workflow methods and `TrayRemediationActionExecutor` were removed; the retained
`WindowsTrayApplicationContext` tests exercise the coordinator through its
injected background/UI queues. The direct coordinator acceptance matrix remains
open until the success, cancellation, stale-completion, activation rollback,
retry failure, and recovery failure paths are named and tested at the
coordinator boundary.

**Acceptance evidence (2026-08-15):** `WindowsTrayApplicationContext` tests
cover first-run setup success/cancellation, stale and missing setup attempt IDs,
candidate activation rollback, retry failure, successful recovery, failed
recovery, recovery exception, cancellation, and raw-free status projection.
They inject background/UI queues and runtime factories; no cloud target or
desktop focus is used. Ticket 345 is complete.

**Implementation slices (2026-08-15):**

1. **Resident coordinator extraction**
   - **Completed (2026-08-15):** `ResidentProtectionWorkflowCoordinator` owns
     operation single-flight, setup attempt correlation, candidate activation,
     persistence/target rollback, retry activation, and local recovery.
   - **State owner:** the coordinator owns setup/retry/recovery attempt
     correlation, candidate activation and rollback; the tray owns only local
     UI lifetime and user intent dispatch.
   - **Fail-closed:** a worker, candidate, persistence, reload, or terminal
     publication failure leaves the prior resident runtime active when safe,
     otherwise leaves selected Send blocked.
   - **Allowed transitions:** `intent -> running(attempt) -> terminal`; stale
     completion is ignored, and only the active attempt may publish terminal
     state.
   - **Deterministic proof:** injected background/UI queues, setup/retry/
     recovery workers, runtime factories, and a snapshot observer; no timers,
     desktop focus, or cloud submission.
2. **Tray contraction**
   - **Completed (2026-08-15):** tray actions now forward intent to the
     coordinator; the tray retains only user confirmation and rendering hooks.
   - **Blocked by:** resident coordinator extraction.
   - Replace tray workflow methods with coordinator intent calls and render its
     published status/notice only. Preserve the existing setup-complete callback
     as an observer, not as a workflow owner.
3. **Coordinator acceptance matrix**
   - **Blocked by:** tray contraction.
   - Prove setup success, activation failure/rollback, retry failure, recovery
     failure, cancellation, and stale completion. Confirm the tray cannot
     independently mark protection ready or release Send.

## 347. Углубить resident workflow interface и разгрузить TrayProtection

**What to build:** Resident setup, retry, recovery, readiness and runtime
activation are driven through one compact resident workflow interface. The tray
continues to dispatch user intent and project published state, but it no longer
depends on low-level transition primitives or reconstructs protection status.
The refactor must preserve the currently working keyboard protected-Send path.

**Blocked by:** 341. Deepen the resident protection runtime behind one
published interface; 342. Make the Windows tray a thin projection of resident
protection state; 345. Move resident setup, retry, and recovery workflows into
one coordinator; 346. Publish immutable resident admission evidence before
native callbacks.

**State owner:** The resident workflow module owns operation ordering, attempt
correlation, candidate activation/rollback, readiness admission and published
terminal state. The tray owns only WinForms lifetime, explicit user intents and
rendering of the published snapshot.

**Fail-closed state:** A missing or stale workflow operation, an uncorrelated
completion, a failed candidate activation, a failed rollback or an unavailable
published snapshot leaves the current safe runtime active when possible;
otherwise selected Send remains suppressed. The tray cannot mark protection
ready from a local flag.

**Allowed transitions:** `intent -> running(attempt) -> stage updates ->
succeeded | failed | cancelled`; `candidate -> activated -> published` or
`candidate -> rejected -> rollback`. Only the resident workflow owner may
publish terminal state, and stale completions cannot alter a newer generation.

**Deterministic proof:** Use injected queues and adapters to exercise setup,
retry, local recovery, readiness, candidate activation, rollback, cancellation,
stale completion and concurrent reload. Tests must run without timers, desktop
focus, UIA or cloud access and must assert that every published protection
state is a projection of one resident snapshot.

- [x] Replace the broad workflow transition surface with one compact resident
      workflow interface whose operations are user-intent/lifecycle oriented.
- [x] Remove tray decisions about readiness, activation success and terminal
      recovery state; retain only intent dispatch, confirmation and projection.
- [x] Preserve atomic generation/CAS publication and prove no mixed state during
      concurrent reload and callback activity.
- [x] Keep keyboard protected Send behaviour unchanged and pass the full
      automated suite, `--self-test` and `--product-smoke`.

**Completed (2026-08-15):** Added the compact resident workflow port and moved
setup, retry, recovery, readiness, candidate activation/rollback and terminal
publication through the coordinator. Correlated reload checks run inside the
reload gate, stale publications are rejected without changing the snapshot,
and the tray-facing context retains only the resident UI port. Deterministic
tests cover incomplete/stale publication and concurrent whole-generation
reload. Verification: `1733/1733` tests, `--self-test` and `--product-smoke`
passed.

## 348. Выделить ядро protected Send из NativeSubmitInterception

**What to build:** The correlated protected Send operation becomes one deep
module that owns the attempt lifecycle, target revalidation, sanitization,
confirmation, local write, replay and raw-free terminal trace. Windows hook,
profile/compatibility evidence, persistence and product smoke remain adapters
around that seam. The user-visible behaviour and fail-closed guarantees stay
unchanged.

**Blocked by:** 347. Deepen the resident workflow interface and разгрузить
TrayProtection; 323. Make ChatGPT compatibility fingerprints explicitly
opaque; 324. Centralize the verified ChatGPT discovery fixture schema; 346.
Publish immutable resident admission evidence before native callbacks.

**State owner:** The protected Send operation owns the correlated attempt,
target identity, operation stage and terminal outcome. The Windows input
adapter owns only fast captured-input classification and suppression. Profile
and compatibility adapters own evidence construction and persistence. The
resident snapshot remains the only admission source supplied to the callback.

**Fail-closed state:** Any missing stage, target change, foreground refusal,
write failure, replay uncertainty, stale snapshot or incomplete evidence
suppresses the original Send and publishes a raw-free blocked outcome. No
adapter may replay or submit independently of the operation.

**Allowed transitions:**
`send_detected -> target_matched -> composer_read -> sanitized ->
overlay_decision -> text_written -> replayed -> sent_safely | blocked(reason)`.
Safe prompts use an explicit no-overlay terminal branch. Cancel, stale target,
write failure and replay uncertainty terminate the current attempt and leave the
next Send eligible for a new attempt.

**Deterministic proof:** Run the same protected Send interface through the
reference composer and injected Windows adapters. Cover safe and sensitive
prompts, cancel, foreground refusal, target change before write/replay, write
failure, replay unavailable/partial, repeated Send and unrelated input. Tests
must assert raw-free traces, zero callback-time storage reads and no cloud
access.

- [x] Move protected Send stage ordering and terminal trace ownership behind one
      deep operation interface.
- [x] Keep hook, UIA, profile storage, compatibility evidence and smoke code as
      adapters with no independent submit/replay decisions.
- [x] Preserve reference-composer and live compatibility evidence semantics,
      including opaque fingerprint comparison and resident admission.
- [x] Pass the full automated suite, `--self-test`, `--product-smoke` and the
      deterministic reference-composer matrix before any file-ingress work.

**Completed (2026-08-15):** Extracted `ProtectedSendPipeline` and guarded
execution from `NativeSubmitInterception`. The pipeline owns correlated stage
and terminal trace publication, while the hook only classifies/suppresses and
the Windows/UIA path receives the operation's target, trace and execution
guards. Replay trace publication now occurs before the actual submit side
effect; trace failure therefore blocks without sending. Canonical safe and
sensitive traces, repeated sends, unrelated input, raw-free exceptions and
reference-composer failure scenarios are covered. Verification: `1733/1733`
tests, `--self-test`, `--product-smoke` and the twice-run reference-composer
matrix passed.
