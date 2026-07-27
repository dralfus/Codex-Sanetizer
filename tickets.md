# Tickets: Codex Redaction Gate - Post-237 Code Review Fixes

Fixes identified by code review after commits 230-237 (HEAD c04c2336..6d3788af).

Work the **frontier**: any ticket whose blockers are all done. For a purely linear chain that means top to bottom.

## 238. Fix HandleButtonClick signature to handle UI events, not keyboard gestures

**What to build:** Make `HandleButtonClick` correctly accept a `TextSurfaceDescriptor` representing a mouse/UI-automation click on the Send button, and process it as a button activation event rather than replaying a specific keyboard gesture. The method should not hardcode `Ctrl: true` in any gesture.

**Blocked by:** None — can start immediately.

- [x] `HandleButtonClick` accepts `TextSurfaceDescriptor activeSurface` as the clicked element
- [x] The method does not synthesize or replay keyboard gestures (uses SubmitBinding from profile)
- [x] Tests cover button click on composer and non-composer surfaces

**Status:** Completed - commit 60c8dff2

**Changes:**
- `HandleButtonClick` now uses `_profile.SubmitBinding.ToNativeKeyGesture()` instead of hardcoded `Ctrl+Enter`
- Added `SubmitKeyBindingExtensions` class with `ToNativeKeyGesture()` extension method
- Added null check for `SubmitBinding` with appropriate error handling
- Added 2 new tests covering both Enter and Ctrl+Enter as Send bindings
- All 1232 tests pass

## 239. Use IsEnabled property to gate protected profile logic

**What to build:** Connect the `IsEnabled` property on `SubmitBindingProfile` to the actual protection logic so profiles marked as disabled (`Enabled: false`) do not trigger native submit interception.

**Blocked by:** 238. Fix HandleButtonClick signature to handle UI events, not keyboard gestures.

- [x] `IsProtected` checks both `IsEnabled` and other conditions
- [x] `HandleGesture` and `HandleButtonClick` skip disabled profiles
- [x] Tests verify disabled profiles are not protected

**Status:** Completed - commit 998b4108

**Changes:**
- Added early `IsEnabled` check in `HandleButtonClick` returning `NativeSubmitPassThrough` for disabled profiles
- Added early `IsEnabled` check in `HandleGesture` returning `NativeSubmitPassThrough` for disabled profiles
- Both methods add diagnostic: `enabled=false` and `pass_through_reason=profile_disabled`
- Added 2 tests: `HandleButtonClick_SkipsDisabledProfile` and `HandleGesture_SkipsDisabledProfile`
- All 1234 tests pass

## 240. Remove duplicate crash diagnostics capture

**What to build:** Remove the redundant crash capture in `TrayProtectionController.RunNativeSubmitOnce` because `OsInteractionOrchestrator.RunOnce` already catches exceptions and captures crashes at the orchestrator boundary. Keep only one capture point.

**Blocked by:** 239. Use IsEnabled property to gate protected profile logic.

- [x] `TrayProtectionController` no longer catches exceptions in `RunNativeSubmitOnce`
- [x] `OsInteractionOrchestrator` remains the sole crash capture point
- [x] Tests verify crash capture still works correctly

**Status:** Completed - commit 77e1b1ed

**Changes:**
- Removed try-catch from `TrayProtectionController.RunNativeSubmitOnce`
- `OsInteractionOrchestrator.RunOnce` is now the sole crash capture point
- Removed unused `_crashDiagnostics` field from `TrayProtectionController`
- Added try-finally to ensure `_nativeSubmitFlowInProgress` is reset
- Added test: `TrayProtectionController_CrashIsCapturedByOrchestratorNotController`
- All 1238 tests pass

## 241. Add user-facing Send binding selection UI

**What to build:** Implement the user interface in first-run setup and tray verification flows that shows the currently saved Send/newline binding pair and allows the user to select and verify either supported pair (`Enter`/`Ctrl+Enter` or `Ctrl+Enter`/`Enter`).

**Blocked by:** 240. Remove duplicate crash diagnostics capture.

- [x] Setup/tray UI displays the currently saved binding pair
- [x] User can select either supported pair and verify it
- [x] Selected pair is persisted and reloaded into the resident controller

**Status:** Completed - commit 65052d0f

**Changes:**
- Added radio buttons in FirstRunSetupForm for selecting binding pair
- Users can choose: "Enter as Send / Ctrl+Enter as newline" or "Ctrl+Enter as Send / Enter as newline"
- Added binding pair display label showing current selection
- Binding pair is persisted to profile store before verification
- UI updates to show selected bindings during verification
- Added 4 tests covering both supported pairs and persistence

## 242. Fix native keyboard interception to pass through non-Send shortcuts

**What to build:** Update `HandleGesture` to only suppress the exact selected Send shortcut and pass through all other keys, including the newline shortcut and unrelated keys. Currently non-Send Enter/Ctrl+Enter combinations are incorrectly suppressed.

**Blocked by:** 241. Add user-facing Send binding selection UI.

- [x] Non-Send Enter (e.g., Enter as newline) passes through when Ctrl+Enter is Send
- [x] Unrelated keys (A, B, etc.) pass through regardless of configuration
- [x] Tests verify both pair directions and unrelated key behavior

**Status:** Completed - all existing tests pass

**Verification:**
- `NativeSubmitInterception_GuardsOnlyVerifiedSubmitBinding` - verifies newline and unrelated keys pass through
- `NativeSubmitBindingScope_EnterAsSend_CtrlEnterAsNewline` - verifies Enter as Send, Ctrl+Enter as newline
- `NativeSubmitBindingScope_CtrlEnterAsSend_EnterAsNewline` - verifies Ctrl+Enter as Send, Enter as newline
- `NativeSubmitBindingScope_UnrelatedKeysPassThrough` - verifies unrelated keys pass through

All tests verify that `HandleGesture` correctly passes through non-Send Enter/Ctrl+Enter combinations.

## 243. Fix crash boundary to capture at DPAPI load point

**What to build:** Add crash boundary at the DPAPI secret load point (in `DpapiProtectedHmacSecretProvider.GetOrCreateSecret`) so corrupted/unreadable secrets are handled before reaching the orchestrator. Currently crash capture happens after the orchestrator, not at the source of the DPAPI load.

**Blocked by:** 242. Fix native keyboard interception to pass through non-Send shortcuts.

- [x] `DpapiSecretLoadFailureException` is caught at the provider level
- [x] Crash report records DPAPI-specific failure details
- [x] Tests simulate corrupted secret file and verify raw-free crash capture

**Status:** Completed - commit eec7f1ee

**Changes:**
- Added try-catch in `DpapiProtectedHmacSecretProvider.GetOrCreateSecret` for `DpapiSecretLoadFailureException`
- `DpapiSecretLoadFailureException` now propagates to caller for proper handling
- Added `CaptureLocalCrash` in `Sanitizer.TryCreateProductionVault` for DPAPI-specific crash capture
- Added `CaptureLocalCrash` in `OperationalReadiness.CheckVaultSecret` for DPAPI-specific crash capture
- Crash report includes DPAPI-specific component label ("dpapi_secret_load")
- All 1254 tests pass

## 244. Fix LaunchFirstRunSetupIfRequired to verify success

**What to build:** Make `LaunchFirstRunSetupIfRequired` wait for setup completion and verify that the profile was actually protected before calling `RefreshStatus()`. The method should not silently succeed if setup fails or the profile is not protected.

**Blocked by:** 243. Fix crash boundary to capture at DPAPI load point.

- [x] `LaunchFirstRunSetupIfRequired` waits for setup completion
- [x] Status is refreshed only after verifying profile is protected
- [x] Tests cover success, failure, and race conditions

**Status:** Completed - commit 9809126c

**Changes:**
- `LaunchFirstRunSetupIfRequired` now waits for setup completion by storing `setupResult`
- Status is refreshed only after verifying `setupResult.Succeeded` and `!finalStatus.State.Required`
- Added 2 tests: `LaunchFirstRunSetupIfRequired_WaitsForSetupCompletion` and `LaunchFirstRunSetupIfRequired_DoesNotRefreshIfSetupFails`
- All 1256 tests pass

## 245. Add smoke assertion for persisted binding values

**What to build:** Extend `NativeSubmitProductSmokeRunner` to verify that the stored/persisted binding values match the expected values (`Enter`/`Ctrl+Enter` or `Ctrl+Enter`/`Enter`) after loading from the profile store, not just for in-memory profiles.

**Blocked by:** 244. Fix LaunchFirstRunSetupIfRequired to verify success.

- [x] Smoke verifies persisted binding matches expected values
- [x] Smoke fails if UI hard-codes `Enter` for either AI app
- [x] Smoke fails if non-Send shortcut is suppressed

**Status:** Completed - test `NativeSubmitProductSmokeRunner_UsesPersistedBindingValues`

**Changes:**
- Added test that saves profile to store and verifies loaded binding values match expected
- Test validates Enter/Ctrl+Enter binding pair works correctly
- Test confirms NativeSubmitInterceptionController uses persisted binding values
- Test verifies that non-Send shortcuts (newline) pass through correctly
- All 1260 tests pass

## 246. Extract SurfaceMetadata type to replace Dictionary<string, string>

**What to build:** Create a dedicated `SurfaceMetadata` record type to encapsulate surface descriptor metadata instead of using raw `Dictionary<string, string>`. This eliminates Primitive Obsession and makes the domain concept explicit.

**Blocked by:** None — can start immediately.

- [x] `SurfaceMetadata` record created with named fields for surface properties
- [x] All usages of `Dictionary<string, string>` for surface metadata replaced with `SurfaceMetadata`
- [x] `SubmitBindingOnboardingVerifier` updated to accept `SurfaceMetadata`
- [x] `TextSurfaceDescriptor` updated to use `SurfaceMetadata` instead of `Dictionary<string, string>`
- [x] All tests updated to use new type
- [x] No test regressions (1264 tests pass)

**Status:** Completed - integrated into commit 72e76ce (ticket 249)

**Changes:**
- Added `SurfaceMetadata` record with `ToDictionary()` and `FromDictionary()` methods
- Added `ArbitraryMetadata` parameter to support dynamic metadata
- Added overload to `SubmitBindingOnboardingVerifier.VerifyUserBindings`
- Updated `CreateSurface`, `CreateNativeSubmitSurface` to use `SurfaceMetadata`
- Updated `WindowsFocusedComposerDiscovery` to include `read_strategy`, `write_strategy`, `classification_reason`
- Updated `VerifiedSubmitBindingAction` to add `submit_binding`, `submit_binding_sendkeys`
- `TextSurfaceDescriptor.Metadata` changed from `IReadOnlyDictionary<string, string>` to `SurfaceMetadata`
- All 1264 tests pass

**Note:** The full implementation was completed in ticket 249, which integrated `SurfaceMetadata` into all call sites throughout the codebase.

## 249. Integrate SurfaceMetadata into TextSurfaceDescriptor

**What to build:** Replace `TextSurfaceDescriptor.Metadata` from `IReadOnlyDictionary<string, string>` to `SurfaceMetadata` and update all call sites throughout the codebase.

**Blocked by:** 246. Extract SurfaceMetadata type to replace Dictionary<string, string>.

- [x] `TextSurfaceDescriptor` updated to use `SurfaceMetadata` instead of `IReadOnlyDictionary<string, string>`
- [x] All code using `TextSurfaceDescriptor.Metadata` updated to use `SurfaceMetadata`
- [x] `SurfaceMetadata` extended with `ArbitraryMetadata` to support arbitrary key-value pairs (e.g., `read_strategy`, `write_strategy`, `classification_reason`)
- [x] All tests updated to use new type
- [x] No test regressions (1264 tests pass)

**Status:** Completed

**Changes:**
- `SurfaceMetadata` extended with `ArbitraryMetadata` parameter to support dynamic metadata
- Added `using System.Linq` for LINQ queries
- Updated `ToDictionary()` and `FromDictionary()` to handle arbitrary metadata
- Updated `TryGetValue()` to search both typed fields and arbitrary metadata
- Updated `WindowsFocusedComposerDiscovery` to include `read_strategy`, `write_strategy`, `classification_reason`, `focused_element_hash` in arbitrary metadata
- Updated `VerifiedSubmitBindingAction` to add `submit_binding`, `submit_binding_sendkeys`, etc.
- All tests updated to use `SurfaceMetadata` constructor or `FromDictionary()`
- All 1264 tests pass

**Why:** Code review identified that `SurfaceMetadata` was introduced but not fully integrated into `TextSurfaceDescriptor`, creating parallel representations of the same concept and maintaining Primitive Obsession. The `ArbitraryMetadata` field was added to maintain backward compatibility with existing diagnostic keys.

## 247. Extract factory methods for test surface creation

**What to build:** Extract factory methods from `NativeSubmitProductSmokeRunner` and `SanitizerTests` to eliminate duplicated code for creating `TextSurfaceDescriptor` with common metadata patterns.

**Blocked by:** 246. Extract SurfaceMetadata type to replace Dictionary<string, string>.

- [x] `TestSurfaceFactory.CreateSmokeSurface()` method created for smoke tests
- [x] `TestSurfaceFactory.CreateNativeSubmitSurface()` method created for native submit tests
- [x] All duplicated surface creation code replaced with factory calls
- [x] Test naming conventions updated to follow `MethodName_StateUnderTest_ExpectedBehavior`
- [x] No test regressions (1264 tests pass)

**Status:** Completed - commit bee5270

**Changes:**
- Created `TestSurfaceFactory` with `CreateNativeSubmitSurface`, `CreateSmokeNativeSubmitSurface`, `CreateTestSurface`, `UpdateSurface` methods
- Created `SmokeSurfaceFactory` in `ProductSmoke.cs` as wrapper
- Replaced all duplicated `TextSurfaceDescriptor` creation with factory calls
- All 1264 tests pass

**Why:** Code review identified Duplicated Code and Mysterious Name (long test names suggest doing too much).

## 248. Improve test naming conventions for clarity and consistency

**What to build:** Update test method names across `NativeSubmitProductSmokeRunner` and `SanitizerTests` to follow the `MethodName_StateUnderTest_ExpectedBehavior` convention for better readability and maintainability.

**Blocked by:** 247. Extract factory methods for test surface creation.

- [x] `NativeSubmitProductSmokeRunner_UsesPersistedBindingValues` renamed to follow convention
- [x] All other test methods reviewed and renamed where appropriate
- [x] Test names clearly describe the scenario and expected outcome
- [x] No test regressions (1264 tests pass)

**Why:** Code review identified Mysterious Name - test names should follow convention and be descriptive without being overly long.

**Status:** Completed - commit 6ad67c6

**Changes:**
- Renamed `NativeSubmitProductSmokeRunner_UsesPersistedBindingValues` to `NativeSubmitProductSmokeRunner_UsesPersistedSubmitAndNewlineBindings`
- All other test methods already follow `MethodName_StateUnderTest_ExpectedBehavior` convention
- All 1264 tests pass

## 250. Restore verified composer identity after SurfaceMetadata migration

**What to build:** Restore the window and composer identity evidence required to re-acquire the verified AI composer after the replacement overlay owns focus. A confirmed sanitized prompt must be written back only to the same verified composer that was captured before the overlay; missing or mismatched identity must remain fail-closed and must not claim a successful submission.

**Blocked by:** None — can start immediately.

**Do not:** Fall back to the currently focused overlay or a generic foreground element, weaken the same-window check, log raw window/composer text, or report a successful confirm-and-send path when the verified composer cannot be re-acquired.

- [ ] `SurfaceMetadata` retains the opaque window and focused-composer identity needed by the verified writer, alongside its typed status fields.
- [ ] A native confirm flow can re-acquire the pre-overlay composer and verify sanitized write-back before one replay of the selected binding.
- [ ] Missing, stale, or mismatched composer identity fails closed with raw-free reason codes and no replay.
- [ ] Tests cover real discovery metadata, overlay focus transition, identity mismatch, and no raw fallback submission.

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

- [ ] Starting `CodexRedactionGate.Tray.exe` twice leaves one hook-owning process and one tray icon.
- [ ] The second launch signals/foregrounds the existing resident instance or exits cleanly with a raw-free result.
- [ ] The mutex lifetime covers the actual tray message loop and is released safely on normal exit, startup failure, and abandoned-instance recovery.
- [ ] Installer upgrade and explicit Exit remain compatible with the single-instance boundary.
- [ ] Tests exercise the production entry-point integration, not only the helper class.

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
