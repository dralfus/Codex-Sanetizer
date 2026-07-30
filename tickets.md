---

# Tickets: Codex Redaction Gate - SurfaceMetadata and Native Submit Improvements

All tickets 238-250 completed. The convergence frontier starts with ticket 273.

Work the **frontier**: any ticket whose blockers are all done. For a purely linear chain that means top to bottom.

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
- [ ] When the boundary is unavailable, protected-workspace mode fails closed for that channel and reports `unsupported` rather than silently allowing a direct read/upload/tool-output path.
- [ ] The user experience presents one operation-level batch summary with a navigable per-file list; it does not require accepting a separate popup for every file.
- [ ] Live `project_files_protected` can become `true` only after the real ingress proof and its automated regression test exist.

## 284. Enforce source-whitespace hygiene in the release check

**What to build:** Remove current source whitespace defects and add a lightweight repeatable release check so trailing whitespace and extra end-of-file blank lines do not create noisy diffs or conceal substantive security changes.

**Blocked by:** None - can start immediately.

- [ ] Tracked source and test files have no current `git diff --check` whitespace errors.
- [ ] The documented release verification includes a non-interactive whitespace check that fails before packaging when new defects are introduced.

## 285. Show local protection capabilities and active state in the tray UI

**What to build:** Add a clear local protection-status view reachable from the tray. It must tell the user both which capabilities the installed product contains and which of them are currently active for this Windows session: DPAPI-backed local storage, automatic selected-app prompt protection, and live project-file protection.

**Blocked by:** 281. Recover unreadable local DPAPI protection safely and prevent partial secret writes; 282. Separate broker evidence from live project-file protection status.

**Do not:** Present DPAPI as an unsafe on/off switch; claim that a successful self-test proves production DPAPI readiness; collapse `broker demo`, `unsupported`, and `live protected` into one green file-protection label; or include raw paths, sensitive terms, prompts, mappings, or exception details in the status view.

- [ ] The tray offers a dedicated local status view with separate rows for `Local DPAPI protection`, `Automatic prompt protection`, and `Project-file protection`.
- [ ] Each row shows a stable capability state and an operational state: DPAPI is `ready`, `recovery required`, or `unavailable`; prompt protection is `active`, `setup required`, `degraded`, or `disabled`; project-file protection is `live protected`, `broker demo only`, `unsupported`, or `not configured`.
- [ ] The view explains the immediate consequence of every non-green state and offers only safe relevant actions, such as profile verification, opening recovery, or opening protected-file management.
- [ ] Tray status updates after profile verification, protection enable/disable, DPAPI recovery, and file-policy changes without requiring a restart; tests prove the displayed states remain raw-free and truthful.

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
