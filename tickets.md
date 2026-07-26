# Tickets: Codex Redaction Gate - Post-237 Code Review Fixes

Fixes identified by code review after commits 230-237 (HEAD c04c2336..6d3788af).

Work the **frontier**: any ticket whose blockers are all done. For a purely linear chain that means top to bottom.

## 238. Fix HandleButtonClick signature to handle UI events, not keyboard gestures

**What to build:** Make `HandleButtonClick` correctly accept a `TextSurfaceDescriptor` representing a mouse/UI-automation click on the Send button, and process it as a button activation event rather than replaying a specific keyboard gesture. The method should not hardcode `Ctrl: true` in any gesture.

**Blocked by:** None — can start immediately.

- [ ] `HandleButtonClick` accepts `TextSurfaceDescriptor activeSurface` as the clicked element
- [ ] The method does not synthesize or replay keyboard gestures
- [ ] Tests cover button click on composer and non-composer surfaces

## 239. Use IsEnabled property to gate protected profile logic

**What to build:** Connect the `IsEnabled` property on `SubmitBindingProfile` to the actual protection logic so profiles marked as disabled (`Enabled: false`) do not trigger native submit interception.

**Blocked by:** 238. Fix HandleButtonClick signature to handle UI events, not keyboard gestures.

- [ ] `IsProtected` checks both `IsEnabled` and other conditions
-   [ ] `HandleGesture` and `HandleButtonClick` skip disabled profiles
- [ ] Tests verify disabled profiles are not protected

## 240. Remove duplicate crash diagnostics capture

**What to build:** Remove the redundant crash capture in `TrayProtectionController.RunNativeSubmitOnce` because `OsInteractionOrchestrator.RunOnce` already catches exceptions and captures crashes at the orchestrator boundary. Keep only one capture point.

**Blocked by:** 239. Use IsEnabled property to gate protected profile logic.

- [ ] `TrayProtectionController` no longer catches exceptions in `RunNativeSubmitOnce`
- [ ] `OsInteractionOrchestrator` remains the sole crash capture point
- [ ] Tests verify crash capture still works correctly

## 241. Add user-facing Send binding selection UI

**What to build:** Implement the user interface in first-run setup and tray verification flows that shows the currently saved Send/newline binding pair and allows the user to select and verify either supported pair (`Enter`/`Ctrl+Enter` or `Ctrl+Enter`/`Enter`).

**Blocked by:** 240. Remove duplicate crash diagnostics capture.

- [ ] Setup/tray UI displays the currently saved binding pair
- [ ] User can select either supported pair and verify it
- [ ] Selected pair is persisted and reloaded into the resident controller

## 242. Fix native keyboard interception to pass through non-Send shortcuts

**What to build:** Update `HandleGesture` to only suppress the exact selected Send shortcut and pass through all other keys, including the newline shortcut and unrelated keys. Currently non-Send Enter/Ctrl+Enter combinations are incorrectly suppressed.

**Blocked by:** 241. Add user-facing Send binding selection UI.

- [ ] Non-Send Enter (e.g., Enter as newline) passes through when Ctrl+Enter is Send
- [ ] Unrelated keys (A, B, etc.) pass through regardless of configuration
- [ ] Tests verify both pair directions and unrelated key behavior

## 243. Fix crash boundary to capture at DPAPI load point

**What to build:** Add crash boundary at the DPAPI secret load point (in `DpapiProtectedHmacSecretProvider.GetOrCreateSecret`) so corrupted/unreadable secrets are handled before reaching the orchestrator. Currently crash capture happens after the orchestrator, not at the source of the DPAPI load.

**Blocked by:** 242. Fix native keyboard interception to pass through non-Send shortcuts.

- [ ] `DpapiSecretLoadFailureException` is caught at the provider level
- [ ] Crash report records DPAPI-specific failure details
- [ ] Tests simulate corrupted secret file and verify raw-free crash capture

## 244. Fix LaunchFirstRunSetupIfRequired to verify success

**What to build:** Make `LaunchFirstRunSetupIfRequired` wait for setup completion and verify that the profile was actually protected before calling `RefreshStatus()`. The method should not silently succeed if setup fails or the profile is not protected.

**Blocked by:** 243. Fix crash boundary to capture at DPAPI load point.

- [ ] `LaunchFirstRunSetupIfRequired` waits for setup completion
- [ ] Status is refreshed only after verifying profile is protected
- [ ] Tests cover success, failure, and race conditions

## 245. Add smoke assertion for persisted binding values

**What to build:** Extend `NativeSubmitProductSmokeRunner` to verify that the stored/persisted binding values match the expected values (`Enter`/`Ctrl+Enter` or `Ctrl+Enter`/`Enter`) after loading from the profile store, not just for in-memory profiles.

**Blocked by:** 244. Fix LaunchFirstRunSetupIfRequired to verify success.

- [ ] Smoke verifies persisted binding matches expected values
- [ ] Smoke fails if UI hard-codes `Enter` for either AI app
- [ ] Smoke fails if non-Send shortcut is suppressed
