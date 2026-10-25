# InsightService

## Структура кода

Один web-проект `Api` и отдельный `Tests`. Основа структуры —
[CompanyService](https://github.com/AlfaHackathon/CompanyService/tree/f6c3413c1c6dc536b31fe2a24ad272b6c6c358cd/Api)
и [SQL resources UserService](https://github.com/AlfaHackathon/UserService/tree/23b88c7cd14a1d4d0b8e07968533f896121c74f7/Api/Resources).

| Путь | Ответственность |
|---|---|
| `Api/Program.cs`, `Extensions`, `Middleware` | Композиция/DI, internal authentication и обработка ошибок |
| `Api/Controllers` | Настоящий MVC: `[ApiController]`, `BaseController`, `HttpGet/HttpPost`, explicit body/query/route binding |
| `Api/Contracts/*` | Wire DTO: analyses, calculations, context, questions, news jobs, source reactions |
| `Api/Services`, `Models/Policies` | Анализ, нормализация, проверка источников, Decimal arithmetic, applicability и question identity |
| `Api/Repositories/EffectRepository.cs` | Координатор одной атомарной транзакции receipt + revisions + heads |
| `Api/Repositories/EffectReader.cs`, `EffectRevisionWriter.cs` | Чтение истории/состояния и запись ревизий в транзакции координатора |
| `Api/Repositories/NewsJobRepository.cs`, `SourceCursorRepository.cs`, `SourceReactionRepository.cs` | Leases/checkpoints/audit, CAS cursor, invalidation и выбор затронутых вопросов |
| `Api/Resources`, `Migrations` | PostgreSQL SQL-ресурсы `.resx`, типизированные accessors, schema/backfill |
| `Api/Common`, `Models/Persistence` | Общие сериализация/протокол/validation и внутренние persistence-модели |

Репозитории используют организационный пакет `AlfaHackaton.DapperContext 1.2.0`: `IDapperContext.BeginTransaction`, `ITransaction`, `QueryObject`. Все чтения внутри операции идут через ту же транзакцию. Согласованное расширение пакета добавляет `ITransaction.FirstOrDefault<T>`, `ListOrEmpty<T>` и `CommandWithCount`: прямые вызовы Dapper на том же соединении и транзакции. `Repositories/Postgres/InsightTransaction` только открывает транзакцию и берёт прежний advisory lock; собственного reader, JSON row envelope и мутации параметров нет. SQL возвращает типизированные persistence DTO с явными aliases; параметры передаются через `new { ... }` отдельно от SQL. JSON payloads и точные bigint ticks в базе сохранены.

`AlfaHackaton.Result 1.0.0` явно подключён: ожидаемые ошибки валидации, бизнес-правил и конфликтов возвращаются через `Result<T>`/`Result` и пакетный `Error`. Контроллеры напрямую наследуют `AlfaHackaton.Controllers 1.4.1` / `BaseController`; его `OkOrProblem<T>` возвращает успешный DTO либо `{errorMessage}`. При ошибке заданный `Error.HttpStatusCode` имеет приоритет, иначе действует mapping `ErrorType`. Собственных `ApiError` и controller adapter нет. Ранний возврат ошибки из транзакции оставляет её без commit; disposal откатывает изменения. Middleware обрабатывает authentication, ошибки HTTP/БД и неожиданные исключения с прежним `{code,message}`.

`AlfaHackaton.HttpClient 1.1.1` реально выполняет обращения к ContextService: `Response.Result`, timeout 15 секунд, без redirect, credential задаётся только для исходного запроса, максимальный буфер ответа 4 MiB. Никаких новых LLM-вызовов это внедрение не добавляет.

Основное хранилище — PostgreSQL. SQLite runtime/fallback отсутствует. Схема создаётся FluentMigrator через организационные `AddDapper/AddMigrations/UseMigrations`; startup требует connection string. Имена таблиц и TEXT JSON payload сохранены для переноса истории. Ticks/version/sequence имеют BIGINT, `question_validity_events.event_id` — BIGSERIAL. FK — DEFERRABLE INITIALLY IMMEDIATE для атомарного импорта старой истории. SQL-функция времени сохраняет все 7 дробных знаков .NET отдельно от PostgreSQL timestamp, включая timezone offset.

Каждая repository operation владеет одной транзакцией. Транзакционный advisory lock `714103201` сохраняет прежние последовательные переходы heads/receipts/leases и согласованные чтения. Это консервативная семантика, а не обещание высокой пропускной способности; увеличение параллелизма потребует отдельного дизайна и race-тестов. Пакет DapperContext не предоставляет cancellation token для команд БД; HTTP-резолвер учитывает cancellation token.

## Пакеты, запуск и проверки

Обычный restore использует GitHub Packages организации `https://nuget.pkg.github.com/AlfaHackathon/index.json` и nuget.org согласно InsightService/NuGet.Config. Credentials настраиваются вне репозитория штатным NuGet credential provider или защищённым окружением `NuGetPackageSourceCredentials_github=Username=<github-login>;Password=<PAT>;ValidAuthenticationTypes=Basic`; токены не помещаются в исходники. Ссылки на локальные source-built feeds не являются конфигурацией запуска.

Команды из каталога InsightService:

```powershell
dotnet restore InsightService.slnx
dotnet test InsightService.slnx -c Release --no-restore
dotnet publish Api/InsightService.Api.csproj -c Release --no-restore -o ./publish
# ConnectionStrings__Postgres: явная строка подключения PostgreSQL.
# Internal__Secret: internal credential, задаётся окружением процесса.
# ContextService__BaseUrl: http://127.0.0.1:5181 (корень host).
# ContextService__InternalSecret: отдельный credential либо Internal__Secret.
# CompanyService__BaseUrl: корень host CompanyService.
# CompanyService__InternalSecret: отдельный credential либо Internal__Secret.
dotnet run --no-build --project Api/InsightService.Api.csproj --urls http://127.0.0.1:5182
```

`Storage__ConnectionString` также поддерживается как явное имя настройки; `Storage__Path` больше не выбирает SQLite. Старую базу нужно переносить отдельным проверяемым importer до переключения приложения.

Владение правилами проекций находится в InsightService: `POST /insight-service/internal/projections/canonical-facts`, `/groups`, `/role` возвращают канонические факты, приоритеты, точные групповые суммы и разрешённые ролевые представления. Входящие receipts служат только идентификаторами: значения и актуальные головы перечитываются из собственной БД на `asOf`. Группы и явные ролевые grants разрешаются через CompanyService; доступ к исходникам, hash и время версии проверяются через ContextService. Сведения о компаниях, людях и группах принадлежат CompanyService.

`POST /insight-service/internal/analyses/canonical-company`, `/applicability`, `/prepare`, `/depth` выполняют выбор канонического receipt, атомарную запись неглубокой оценки, нормализацию устойчивого вопроса/предшественника и выбор глубины. `/depth` получает собственную инвентаризацию ContextService и профиль CompanyService; переданная агентом инвентаризация не определяет решение. `/validate-applicability` проверяет до 32 предварительных candidates через ту же политику и точные исходники до сохранения: агент получает ошибку для исправления ответа модели без собственной копии правил. Сохранение `/analyses` остаётся окончательной проверкой источников и применимости. `GET/POST /source-cursor?source=company` хранит отдельный checkpoint изменений CompanyService; без параметра сохраняется прежний курсор ContextService.

`POST /insight-service/internal/execution/company-plan` принимает `jobId`, `companyId` и необязательную модельную оценку relevance. Правила выбора механизмов, глубины, bank profile и canonical/applicability fallback читают сохранённый job и данные владельцев. До сохранения watch cursor операция не записывает shallow receipt. `/execution/company-watch` и `/execution/source-jobs` возвращают готовые задания реагирования; проверка события сверяет его с журналом владельца, enqueue и CAS checkpoint выполняет worker.

`POST /insight-service/internal/projections/job` читает persisted job/checkpoint и возвращает итог вместе с разрешёнными входами для ролевой модели. Второй вызов принимает только proposals действий. `scopeVersion` связывает действия с точными каноническими ревизиями, группами и grants: устаревшая отправка получает 409; устаревший checkpoint модели требует нового reasoning. Итоговые facts, unknown/partial coverage, суммы и missing scopes вычисляет сервис, а не агент.

Тесты используют настоящий PostgreSQL: `INSIGHT_TEST_POSTGRES` — admin connection string с правом CREATE DATABASE только на изолированном тестовом instance. Если переменная отсутствует, тестовый fixture ожидает disposable localhost:55441/database=postgres/user=postgres. Каждая test instance создаёт собственную `insight_test_<random>` БД, прогоняет организационные миграции и удаляет только эту БД после проверки. Production DSN тестам не передаётся.

Перенесённые тесты сохраняют проверки question identity, correction/replay, неизвестности, immutable receipt, сериализованных writes, rollback, checkpoints и lease/CAS. Дополнительно проверены MVC HTTP binding/errors, конкурентный HTTP cursor CAS, настоящий org HTTP клиент с redirect rejection/timeout/status/headers и точные SQL ticks. SQL embedded в DLL; внешний resx при запуске не нужен.

## Устойчивые вопросы

Текущий `/analyses` принимает `draft.question={mechanism,frame,unit,targetPeriod,baselinePeriod,basis}`,
`draft.assessment={conditions,unknowns}`, `expectedStateVersion`, optional `mechanisms` и
`trigger={materialId,versionId}`. Идентичность вопроса зависит от компании, буквального объекта,
механизма и рамки сравнения; материал новости в неё не входит. `GET /effects` возвращает
`{effects,stateVersion}`: канонические вопросы компании плюс legacy effects указанной новости.
Независимые новости продолжают фактическую историю вопроса; correction проверяет lineage своего
новостного материала, а predecessor всегда фактический head всего вопроса.

Receipt уникален по company/news material/news version/optional source trigger. Replay сохраняет
исторические ревизии и не сдвигает head. Before snapshot проверяется вместе с optimistic heads
в одной write-транзакции. `mechanisms` ограничивает пересмотр и список omitted questions;
при отсутствии scope список остаётся консервативным. Legacy effects/receipts сохранены:
автоматически угадывать механизм старого coarse risk при миграции нельзя.

`POST /source-changes` помечает зависимые вопросы как `requiresReassessment` и возвращает
страницу до 64 адресных работ. Пометка снимается при записи оценки со ссылкой на новую версию.
`GET /question-recipients` возвращает до 256 прежних адресатов с cursor.
`GET/POST /source-cursor` хранит CAS checkpoint доставки изменений.
News job `partial` допускает новую lease и продолжение незавершённой очереди.

Для новых количественных вопросов `conclusion` отображает вычисленный Decimal, единицы, период
и рамку; исходный текст модели сохраняется отдельно как `modelExplanation` для аудита.
Это предотвращает расхождение цифр в отображении и выражении, сохраняя отдельную проверку
семантики условий и выбранной формулы. См. `Tests/QuestionHistoryTests.cs` и `Tests/NewsJobsTests.cs`.

News flow сохраняет `contextComplete` из внутреннего analysis request в immutable receipt.
Это заявленная полнота retrieval-набора на момент анализа, а не оценка истинности вывода;
отсутствующее поле старого receipt означает неизвестную полноту. В той же транзакции
вычисляется `unrevisedEffectIds`: прежние effects этой компании/новости, не обновлённые
данной версией. Они остаются в каноническом хранилище со своими исходными ревизиями;
групповая проекция не считает их отсутствующими или нулевыми. Replay возвращает сохранённые
coverage/gaps, даже когда головы effects уже изменились следующей коррекцией.

Канонические последствия новости сохраняются .NET 10 сервисом в PostgreSQL через организационный DapperContext. Источники проверяются через ContextService, арифметика выполняется Decimal. Reasoning и история чата не владеют этим состоянием.

## Запуск

`GET /health` возвращает service/status. Все `/insight-service/internal/*` требуют непустой настроенный `X-Internal-Secret`; реальные membership/document ACL этим не реализованы.

## HTTP

| Метод и путь | Результат |
|---|---|
| POST `/insight-service/internal/analyses` | Атомарный пакет drafts и durable completion receipt |
| GET `/insight-service/internal/analyses?companyId=...&newsMaterialId=...&newsVersionId=...` | Сохранённый receipt или 404 |
| GET `/insight-service/internal/effects?companyId=...&newsMaterialId=...` | `{effects:[EffectResponse],stateVersion}` с актуальными head и историей |
| GET `/insight-service/internal/effects/{effectId}` | Один EffectResponse с актуальной ревизией и историей |

POST body:

```text
{companyId,bankProfileId?,newsMaterialId,newsVersionId,asOf,
 drafts:[{effectKey,literalObjectRef,expectedPreviousRevisionId:null|string,
          conclusion,status,sourceRefs:[{materialId,versionId,contentHash}],calculation?}]}
```

Receipt: `{analysisId,companyId,bankProfileId,newsMaterialId,newsVersionId,asOf,completedAt,duplicate,resultSemantics,effects:[{effectId,revisionId,previousRevisionId,duplicate,currentRevision}]}`. `resultSemantics` всегда `saved_analysis_revisions_not_latest_heads`: `currentRevision` внутри receipt — ревизия, сохранённая **этим анализом**, и после будущего исправления становится исторической. Текущие head нужно читать через GET effects. Идемпотентный повтор старого анализа не откатывает состояние. GET receipt возвращает исходную отметку, а повтор POST помечает `duplicate:true`.

EffectResponse: `{effectId,effectKey,companyId,bankProfileId,newsMaterialId,literalObjectRef,currentRevision,history:[revision...]}`. Revision: `{revisionId,previousRevisionId,newsVersionId,asOf,conclusion,status,sourceRefs,calculation,calculationResult,calculationArithmeticOnly,createdAt}`. Денежный `calculationResult` — **строка Decimal** или null, не объект `{value}`. Unit/period остаются внутри calculation. History упорядочена от первой ревизии к последней.

## Инварианты

- Legacy effect identity: SHA256 от однозначно сериализованного `(companyId,newsMaterialId,effectKey)`. Канонический question key строится по объекту/механизму/рамке/единицам/периодам/basis и не содержит news material. Guid компании и строка банковского профиля различаются. Нельзя перепривязать вопрос к другому буквальному объекту или bank-profile bridge.
- Correction использует actual latest previousRevisionId и news-version, цепочка исправлений которой достигает прежней новости. Optimistic head update выполняется в одной write-транзакции со всеми revisions и receipt. Stale prior даёт 409; ни один частичный результат не коммитится.
- Receipt уникален для company/news material/news version, включая `drafts:[]`. Повтор того же канонического запроса проверяется до stale prior и возвращает сохранённый receipt. Конфликтующий payload даёт 409. Пропущенный в новом пакете старый effect остаётся сохранённым; отсутствие не равно нулю или отмене.
- Каждый draft обязан цитировать точную текущую news-version с её hash. Все ссылки разрешаются настоящими HTTP-вызовами к ContextService; проверяются hash, knownAt и company Guid scope. Отсутствующий/будущий/чужой/изменённый источник даёт 422. Недоступный upstream даёт 502/503 и не превращается в пустой успех. Проверка доступности/идентичности текста не доказывает смысловую правильность вывода.
- HTTP-клиент источников запрещает автоматические редиректы: ответ 30x даёт 502, внутренний credential не пересылается на destination. `Tests/ContextRedirectTests.cs` проверяет production DI-registration двумя настоящими loopback TCP-серверами; второй origin не получает ни запроса, ни заголовка.
- Один анализ ограничен 32 drafts, 64 refs на draft и 128 разрешёнными source versions. Все literals/поля ограничены. Внутренний caller обязан передать доверенный company scope; реальные membership/document ACL этим не реализованы.

`calculation={expression,unit,period}`; expression — `{kind:"value",value:"150"}` либо `{kind:"add|subtract|multiply|divide",left:expression,right:expression}`. Максимум глубины 16 и 255 узлов. Литералы должны точно представляться Decimal; код/NaN/exponent не принимаются. Overflow и деление на ноль дают 422. Арифметика Decimal конечной точности; `calculationArithmeticOnly:true` не подтверждает выбранные моделью основания, период или знак. Status задаётся автором и не меняется калькулятором автоматически.

PostgreSQL-операции находятся за `IEffectRepository`; все reads/writes одного изменения выполняются в одной организационной транзакции.

`Tests/AnalysisTests.cs` проверяют correction15600→6240 с прежним effectId, повтор старой версии без отката, persistence/empty receipt, конфликт literal/stale prior, недопустимые citations, concurrent identical analyses, rollback пакета и арифметические границы. Детерминированная пауза на source resolution проверяет commit конкурента между первым чтением receipt и чтением head: одинаковый canonical request получает duplicate, отличающийся — 409. В этих component tests source resolver управляемый; настоящий HTTP resolver и три процесса проверяются отдельным интеграционным harness.

## Financial source binding for new calculations

New monetary drafts with a question require a nonblank `impact.economicObjectId`
that occurs as a complete identifier in an exact cited own exposure document
(`assessment.applicability.sourceRefs`). Public news and `provided-bank-csv`
cannot establish this binding. Source company, material/version and hash must match.
Preparation and final save enforce the same policy; failures return HTTP 422.

For `frame=budget_delta`, `baselinePeriod` must identify `budget:<period>`.
An own exposure document must also support a condition with
`predicate=budget_baseline`, `status=supported`, `assertion=observed`, an empty
`dependsOn`, and a `fact` copied exactly from that document. The excerpt must
contain the exact budget identifier and economic object identifier. For example,
an actual source excerpt `Approved budget:2027 for Loan-C-001: interest RUB 120000.`
can bind that budget to that loan. The model's explanation, an ordinary loan
rate, or a year appearing independently in a contract does not establish a budget.
Documents without an explicit budget identifier cannot currently support this
frame; preserve a non-budget comparison or missing-data question instead of
inventing a budget identifier in the source.

This is a conservative documentary binding gate, not a natural-language verifier:
an exact excerpt still needs to assert the claimed budget, and arithmetic remains
the calculator's responsibility. It does not establish completeness of company
exposure, independently verify every contractual term, or infer object aliases.
Qualitative questions, historical non-question writes, historical reads and
already-completed receipt replay retain their existing behavior.
