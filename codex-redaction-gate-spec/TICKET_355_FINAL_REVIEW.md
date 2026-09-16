# Ticket 355: финальный scoped review

Дата: 2026-09-14. Фиксированная точка: `1d759b565d42bc88dab6747d7de2b1d509d9c7f3`.

Рассмотрен только новый execution-channel/focus scope:
`SandboxWorkerBootstrap.ps1`, `SandboxTestWorker.ps1`,
`Test-SandboxInteractiveWorkerHost.ps1`,
`WINDOWS_SANDBOX_TEST_EXECUTION.md`, а также взаимодействующие изменения
reference persistent-fixture в `ReferenceComposerAcceptance.cs` и
`SanitizerNativeSubmitTests.cs`. Исторический Ticket 355 diff повторно не
ревьюировался, кроме этого взаимодействия.

## Standards

Verdict: **PASS**. `CRITICAL=0`, `HIGH=0`.

- P2 — `scripts/SandboxWorkerBootstrap.ps1:169-172`: закрытие supervisor
  синхронно вызывает `workerPowerShell.Stop()`. Активная job может завершиться
  до обычной publication result; последующий worker архивирует оставшийся
  lease. Это fail-safe, но не graceful shutdown.
- P3 — `scripts/SandboxTestWorker.ps1:179-199`: `Set-WorkerState` принимает
  readiness без строгого типа при наличии `InteractiveHostReadiness`. Это
  maintainability judgement, не contract breach.
- P3 — узкий P/Invoke foreground API продублирован между bootstrap и worker.
  Для отдельных runspace/UI responsibilities это оправдано и не требует
  refactor в данном scope.

Проверено без finding: durable `Application.Run`; worker в отдельном MTA
runspace того же OS process; UI marshal через `BeginInvoke` с bounded wait;
cleanup runspace/PowerShell; fixed job schema/path; `restore` ungated;
identity-bound one-shot focus reacquisition persistent fixture; отсутствие
`AttachThreadInput`, `AllowSetForegroundWindow` и `SendInput`.

## Spec

Verdict: **FAIL**. `CRITICAL=0`, `HIGH=1`.

- High — `scripts/SandboxTestWorker.ps1:51-54,202-212,353-373` и
  `scripts/SandboxWorkerBootstrap.ps1:127-144`: one-shot UI lease хранится
  только в `$script:` текущего worker process. Повторный запуск bootstrap в
  той же живой Windows Sandbox создаёт новый worker с `lease=true` и допускает
  второй UI-sensitive `test` без новой Sandbox session. Это противоречит
  контракту «ровно один UI-sensitive test lease на fresh Sandbox session» в
  `WINDOWS_SANDBOX_TEST_EXECUTION.md:94-99` и оставляет путь к ложному
canonical-GREEN evidence.

**Resolution pending runtime evidence (2026-09-14):** Bootstrap now owns one
opaque, exact-GUID session id in Sandbox-local non-mapped storage. The worker
uses that id as the name of one mapped `FileMode.CreateNew` lease claim before
`dotnet`. A repeated Bootstrap in the same live Sandbox sees the existing claim
and fails closed before `dotnet`; a fresh Sandbox filesystem naturally creates
a new id. Static contract evidence is available; a fresh-session runtime proof
is still required.

Проверено без finding: manual foreground launch документирован; перед
`dotnet` выполняется bounded реальный `GetForegroundWindow == supervisor HWND`
gate; недоступный foreground fail-closed; `restore` не потребляет lease;
result/worker JSON несут raw-free readiness и lease metadata; реальный fixture
foreground/UIA остаётся acceptance authority, а `SetForegroundWindow` является
только request, не evidence.

## Release-matrix evidence seen during review

В dedicated Sandbox worktree присутствует результат
`.sandbox-jobs/results/t355-one-shot-final-matrix-20260914-001.json`: exact
filter `ReferenceComposerReleaseAcceptance_RunsFullMatrixTwice`, `ExitCode=0`,
`Status=completed`, `InteractiveHostPresent=true`,
`InteractiveHostForegroundReady=true`, lease был consumed до запуска `dotnet`.
Лог сообщает `1 passed, 0 failed`. Это focused Sandbox GREEN evidence, но оно
не устраняет High lease-session defect выше и не делает Ticket 355 DONE.

## Closure note (2026-09-16)

Этот документ сохраняет scoped review от 2026-09-14 как исторический record.
Позднее coordinator принял Ticket 355 по отдельному complete evidence bundle:
independent final review `SPEC PASS` / `CODE_QUALITY PASS`, local
non-interactive receipt `1974/1974` и current-build local interactive
release-matrix receipt `1/1`. Исторический Sandbox defect не является
completion requirement для одобренного local interactive channel. Авторитетный
статус и exact identities находятся в `TICKET_355_CHECKPOINT.md` и
`tickets.md`.
