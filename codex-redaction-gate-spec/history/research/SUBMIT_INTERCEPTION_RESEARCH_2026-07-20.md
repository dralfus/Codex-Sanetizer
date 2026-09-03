# Submit Interception Research Notes, 2026-07-20

## Scope

This note answers the open questions from `ADR-004: Native Submit Interception Is the Primary Desktop UX` for the current Windows-only product scope: the new ChatGPT desktop app with Chat, Work, and Codex, plus Codex/ChatGPT Desktop surfaces that run from the installed `OpenAI.Codex` MSIX package.

## Evidence

- OpenAI Help, [`Moving to the new ChatGPT desktop app`](https://help.openai.com/en/articles/20001276), updated 2026-07-19: the new desktop app includes Chat and Work under ChatGPT, alongside Codex; updating the Codex app turns it into the new ChatGPT desktop app.
- OpenAI Help, [`Using the ChatGPT Windows app`](https://help.openai.com/en/articles/9982051-using-the-chatgpt-windows-app), updated 2026-07-16: the Windows app documents a customizable companion-window hotkey under `Settings > App > Companion window hotkey`, but does not document a prompt submit shortcut storage location.
- Microsoft Learn, [`LowLevelKeyboardProc`](https://learn.microsoft.com/en-us/windows/win32/winmsg/lowlevelkeyboardproc), updated 2025-07-14: `WH_KEYBOARD_LL` receives keyboard input before it reaches the target thread; returning a nonzero value suppresses propagation; callbacks must return quickly and should hand off work to another thread.
- Microsoft Learn, [`Hooks Overview`](https://learn.microsoft.com/en-us/windows/win32/winmsg/about-hooks), updated 2025-09-15: hooks can intercept, modify, or discard keystrokes, but global hooks should be used only when necessary because they can slow the system and conflict with other applications.
- Microsoft Learn, [`RegisterHotKey`](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-registerhotkey): `RegisterHotKey` is system-wide hotkey registration, and `F12` is reserved for debuggers. This supports keeping the old hotkey path secondary and not relying on `F12`.
- Microsoft Learn, [`About the Text and TextRange Control Patterns`](https://learn.microsoft.com/en-us/windows/win32/winauto/uiauto-about-text-and-textrange-patterns), updated 2025-07-14: UI Automation `TextPattern` exposes textual content as a read-only text stream; modifications require `ValuePattern`, `TextEdit`, or direct keyboard input.
- Local inspection on 2026-07-20:
  - Installed package: `OpenAI.Codex_26.715.2305.0_x64__2p2nqsd0c76g0`.
  - Running app processes include `ChatGPT.exe`, `codex.exe`, and `codex-code-mode-host` under that package.
  - User data root exists under `%LOCALAPPDATA%\Packages\OpenAI.Codex_2p2nqsd0c76g0\LocalCache\Roaming\Codex`.
  - `%USERPROFILE%\.codex\keybindings.json` only contained an `archiveThread` command with `key: null`.
  - Chromium/WebView `Preferences` files did not expose a stable prompt submit binding. Matching keys were browser or sync settings such as `apps.shortcuts_arch`, `profile.content_settings.exceptions.keyboard_lock`, and `sync.data_type_status_for_sync_to_signin.send_tab_to_self`.

## Answers

### 1. Where is the user-configured send shortcut stored, and is it stable?

Current answer: no stable local source is confirmed.

The current app stores user data in MSIX package-local Chromium/WebView-style profile directories, but neither the top-level Codex preferences nor the inspected web profile preferences expose an explicit prompt submit binding. The documented Windows setting covers the companion-window hotkey, not the prompt submit shortcut.

Product decision: a protected profile must not claim `protected` from file inspection alone. The binding source should be recorded as one of:

- `documented_config`, only if OpenAI later documents a stable setting;
- `empirical_config`, only after we can repeatedly prove the setting path and schema across app updates;
- `user_verified`, when onboarding records the shortcut and verifies it on the target surface.

For the current release path, use `user_verified` as the primary source and treat config discovery as an optional optimization.

### 2. Can the adapter distinguish send prompt from insert newline?

Current answer: yes, but only by profile-verified context, not by key name alone.

The adapter must distinguish:

- foreground app identity matches the selected protected profile;
- focused element matches the verified composer UIA shape;
- the keyboard event matches the profile's active submit binding exactly;
- the event is not an IME composition or dead-key sequence;
- the event does not match the profile's newline binding.

Product decision: the profile must store both `submit_binding` and `newline_binding`. Onboarding must test both actions in dry-run mode against the selected surface. If submit and newline cannot be separated for that profile, status is `binding_unknown` or `surface_unverified`, not `protected`.

### 3. What is the safest emergency escape?

Current answer: use layered escape, with a local emergency chord plus tray control and watchdog behavior.

The hook path can block input if implemented poorly, and Microsoft documents strict timing requirements for low-level hooks. The safety design should be:

- hook callback performs only fast classification and queues sanitizer work;
- all non-matching input is passed through immediately;
- `Ctrl+Alt+Shift+Pause` disables protection for the current app for a short local window, such as 5 minutes;
- tray menu always exposes `Disable protection`, `Re-enable protection`, and current status;
- if the hook health check fails, the app unregisters the hook and switches to `degraded_hotkey_only`;
- if a protected submit is matched but sanitizer cannot complete, suppress the submit and show local recovery UI.

The emergency action must be audited raw-free and visible. It should disable interception, not silently send the raw prompt.

### 4. Should enterprise mode lock protected profiles and disallow hotkey-only degradation?

Current answer: yes.

For enterprise and managed desktops, `hotkey_only` is not an acceptable silent fallback for protected AI apps because it reintroduces the original leak path. Admin policy should be able to:

- require specific protected profiles;
- block user removal of those profiles;
- block `hotkey_only` degradation for protected apps;
- choose the failure behavior: `block_submit` or `allow_with_visible_unprotected_warning`;
- export raw-free status and audit events.

Consumer/local mode can remain more permissive, but enterprise mode should make protection state enforceable.

### 5. How should the product warn on version/profile mismatch?

Current answer: profile compatibility must be explicit and user-visible.

Each verified profile should include:

- package family name and package full name pattern;
- package version tested range;
- executable path and product version;
- process name;
- expected top-level window identity signals;
- expected composer UIA control type, framework id, class name, automation id/name patterns, and supported read/write patterns;
- submit and newline binding source/version;
- last verification timestamp and verification result.

If the app is open but one or more required signals no longer match, status becomes `surface_unverified`. The tray and any confirmation UI should say that the selected AI app is open but not protected for this version/profile. Enterprise policy decides whether submit is blocked or allowed with a visible warning.

## Implementation Consequences

- Add a `SubmitBindingProfile` model with binding source, submit binding, newline binding, compatibility evidence, and verification timestamps.
- Build an onboarding verifier that records `user_verified` bindings without sending a cloud prompt.
- Replace any default assumption of `Enter` or `Ctrl+Enter` with `binding_unknown` until verification succeeds.
- Keep the current manual hotkey feature as diagnostics/rescue only.
- Add raw-free diagnostics for package identity, profile match status, binding source, and mismatch reason.
