# Codex handoff: Code Sanitizer

## Resume point

- Repository: `S:\6. DevSecOps\Codex security`
- Branch: `master`
- Implementation baseline: `61c7c8f191dbfe199ae94bd1b4db38fd717e5332`
  (`Fix resident workflow and protected send races`)
- This handoff is committed immediately after that baseline; use
  `git rev-parse HEAD` for the handoff commit itself.
- Authoritative backlog: [`tickets.md`](../tickets.md)
- Dependency map: [`DEVELOPMENT_ROADMAP.md`](DEVELOPMENT_ROADMAP.md)
- Product and architecture requirements: the other documents in this
  specification directory; do not reconstruct requirements from chat history.

## Current product status

The keyboard protected-Send path for selected Windows Codex/ChatGPT Desktop
composers is implemented. The latest change fixed two concurrency gaps:

- resident candidate activation and workflow terminal publication now use an
  attempt-scoped transaction gate;
- an irreversible sanitized submit retains its side-effect boundary until the
  canonical `sent_safely` state is committed and serialized into the resident
  snapshot.

Tickets 347 and 348 remain marked reopened intentionally. Their production
race fixes are implemented, but ticket 347 must not be reclosed until the
remaining deterministic setup/recovery proof is complete.

## Immediate next task

Implement **ticket 349** from `tickets.md`: the deterministic setup/recovery
workflow race matrix.

Required slices, in order:

1. Setup cancellation and newer-operation races after candidate activation but
   before persistence/terminal publication.
2. Failed rollback-runtime restoration publishes the coordinator-owned failure
   and leaves resident protection stopped.
3. Recovery cancellation and newer-operation races after reload but before
   terminal publication.
4. Run the complete verification gates, review the diff, and only then decide
   whether 347 and 348 can be reclosed.

Use injected barriers immediately before contested gates. Do not use sleeps,
live desktop focus, UIA, or cloud submission as proof. Preserve the invariant
that ordinary input in unrelated applications is unaffected.

## Verification evidence at handoff

The final committed implementation passed:

```powershell
dotnet test .\src\CodexRedactionGate\CodexRedactionGate.csproj --no-restore -nologo
# 1739 passed, 0 failed

dotnet run --project .\src\CodexRedactionGate\CodexRedactionGate.csproj --no-build -- --self-test
# Self-test passed.

dotnet run --project .\src\CodexRedactionGate\CodexRedactionGate.csproj --no-build -- --product-smoke
# status: product_smoke_passed
```

An explicit reference-composer run reported every scenario, raw-free check and
cleanup as passed, but returned `reference_proof_recorded: false` because the
installed-build identity did not match the source build. Treat this as an open
release-proof condition, not as proof that the scenario matrix failed.

Run .NET commands sequentially; parallel build/test commands can lock `obj`.

## Installer and manual testing

The newest installer currently present is:

`artifacts\installer\CodexRedactionGateSetup-0.1.20260811.t1900.exe`

It predates commit `61c7c8f1` and is therefore stale. Do not use it to validate
the current fixes. Complete ticket 349 and final review first, then rebuild the
installer and verify its embedded version before manual testing.

Project-file protection is still not a product claim: real pre-cloud file
ingress is unresolved. Keep the published status unsupported/not configured;
do not infer file protection from the working prompt path.

## Review boundary

The previous review fixed all actionable findings in the protected-Send race
scope. A final narrow review found no remaining deadlock, livelock, or
`Submitted=false`-after-submit path. Do not reopen those findings without a
new failing behavioral test or concrete code path.

Do not delete completed tickets or old dependency edges; they are retained as
project history. New tickets must define state owner, fail-closed state,
allowed transitions, and deterministic proof.

## Suggested skills

- `$mattpocock-skills:tdd` for each deterministic race slice.
- `$mattpocock-skills:implement` for ticket 349.
- `$karpathy-guidelines` to keep changes narrow and evidence-driven.
- `$mattpocock-skills:code-review` after implementation, using commit
  `61c7c8f1` as the fixed point.
- `$mattpocock-skills:to-tickets` only if review finds work outside ticket 349;
  amend existing tickets when the finding belongs to their acceptance scope.
