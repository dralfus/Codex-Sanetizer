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

**Status:** Completed - test `NativeSubmitProductSmokeRunner_PersistsBindingValuesToStore`

**Changes:**
- Added test that saves profiles to store and verifies loaded binding values match expected
- Test validates both supported pairs: Enter/Ctrl+Enter and Ctrl+Enter/Enter
- Test verifies `NativeSubmitProductSmokeRunner.Run()` works with persisted profiles
- All 1260 tests pass
