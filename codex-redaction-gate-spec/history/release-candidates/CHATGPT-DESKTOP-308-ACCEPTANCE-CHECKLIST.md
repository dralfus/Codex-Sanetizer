# ChatGPT Desktop Live Acceptance Checklist

Use this checklist for the 308 release gate. It verifies one installed build
of Code Sanitizer against the configured Windows ChatGPT Desktop profile.

## 1. Install And Identify The Build

- [ ] Close any older Code Sanitizer instance, or let the installer stop it.
- [ ] Install the supplied `CodexRedactionGateSetup-*.exe`.
- [ ] Leave the launch-after-install option enabled.
- [ ] Confirm that exactly one Code Sanitizer tray icon is present.
- [ ] Open the tray menu and record the displayed build version.
- [ ] Confirm the installed folder is `%LOCALAPPDATA%\Programs\CodexRedactionGate`.
- [ ] Confirm the tray status is not `disabled`, `setup_required`, or
      `degraded` before continuing.
- [ ] If the status shows `previous Send interrupted`, click `Retry
      protection` once.
- [ ] During retry, the status changes to `retrying protection`; after the
      operation it shows a final result and does not remain stuck on the old
      interruption message.

## 2. Verify The ChatGPT Profile

- [ ] Open `Local protection status` from the tray.
- [ ] Confirm `Local DPAPI protection` is `ready`.
- [ ] Confirm `Automatic prompt protection` identifies the selected ChatGPT
      Desktop profile and the intended keyboard Send binding.
- [ ] Confirm the binding is the same binding configured in ChatGPT Desktop.
- [ ] If the profile or binding is not verified, run profile setup first and
      restart this checklist from this section.

## 3. Record The Reference Proof

Run from PowerShell:

```powershell
$app = "$env:LOCALAPPDATA\Programs\CodexRedactionGate\CodexRedactionGate.exe"
& $app --reference-composer-release-acceptance
```

Expected result:

- [ ] The command exits with code `0`.
- [ ] The output reports `overall: passed`.
- [ ] The output reports `reference_proof_recorded: true`.
- [ ] No prompt text, sensitive value, local path, or exception detail is
      printed.

## 4. Capture One Live ChatGPT Send

Arm the one-use live contract capture:

```powershell
& $app --chatgpt-live-contract-arm
```

Expected result:

- [ ] The output reports `status: live_contract_armed`.
- [ ] The output reports `cloud_submission: false`.
- [ ] The output says the next action is one non-sensitive prompt.

In ChatGPT Desktop:

- [ ] Focus the verified ChatGPT composer.
- [ ] Enter one harmless prompt, for example `reply with OK`.
- [ ] Send it using the verified keyboard binding.
- [ ] Confirm the prompt is sent once.
- [ ] Confirm no replacement window is required for this harmless prompt.
- [ ] Do not use a real secret or production credential for this step.

## 5. Confirm The Resident Protected Claim

Use the tray menu or `Local protection status` window as the authoritative
result. The resident status must show all of the following:

```text
protected_claim=protected
reference_acceptance=passed
live_contract=passed
readiness=protected
native_submit=protected
```

- [ ] The resident tray/status view shows the protected claim.
- [ ] The standalone `--chatgpt-protected-claim-status` command is treated as
      diagnostic-only; `protected: false` there is expected because a separate
      process cannot read the resident snapshot authoritatively.
- [ ] The live arm is no longer reusable. A second arm is required for another
      live-contract capture.

## 6. Verify A Synthetic Sensitive Prompt

Use only a synthetic dictionary term already configured for testing.

- [ ] Enter the synthetic sensitive term in ChatGPT Desktop.
- [ ] Send using the verified keyboard binding.
- [ ] The original Send is suppressed before cloud submission.
- [ ] The replacement window appears in the foreground.
- [ ] Approving the replacement sends only the sanitized text.
- [ ] Canceling the replacement does not send the original text.
- [ ] A subsequent new sensitive prompt still opens a new replacement window.

## 7. Record The Acceptance Result

Record only safe metadata:

```text
installer_file:
installed_build:
chatgpt_profile_verified: yes/no
submit_binding:
reference_acceptance: passed/failed
live_contract: passed/failed
resident_claim: protected/blocked
synthetic_sensitive_prompt: passed/failed
original_text_sent: yes/no
```

Acceptance fails if the resident claim is not protected, if the original
sensitive text can be sent without the replacement window, or if any required
proof is missing, stale, or bound to another build, fingerprint, or Send key.
