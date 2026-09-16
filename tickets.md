# Tickets: active Codex Redaction Gate backlog

This is the authoritative backlog for active or reopened work. Closed tickets,
their acceptance criteria, and their evidence history are retained in
[ARCHIVE_TICKETS.md](ARCHIVE_TICKETS.md).

The user-facing development sequence is [DEVELOPMENT_ROADMAP.md](DEVELOPMENT_ROADMAP.md).

## Required Contract For Every New Ticket

Every ticket added after this section must state these items before implementation begins:

- **State owner:** the one component that publishes the authoritative state for this behavior. UI and persisted records may project it, but cannot independently decide protection.
- **Fail-closed state:** the exact externally visible state and cloud-submission behavior when evidence, activation, recovery, or storage is uncertain.
- **Allowed transitions:** the permitted state changes, their triggering commands/events, and the condition for publishing each new state.
- **Deterministic proof:** the highest available test seam and the assertions that prove the transition without timer polling, foreground focus, or a live cloud submission.
- **Red-capable reproduction:** for a bug or safety regression, the exact command
  or resident action that fails before the change and must pass afterward. If no
  current seam can reproduce it, diagnostics/acceptance work is the first
  ticket and production logic is not changed yet.
- **Highest required seam:** deterministic transaction, production-access
  reference composer, installed resident canary, or another explicitly named
  product boundary. A lower seam cannot substitute for a required higher seam.
- **Evidence target:** the required next state from `proposed`,
  `reproduced_red`, `implemented`, `locally_verified`, `live_verified`, and
  `released`, including the build identity fields that make the evidence
  current.

If one of these cannot be stated, the work is an architecture-discovery ticket
and must be resolved before a feature ticket is implemented. `Implemented` is
not reported as `fixed`; fixed requires the original reproduction to be green
at the highest required seam.


## Active tickets
## 283. Prove a supported live ingress boundary for protected project files

**What to build:** Establish a real, pre-cloud integration boundary through which a selected coding workspace's supported file reads, attachments, and file-derived tool output must pass before model visibility. If the selected Codex/Desktop surface has no such supported boundary, keep the capability explicitly unsupported rather than implying protection from UI observation or a local broker demo.

**Blocked by:** 282. Separate broker evidence from live project-file protection status; a verified supported Codex/Desktop integration surface or an approved local gateway design.

**Do not:** Claim protection from post-action UI Automation events, scrape raw project content from the client after it has been sent, or turn every file in a task into a sequence of blocking confirmation dialogs.

- [ ] A disposable protected workspace demonstrates one real pre-cloud file-context operation entering the local broker and produces raw-free evidence that the model-visible payload is sanitized.
- [x] When the boundary is unavailable, protected-workspace mode fails closed for its local attachment and unmanaged-connector channels and reports `unsupported` rather than silently allowing those channels through Code Sanitizer.
- [ ] The user experience presents one operation-level batch summary with a navigable per-file list; it does not require accepting a separate popup for every file.
- [x] Live `project_files_protected` remains false unless a real ingress proof and its automated regression test exist.

**Current blocker:** The Windows Codex/ChatGPT Desktop surface exposes no verified pre-cloud integration point for repository reads, attachments, or file-derived tool output. Code Sanitizer therefore reports `project_file_ingress_unsupported`; it does not claim to block direct desktop-client file reads. The remaining two criteria require a supported client extension point or an approved local gateway that owns those operations before the client reaches the cloud.


## 286. Exclude selected files, including .env, from cloud file context

**What to build:** Let the user select one or more exact local files - including `.env` - for an `exclude from cloud` policy. Once live project-file interception is available, Codex/Desktop must not receive the contents, filename, path, attachment representation, or file-derived tool output of an excluded file. The user must see locally that the file was excluded and why.

**Blocked by:** 283. Prove a supported live ingress boundary for protected project files; 285. Show local protection capabilities and active state in the tray UI.

**Do not:** Treat a filename suffix match as proof that a file was excluded; sanitize and forward an explicitly excluded file; expose selected raw paths in cloud-bound logs or diagnostics; silently fall back to direct reads/uploads when interception is unavailable; or mark a file protected based only on a local preference without a verified pre-cloud enforcement path.

- [ ] The local UI can add, review, and remove one or more exact files from the exclusion policy, including `.env`; local display is allowed, while persisted/cloud-bound diagnostics use protected or raw-free identities.
- [ ] For a protected workspace with a live ingress boundary, every supported path that could expose an excluded file - file read, direct attachment, file-derived tool output, diff, or patch context - is blocked before model visibility with a raw-free `file_excluded_from_cloud` status.
- [ ] The operation-level status view shows that an excluded file was withheld, identifies the policy outcome locally, and keeps the rest of the task/file batch usable without a per-file confirmation-dialog storm.
- [ ] If the live ingress boundary is missing, unhealthy, or cannot classify the selected file, the affected channel fails closed and the UI reports `unsupported` or `degraded`; it never claims the exclusion is enforced.
- [ ] Automated tests use synthetic `.env` and arbitrary-file fixtures to prove no raw contents, paths, or filenames reach model-visible payload records, audit output, or cloud-bound diagnostics.


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

**Reopened (2026-08-16):** Final review found that the execution lease is
released immediately after the OS submit side effect, while the canonical
`sent_safely` terminal trace is published later by the pipeline. Reload or
cancellation in that gap can reject terminal publication after sanitized text
was already sent, producing a false fail-closed result and making a duplicate
retry possible.

- [x] Hold one correlated side-effect boundary from the first irreversible
      local write/replay decision through canonical terminal publication.
- [x] Once sanitized submit succeeds, publish exactly one `sent_safely`
      terminal outcome before reload/cancellation can invalidate the attempt;
      never report `Submitted=false` after the side effect already occurred.
- [x] Add a deterministic test that races reload/cancellation after submit
      succeeds but before terminal publication and proves one submit, one
      terminal result and no duplicate-send ambiguity.
- [x] Centralize adapter-stage normalization in the protected Send operation so
      Windows adapters cannot independently reinterpret replay/terminal state.
- [ ] Close this ticket again only after focused tests, the full suite,
      `--self-test`, `--product-smoke` and the reference-composer matrix pass.

**Remediation implemented (2026-08-16):** One side-effect scope now spans
write/replay through terminal publication. Cancellation requested before the
linearization point blocks the side effect; cancellation after it waits for a
locally committed, serialized `sent_safely` snapshot. The deterministic reload
race proves one submit and exactly one terminal outcome. Verification:
`1739/1739`, `--self-test` and `--product-smoke` passed; the explicit reference
matrix reported all scenarios and cleanup passed but did not record a release
proof for the current installed-build mismatch. Reclosure remains gated by 347
and ticket 349.

**Acceptance pending (2026-08-22):** Ticket 349 completed the remaining
resident transaction proof. A matching installer was built from source commit
`3622ef22` as `0.1.20260822.t1325`; the source-build reference-composer matrix
passes every scenario twice with raw-free traces and cleanup. Keep this ticket
open until the installed candidate records `reference_proof_recorded: true`.

**Architecturally reopened (2026-08-26):** The extracted
`ProtectedSendPipeline` is still a shallow orchestration interface. Its caller
continues to understand stage order through a broad host contract, while
`OsInteractionOrchestrator` owns a second read/sanitize/overlay/write/replay
state machine. In addition, the current reference acceptance can write directly
to its fixture TextBox instead of exercising production
`NativeVerifiedComposerTextAccess`; that proof cannot detect production UIA,
STA, focus, write-verification, or replay failures. Preserve the previous
implementation and evidence above as history, but do not close 348 until the
evidence-gated transaction migration in 351-358 is complete and the matching
installed resident proof is green.


## 354. Introduce ProtectedSendTransaction beside legacy orchestration

**What to build:** Add the deep external interface
`Execute(AdmittedProtectedSend) -> ProtectedSendTerminalResult`. It owns attempt
identity, admitted generation, raw prompt lifetime, sanitization,
confirmation/edit/cancel, target revalidation, write verification, replay,
side-effect linearization, raw-free trace ordering, and exactly one terminal
publication. Initially route only deterministic/reference execution through it;
production remains on the legacy owner until ticket 356.

**Blocked by:** 353.

**State owner:** `ProtectedSendTransaction` is the sole owner of an admitted
attempt and terminal outcome. Resident runtime owns admission; session and UI
adapters return effects only.

**Fail-closed state:** Missing or duplicate stages, stale generation, target
change, cancellation, write mismatch, replay uncertainty, trace failure, or
exception ends in one blocked result with no independent adapter replay.

**Allowed transitions:** `admitted -> read -> sanitized -> safe_path |
confirmation -> write -> verify -> replay -> sent_safely`, or one terminal
`blocked(reason)`/`cancelled`. Only the transaction may cross the irreversible
side-effect boundary and publish terminal state.

**Deterministic proof:** Matrix covers safe/sensitive prompts, edit, cancel,
confirm, target changes, stale generation, write mismatch, replay failures,
cancellation races, repeated sends, and exactly one terminal result without
timers, UIA, or cloud access.

**Red-capable reproduction:** Characterization tests first expose that legacy
callers can observe or own stage ordering outside the proposed transaction and
that duplicate terminal/side-effect ownership is representable.

**Highest required seam:** Deterministic `ProtectedSendTransaction` matrix.

**Evidence target:** `locally_verified`; production remains on legacy and cannot
claim live verification from this ticket.

- [x] Implement the compact request/result contract and explicit state machine.
- [x] Move lease, trace, side-effect, and terminal publication ownership inside
      the transaction.
- [x] Keep the legacy production path active and prohibit dual side effects.
- [x] Prove raw prompt data is not retained in terminal evidence.


## 355. Route reference acceptance through the production composer access path

**Status:** DONE (2026-09-16). Evidence bundle: independent final review
`SPEC PASS` / `CODE_QUALITY PASS`; local non-interactive receipt `1974/1974`;
and current-build local interactive release matrix `1/1`. The historical
Windows Sandbox infrastructure blocker is not used as a requirement for this
approved local evidence channel.

**What to build:** Migrate reference-composer acceptance to
`ProtectedSendTransaction` and `ProtectedComposerSession`, using production
`NativeVerifiedComposerTextAccess` for Windows access behavior. Direct
assignment to the fixture TextBox is removed from release evidence. The
reference-only input source remains physically unable to target Codex/ChatGPT.

**Blocked by:** 354.

**State owner:** The transaction owns each acceptance attempt; the reference
fixture owns only its window and deterministic input stimuli.

**Fail-closed state:** A reference adapter or production-access mismatch fails
the scenario and records one raw-free terminal outcome; it cannot substitute a
fixture write or mark release evidence passed.

**Allowed transitions:** The same transaction transitions as ticket 354, with
two explicit adapter modes: deterministic session contract and production
Windows access to the reference composer.

**Deterministic proof:** Run all reference scenarios twice and prove stable
cleanup, exact multiline formatting, confirm writes sanitized text, cancel
preserves interception, target changes block, and replay failures are terminal.

**Red-capable reproduction:** Disable or fail production composer access while
leaving the fixture TextBox writable; the old proof can pass, while the new
production-access proof must fail.

**Highest required seam:** Reference composer through production
`NativeVerifiedComposerTextAccess` and the session contract.

**Evidence target:** `locally_verified` at the production-access reference
level, bound to the executable build.

**Implementation clarification (2026-09-03):** This ticket is the required
bridge before 356; it is not optional. `ProtectedSendTransaction` currently
uses its own deterministic session contract, while the reference acceptance
fixture still has a replay boundary that can act on its `TextBox` directly.
The implementation must introduce one reference adapter through
`IProtectedComposerSession` / `NativeVerifiedComposerTextAccess` and make the
transaction-facing session delegate read, revalidate, write-and-verify, and
replay to that adapter. The fixture may provide deterministic stimuli and a
verified reference target only; it must not be a fallback side-effect owner.

**Evidence contract:** Add a typed `EvidenceLevel` to the reference acceptance
report and preserve it in the release-acceptance projection. The only level
this ticket may publish is a named reference production-access level. Missing,
wrong, or stronger (`live` / installed-application) evidence levels fail the
scenario closed and cannot be rendered as release proof.

**Required RED proof:** With the fixture `TextBox` still writable, disable or
fail `NativeVerifiedComposerTextAccess` (or its session operation). The old
direct-fixture path would pass; the new path must produce one raw-free blocked
terminal result, no replay, and no passed release scenario.

**Test execution:** UI-sensitive, reference-composer, or full-suite tests run
only through the Windows Sandbox worker described in
`codex-redaction-gate-spec/WINDOWS_SANDBOX_TEST_EXECUTION.md`. Do not run them
on the user's primary desktop. Inspect the per-job JSON and log before claiming
verification.

- [x] Remove direct fixture TextBox writes from acceptance evidence.
- [x] Use the production access adapter through the session contract.
- [x] Preserve physical exclusion from OpenAI Desktop targets.
- [x] Publish the evidence level honestly as reference production-access proof,
      not installed ChatGPT proof.
- [x] Add the bounded edit-loop follow-up to the transaction owner or an
      explicitly linked ticket; do not silently defer it to production routing.
      Linked follow-up: ticket 356 (migrate production keyboard Send to
      `ProtectedSendTransaction`); see the TODO in `ReferenceComposerProductionAccess`.


## 356. Migrate production keyboard Send to ProtectedSendTransaction

**What to build:** Route the supported OpenAI Desktop keyboard Send path from
resident admission into `ProtectedSendTransaction`. The hook only captures,
classifies, and suppresses; tray only projects state; adapters cannot write,
replay, or publish terminal success outside the transaction.

**Blocked by:** 355.

**State owner:** Resident runtime owns immutable admission; the transaction
owns every admitted attempt; the session owns target-scoped mechanics.

**Fail-closed state:** Any unavailable transaction/session, stale generation,
uncertain selected target, write mismatch, or replay failure keeps the original
Send suppressed and reports one actionable raw-free outcome.

**Allowed transitions:** `captured selected Send -> suppressed -> admitted ->
transaction terminal`; unrelated input passes through and cannot create a
transaction.

**Deterministic proof:** Existing callback, reload, repeated-send, cancel/edit,
target-change, formatting, and terminal-publication matrices pass through the
new interface. The resident canary from 352 must turn green on the installed
candidate without changing its assertion.

**Red-capable reproduction:** Reuse the unchanged failed canary artifact and
assertion from 352; do not create a more convenient replacement scenario.

**Highest required seam:** Installed active resident on the supported OpenAI
Desktop keyboard path.

**Evidence target:** `live_verified` for the exact commit, executable, installer,
profile fingerprint, generation, and Send binding.

- [ ] Replace production keyboard orchestration with the transaction call.
- [ ] Remove independent write/replay/terminal decisions from migrated callers.
- [ ] Prove one side effect and one terminal result across cancellation/reload.
- [ ] Turn the original installed resident canary from red to green.


## 357. Evidence-gate installer and release claims

**What to build:** Make installer packaging, release smoke, status UI, and
release documentation consume the evidence contract. A candidate may claim
keyboard protected Send only when required deterministic, reference production-
access, and installed resident evidence is current for that exact build.

**Blocked by:** 356.

**State owner:** The immutable release evidence manifest owns candidate proof;
installer and UI are projections.

**Fail-closed state:** Missing/mismatched commit, version, executable hash,
installer identity, compatibility fingerprint, or binding leaves the claim
`not_verified` and never silently reuses older proof.

**Allowed transitions:** `locally_verified -> live_verified -> released` only
after all required artifacts match. Rebuild or profile/app drift invalidates the
affected higher-level evidence.

**Deterministic proof:** Manifest validator rejects stale and cross-build proof;
installer smoke verifies embedded version/hash and required evidence fields;
UI tests project each state without inventing readiness.

**Red-capable reproduction:** Fixtures with a passing older installer proof or
missing build identity must be rejected before release gating is implemented.

**Highest required seam:** Installer smoke plus matching installed resident
canary evidence.

**Evidence target:** `released` only when deterministic, reference, and live
artifacts all match the packaged candidate.

- [ ] Bind all protected-Send evidence artifacts to exact build identity.
- [ ] Gate release claims and installer smoke on applicable evidence levels.
- [ ] Show evidence state and next action without exposing prompt data.
- [ ] Update release documentation to prohibit unsupported fixed/released
      claims.


## 358. Contract legacy protected-Send orchestration and reclose ticket 348

**What to build:** After the new production path and all evidence levels are
green, remove the legacy protected-Send stage owner, the broad
`IProtectedSendPipelineHost`, duplicate protected-Send sequencing in
`OsInteractionOrchestrator`, and direct acceptance writes. Preserve unrelated
apply-only behavior behind an explicitly named interface if it is still used.

**Blocked by:** 351-357. Ticket 348 cannot close before this contraction.

**State owner:** `ProtectedSendTransaction` remains the only admitted-attempt
owner. Resident, session, sanitizer, overlay, and tray boundaries retain only
their documented responsibilities.

**Fail-closed state:** If any caller still owns replay or terminal publication,
or if any required evidence is missing, contraction and reclosure stop.

**Allowed transitions:** `dual implementation with one active owner -> all
callers migrated -> legacy unreachable -> legacy deleted -> 348 reclosed`.
There is never a state with two active side-effect owners.

**Deterministic proof:** Static dependency checks and tests prove the broad host
and duplicate state machine are gone. Full suite, self-test, product smoke,
reference production-access matrix, and installer-matched resident canary pass
for the same build.

**Red-capable reproduction:** Static dependency checks must initially fail while
the broad host, direct fixture write, or duplicate protected-Send stage owner is
still reachable.

**Highest required seam:** Static architecture gate plus the installed resident
canary for the contracted candidate.

**Evidence target:** `released`, followed by reclosure of 348 with linked proof
for all required levels.

- [ ] Delete legacy transaction ownership and broad host callbacks.
- [ ] Keep any apply-only operation separate from cloud-bound Send semantics.
- [ ] Run all evidence levels on one candidate and record the artifacts.
- [ ] Reclose 348 with links to deterministic, reference, live, and release
      evidence; preserve its earlier history.


## 362. Preserve structural path suffixes during sensitive-term matching

**What to build:** When a sensitive term identifies a host component inside a
structured workspace or container path, replace only that host component and
preserve the path syntax and suffix unchanged. For example, a configured
hostname in `host:/mnt/host/` may be pseudonymized while `/mnt/host/` remains
usable and readable. A plain sensitive hostname must remain configurable as a
separate dictionary term.

**Blocked by:** 352; the protected keyboard acceptance gate must have a
raw-free installed failure artifact before matching behavior is extended.

**State owner:** The sanitizer policy owns term classification and replacement
boundaries; the protected-send transaction owns only the resulting sanitized
text. The path parser/matcher owns structural token boundaries and must not
publish raw values in diagnostics.

**Fail-closed state:** An ambiguous or malformed structured path is not
partially rewritten. It returns the existing safe failure result and leaves the
original text out of diagnostics and cloud submission.

**Allowed transitions:** `input -> structured_match -> host_only_replaced ->
sanitized_output`, or `input -> ambiguous -> failed_closed`.

**Deterministic proof:** Tests cover case-insensitive hostname matching,
structured host/path separation, repeated separators, malformed paths, and
plain hostname terms. Assertions must verify both the replaced host and the
unchanged suffix without using live cloud submission.

**Highest required seam:** Sanitizer output consumed by the protected-send
transaction, followed by the existing installed keyboard acceptance path.

**Evidence target:** `locally_verified` sanitizer matrix, then `live_verified`
only when the unchanged installed canary and protected-send evidence remain
green.

- [ ] Add a structural matcher that distinguishes host tokens from path
      suffixes.
- [ ] Preserve the suffix exactly after host replacement.
- [ ] Add regression tests for `host:/mnt/host/` and malformed input.
- [ ] Document that a plain hostname can still be added as its own sensitive
      term.

