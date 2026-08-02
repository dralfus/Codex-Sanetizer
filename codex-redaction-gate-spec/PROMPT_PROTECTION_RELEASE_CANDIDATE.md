# Prompt Protection Release Candidate

## Scope

This release candidate is the standalone Windows prompt-protection product
slice. It protects the selected and verified Codex Desktop or ChatGPT Desktop
composer before its normal keyboard or mouse Send action reaches the cloud.

## Included

- Native Send interception for a selected, verified desktop-composer profile.
- Fail-closed setup and binding verification: an unverified profile is not
  reported as protected.
- Sensitive-term and built-in detector redaction, local pseudonym mapping, and
  local restore.
- Local DPAPI-backed protection readiness and explicit recovery workflow.
- Resident tray status, single-instance handling, raw-free diagnostics, and
  release smoke coverage.

## Explicitly Outside This Release

- Protection of project files read directly by the Codex or ChatGPT desktop
  client is `unsupported` until a verified pre-cloud file-ingress boundary is
  available.
- The local project-file broker is a demonstrator and must not be represented
  as live client file protection.
- Third-party programmatic Windows UI Automation Send invocation is
  `programmatic_uia_invoke_unsupported`.

## Engineering Acceptance Evidence

The release candidate is accepted for manual Windows testing when the current
commit passes the full automated test suite, `--self-test`, and
`--product-smoke`, and the installer is built from that same commit. The smoke
output must retain `project_files_protected: false` and the file-ingress status
must remain `unsupported`.

Manual user acceptance remains a separate final step. It must prove, on the
installed tray application, that:

1. One resident tray instance is running and its status shows prompt protection
   as active only after a selected profile is verified.
2. A sensitive test prompt opens the active replacement window; accepting it
   sends only the sanitized text.
3. Canceling the replacement window does not permit a later sensitive submit
   to bypass interception.
4. Normal typing and Enter behavior in unrelated applications remain unchanged.
5. The tray's file-protection row continues to show the truthful unsupported
   state rather than claiming project-file protection.

## Follow-on Work

File-context architecture proceeds independently under tickets 283 and 286.
It may be integrated into a later release only after a supported pre-cloud
boundary, operation-level batch review, and end-to-end evidence exist.
