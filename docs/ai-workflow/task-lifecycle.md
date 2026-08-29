# Протокол выполнения одного ticket

Версия workflow-комплекта: `1.1`

Этот файл обязателен для Controller, Implementer, Reviewer и Verifier. Единица
работы — один ticket с явными проверяемыми acceptance criteria.

## Полномочия и запрет вложенной оркестрации

- **Controller** выбирает ticket, фиксирует baseline, оценивает риск, создаёт
  агентов, ведёт журнал цикла и присваивает `DONE`, `BLOCKED_FOR_DESIGN` или
  `BLOCKED`.
- **Implementer** реализует один ticket, пишет или изменяет targeted-тесты и
  возвращает `IMPLEMENTED`, `BLOCKED` либо `NEEDS_CLARIFICATION`.
- **Reviewer** без изменения файлов возвращает отдельные вердикты `SPEC` и
  `CODE_QUALITY`.
- **Verifier** запускает acceptance-проверки и возвращает `ACCEPTED` либо
  `REJECTED` по фактическому evidence.

Только Controller создаёт subagents. Implementer, Reviewer и Verifier не
создают и не делегируют работу другим subagents. Reviewer выполняет обе оси
review самостоятельно и не запускает внутри себя дополнительные SPEC или
CODE_QUALITY agents.

## Preflight до реализации

Controller выполняет preflight до запуска Implementer:

1. Читает `AGENTS.md`, `context.md`, ticket и связанную спецификацию.
2. Самостоятельно определяет рабочую папку, тип репозитория, ветку и commit и
   фиксирует baseline для diff.
3. Для каждого acceptance criterion указывает конкретное наблюдаемое evidence.
4. Отделяет готовые требования от нерешённых архитектурных решений.
5. Оценивает сложность и риск и выбирает модели по матрице ниже.
6. Записывает ожидаемую область изменений: компоненты, примерные файлы и
   запрещённые соседние подсистемы.
7. Объявляет условия ранней остановки цикла.

Если критерий требует определить новый state owner, cancellation/timeout
semantics, необратимый side effect, новый production seam или неизвестную
границу безопасности, ticket получает `BLOCKED_FOR_DESIGN`. Сначала проводится
отдельное архитектурное исследование без production-реализации.

## Классификация риска

Ticket считается **критичным**, если одновременно присутствуют два или более
признака:

- security boundary, auth, secrets или privacy;
- concurrency, thread affinity, cancellation или race condition;
- OS/native integration, UI Automation, COM, driver или installer;
- необратимый внешний side effect;
- изменение архитектурного seam или state owner;
- история Git или другое труднообратимое изменение данных.

Критичный ticket не маршрутизируется как обычная многофайловая реализация.

## Выбор моделей

| Работа | Модель | Effort |
|---|---|---:|
| Controller обычного ticket | Terra | medium |
| Controller при конфликте требований или архитектурном adjudication | Sol | high |
| Ясная механическая реализация на 1–2 файла | Luna | high |
| Обычная многошаговая реализация | Terra | medium |
| Сложная интеграционная реализация | Terra | high |
| Критичная security/concurrency/OS реализация | Sol | high |
| Reviewer обычного diff | Terra | medium |
| Reviewer сложного diff | Terra | high |
| Reviewer критичного diff | Sol | high |
| Verifier обычного ticket | Terra | medium |
| Verifier integration/UI/installer/security | Terra | high |
| Архитектурное исследование или adjudication | Sol | high |
| Финальная проверка критичного релиза | Sol | high |

`xhigh` используется только после измеримого недостатка `high` и с явным
обоснованием. `max` не входит в стандартный процесс.

Перед каждым запуском агента Controller выводит:

```text
Раунд: <номер>
Роль: <роль>
Сложность: <простая | обычная | сложная | критичная>
Риск: <низкий | средний | высокий | критичный>
Модель: <Luna | Terra | Sol>
Reasoning effort: <уровень>
Причина выбора: <краткое объяснение>
Эскалация: <нет | предыдущая конфигурация -> новая конфигурация>
Причина эскалации: <причина либо «не применимо»>
```

## Основной цикл

1. Controller завершает preflight и создаёт одного Implementer.
2. Implementer сначала создаёт или уточняет targeted red-capable test для
   изменяемого поведения, затем реализует ticket и запускает только необходимые
   targeted-проверки.
3. После `IMPLEMENTED` Controller создаёт одного независимого Reviewer.
4. Reviewer проверяет `SPEC` и `CODE_QUALITY`. Он не запускает полный test suite
   и не создаёт других агентов.
5. При `SPEC: FAIL` или `CODE_QUALITY: FAIL` Verifier не запускается. Controller
   выполняет adjudication findings и либо запускает fix-раунд, либо переводит
   ticket в `BLOCKED_FOR_DESIGN`.
6. Только после `SPEC: PASS` и `CODE_QUALITY: PASS` Controller создаёт
   Verifier.
7. Verifier запускает targeted acceptance checks, затем один полный test suite,
   если он требуется проектом, и необходимые live-проверки.
8. `ACCEPTED` позволяет Controller сохранить evidence и присвоить `DONE`.
9. `REJECTED` запускает scoped fix только по подтверждённым требованиям.

## Adjudication findings

Reviewer классифицирует каждый finding:

- `SPEC_VIOLATION` — прямо нарушено существующее требование;
- `REGRESSION` — сломано ранее подтверждённое поведение;
- `QUALITY_BLOCKER` — дефект создаёт конкретный correctness/security risk;
- `NEW_REQUIREMENT` — требование отсутствует в ticket/spec;
- `DESIGN_GAP` — требование существует, но необходимое архитектурное решение не
  определено.

`NEW_REQUIREMENT` не передаётся Implementer автоматически. Controller запрашивает
решение владельца требований или создаёт новый ticket. `DESIGN_GAP` переводит
текущий ticket в `BLOCKED_FOR_DESIGN` и запускает отдельное исследование без
дальнейшего production-кода.

## Здоровье исправительного цикла

Пять fix-раундов — аварийный абсолютный предел, а не ожидаемая длина цикла.

- Fix-раунд 1 может выполнять исходный Implementer по конкретным findings.
- После каждого fix выполняется один scoped re-review только исправленных
  findings и связанных regressions.
- Второе появление одной и той же пары «тип finding + корневая причина»
  немедленно переводит ticket в `BLOCKED_FOR_DESIGN`.
- После двух неуспешных fix-раундов Controller останавливается и запрашивает
  явное разрешение пользователя на продолжение. Автоматический третий раунд
  запрещён.
- Раунды 3–5 возможны только после такого разрешения, со свежим Implementer и
  пересмотренной моделью/архитектурным решением.
- После пятого неуспешного fix-раунда присваивается `BLOCKED`.

Controller также останавливает цикл как `BLOCKED_FOR_DESIGN`, если:

- фактическое число затронутых файлов более чем вдвое превышает объявленную
  верхнюю границу;
- изменение вышло в новую подсистему или изменило state owner;
- зелёные тесты дважды не обнаружили один и тот же production-seam bypass;
- агенту требуется придумать отсутствующую cancellation/timeout semantics;
- usage limit прервал writer и оставил partial diff, который нельзя безопасно
  продолжить без нового preflight.

## Ограничение контекста и стоимости

- Каждый role-agent получает ticket, нужный фрагмент спецификации, список
  файлов, текущие findings и команды проверки, а не полную историю Controller.
- Агент использует только skills, необходимые его роли. Перекрывающиеся process
  skills не загружаются одновременно без конкретной причины.
- Role-agent не использует orchestration skills и не создаёт subagents.
- В рабочей папке одновременно действует только один writer.
- Полный suite не запускается Implementer после каждого fix. Он запускается
  Verifier после успешного статического review.
- Повторный полный suite допустим только после verifier-level failure и
  последующего исправления, способного затронуть широкую регрессию.
- Controller ведёт компактный внешний checkpoint, чтобы после паузы не
  восстанавливать состояние из всей истории chat.

## Возобновление после паузы или usage limit

1. Не считать прерванного агента завершившим работу.
2. Зафиксировать текущий diff, изменённые файлы и незавершённые тесты без
   отката пользовательских изменений.
3. Сопоставить partial diff с последним checkpoint и проверить, не сработало ли
   условие `BLOCKED_FOR_DESIGN`.
4. Если архитектурное решение отсутствует, не запускать нового Implementer.
5. Если решение зафиксировано и scope остаётся прежним, создать свежего
   Implementer с минимальным handoff: baseline, diff, RED/GREEN состояние и
   нерешённые findings.

## Отчёт Implementer

```text
Статус: IMPLEMENTED | BLOCKED | NEEDS_CLARIFICATION
Ticket: <ID>
Baseline: <значение>
Изменённые файлы: <список>
Acceptance criteria: <критерий -> изменение>
Добавленные или изменённые тесты: <путь, название, что проверяет>
Targeted-команды и результаты: <команда, exit code, результат>
Не выполнено или не проверено: <явный список>
Риски и допущения: <список>
```

## Отчёт Reviewer

```text
SPEC: PASS | FAIL
CODE_QUALITY: PASS | FAIL
Fixed point и diff: <значения>
Acceptance criteria: <критерий -> evidence>
Findings: <тип, severity, файл/область, проблема, требуемый результат>
Новые требования или design gaps: <список>
Непроверенные риски: <список>
```

## Отчёт Verifier

```text
EXECUTABLE_VERIFICATION: PASS | FAIL | NOT_RUN
Итог: ACCEPTED | REJECTED
Acceptance criteria и evidence: <критерий -> evidence>
Команды и результаты: <команда, exit code, результат>
Полный suite: <один запуск либо обоснование отсутствия>
Live-проверки: <сценарий и результат либо причина NOT_RUN>
Непроверенные риски: <список>
```

## Evidence перед DONE или BLOCKED

Controller сохраняет:

- идентификатор проекта и ticket;
- baseline и итоговый commit/diff;
- acceptance criteria и evidence;
- targeted и full-suite команды с exit codes;
- тесты, написанные или изменённые Implementer;
- модели и effort всех агентов;
- журнал findings, adjudication и эскалаций;
- число fix-раундов;
- условия ранней остановки и факт их срабатывания;
- итоговый статус `DONE`, `BLOCKED_FOR_DESIGN` или `BLOCKED`.

`DONE` разрешён только после независимого `SPEC: PASS`, `CODE_QUALITY: PASS` и
достаточного `ACCEPTED` evidence.
