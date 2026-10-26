# Публичное чтение инсайтов для фронтенда

Этот согласованный integration candidate объединяет owner API `9edf45fb` и shared category contract `7720106` в отдельной ветке `codex/insight-owner-category-integration-20261025`. Наличие кода/локальных HTTP-тестов не означает публикацию маршрутов на production. Gateway/nginx ingress проверяется отдельно после релиза. Новых портов, CORS, ACL и генерации эта ветка не добавляет.

## Вызовы

Внешний клиент использует Bearer через существующий AuthGateway. Insight получает проверенный `X-User-Id` внутри доверенной сети и сам запрашивает текущий membership CompanyService. Клиент не присылает `companyId`, `personId`, `asOf` или внутренний секрет. Direct Insight ingress не предназначен для публичного доступа.

| Метод и путь | Для чего |
| --- | --- |
| `GET/HEAD /insight-service/insights` | Главная: последние 5 personal-карточек; лента с фильтрами |
| `GET/HEAD /insight-service/insights/{kind}/{id}` | Деталь personal или general; ID брать из ленты |
| `GET/HEAD /insight-service/insights/personal/{id}/history` | Отдельная страница ревизий personal; общая история general пока отсутствует |

HEAD использует те же проверки membership, источников и query, что GET; успешный ответ имеет 200 и пустое тело.

Лента: `kind=personal` по умолчанию либо `general`; `limit=1..20` (по умолчанию 5), `cursor` (не разбирать), `newsMaterialId`, `category=risk|opportunity|event` для personal и general. Категорию можно не передавать: тогда включены все значения, включая legacy null. Пустые/неизвестные значения, `category=null` и повторные параметры дают 400; фильтра только null нет. Для personal также `status` (сохранённая строка, не новый UI enum), `mechanism` (из существующего question enum), `minImportance=1..5` (только новая обоснованная оценка). History: `limit=1..20`, по умолчанию 10, `cursor`. У detail query-параметров нет. Неизвестные и повторные параметры отвергаются.

```http
GET /insight-service/insights?kind=personal&limit=5&minImportance=3
Authorization: Bearer <client-token>
```

Ответ ленты: `{companyId, items, nextCursor, exhaustive:false}`. Personal сортируется по времени создания текущей ревизии DESC, затем ID DESC. General: последний разрешённый completed/partial job для каждой пары news/version, затем updatedAt/job ID/индекс DESC. History сортируется по sequence DESC. Курсор связан с компанией, видом и фильтрами (history — также ID). При изменении фильтров или после обновления данных начать с первой страницы; это живая лента, не фиксированный snapshot между запросами. `exhaustive:false` не является счётчиком или признаком полного поиска. Страница может быть пустой при ненулевом `nextCursor`: приватные по источникам карточки отфильтрованы, продолжайте курсор.

## DTO и отображение

Personal:

```text
kind: "personal"
id: canonical question/effect ID
companyId, newsMaterialId, literalObjectRef
currentRevision: {
  revisionId, previousRevisionId, newsVersionId, newsMaterialId?, asOf, createdAt,
  conclusion, status, category: "risk"|"opportunity"|"event"|null, publicationClass?, question?, assessment?, trigger?, sourceRefs,
  companyImportance: {score: 1..5|null, rationale, evidenceRefs} | null,
  impact: {metric, amountBasis, economicObjectId, evidenceStatus} | null
}
requiresReassessment: boolean
sources: SourceMetadata[]
indicators: Indicator[]    // 0 или 1
indicatorAvailability: {reason, explanation}
```

`currentRevision` — явный публичный allowlist, не сериализация внутренней записи целиком. Нет modelExplanation, raw AST/результата расчёта, legacy importance/urgency/novelty. Assessment сохраняет основания, условия и unknowns; статусы/прогнозы не превращаются в подтверждённые факты. Для history: `{companyId, insightId, items:[{revision, sources, indicators, indicatorAvailability}], nextCursor}`. Исторические права на источники проверяются по текущему membership; текущий requiresReassessment консервативно подавляет показатели истории тоже.

Importance: 1 низкая, 2 умеренная, 3 требует внимания, 4 высокая, 5 критичная. Это важность решения для этой компании, не confidence, размер денежного эффекта или знак риска/возможности. `score:null` означает не оценено; rationale объясняет причину. Внешний `companyImportance:null` означает отсутствие нового assessment в старой записи. Старый `impact.importance` не используется как fallback и не показывается вторым числом. Массового backfill нет. При `requiresReassessment=true` рядом с исторической оценкой показать необходимость пересмотра, не выдавать её за свежую.

General:

```text
kind: "general"
id: opaque stored job/index reference
companyId, newsMaterialId, newsVersionId, asOf, updatedAt
textCompleteness: "full" | "lead" | "unknown"
companyStatus: "completed" | "failed" | "pending"
requiresReassessment: null
insight: {title, conclusion, mechanism, category: "risk"|"opportunity"|"event"|null, applicability[], exclusions[], unknowns[], sourceRefs[], signalIds?, quality?}
sources, indicators: [], indicatorAvailability
```

Это существующий общий результат job, отфильтрованный отдельно для текущей компании. Нет новой сущности/ревизий general, checkpoint, receipt других компаний, role views или общего companyImportance. `quality.priority`, если есть, относится к общему выводу и не заменяет companyImportance. `requiresReassessment:null` означает, что текущая актуальность общего snapshot не оценена; asOf/версия сохранены честно. Старые разрешённые general ID продолжают открываться, даже если в ленте уже появился новый job. General-показатели компании не вычисляются.

## Условный блок показателей

`indicators` — projection одного существующего сохранённого расчёта. Нет обязательного EBITDA/margin, генерации метрик или повторного вычисления. Например, иллюстративное синтетическое значение:

```json
{
  "name": "Изменение расходов",
  "metric": "expense_delta",
  "value": "15600",
  "unit": "RUB",
  "period": "2026-11",
  "baselinePeriod": null,
  "evidenceStatus": "conditional",
  "sourceRefs": [{"materialId":"synthetic-own","versionId":"v1","contentHash":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"}],
  "arithmeticOnly": true
}
```

Отображать value как Decimal string, не считать его JavaScript float. `"0"` — настоящий сохранённый ноль, неизвестное значение не становится нулём. Conditional означает условный расчёт; `arithmeticOnly:true` подтверждает только арифметику, не наступление события/оплату. Название, единица, период, baseline и evidence сохраняют смысл исходного расчёта. Для budget_delta без известной базы, неизвестного периода, неподтверждённого собственного основания, stale или отсутствующего расчёта список пуст. `indicatorAvailability.reason`: available / no_calculation / missing_result / stale_sources / insufficient_evidence / unknown_period / unknown_baseline / general_not_company_calculation; explanation пригодно для объяснения отсутствия блока.

## Источники и ссылка на оригинал

SourceMetadata: `{materialId,versionId,contentHash,sourceId,kind,title,knownAt,publishedAt,provenance}`. Provenance nullable: `{acquisition,sourceUrl,retrievedAt,textCompleteness,originVersionId}`. Полный текст, ACL arrays и internal locator не возвращаются. URL только из `MaterialVersion.provenance.sourceUrl`, только HTTP(S) без username/password; при отсутствии/неподходящем URL — null, из ID ничего не строится. Открытие ссылки фронтом не означает, что backend загрузил полный publisher original; lead/excerpt/unknown остаются такими.

Точный существующий внутренний resolve:

```http
GET /context-service/internal/material-versions/{materialId}/{versionId}
X-Internal-Secret: <service-only>
```

Для новости также существует внутренний `POST /context-service/internal/news/resolve` с `{newsMaterialId,newsVersionId,asOf}`. `assistant_read_news` и `assistant_read_original` уже возвращают `original.provenance.sourceUrl`. Эти внутренние endpoints не предназначены для браузера; фронт берёт ссылку из публичного `sources[].provenance.sourceUrl` после релиза facade. Bank position не выводится из AI-текста и отдельного искусственного bank-position поля здесь нет.

Перед выдачей каждой карточки Insight перечитывает точные версии источников и проверяет IDs/hash/knownAt<=saved asOf и текущий company/bank scope. Для general допускаются только публичные news/market без company/bank ACL и обязательный точный news anchor. Private-source карточка пропускается в feed; detail/history возвращают 404. Временная недоступность источника — ошибка, не успешная пустая лента.

## Ошибки, нагрузка и эксплуатация

Все успешные/ошибочные read-ответы `Cache-Control: private, no-store`. Public identity rejection использует существующий `{error:"Gateway user context required"}` (401); route errors — `{code,message}`:

| HTTP | Смысл |
| --- | --- |
| 400 | INVALID_REQUEST / INVALID_QUERY / INVALID_CURSOR: параметры или scope/filter binding курсора |
| 401 | Нет доверенной identity либо gateway отверг Bearer |
| 403 | COMPANY_MEMBERSHIP_REQUIRED |
| 404 | INSIGHT_NOT_FOUND: отсутствующая или недоступная по текущим правам карточка/история |
| 502 | INSIGHT_READ_CONTRACT / INSIGHT_SCOPE_MISMATCH / SOURCE_IDENTITY_MISMATCH / IMPORTANCE_EVIDENCE_MISMATCH / GENERAL_NEWS_ANCHOR |
| 503 | Зависимость/конфигурация недоступна, exact source отсутствует, либо SOURCE_READ_BUDGET (уменьшить limit) |

Не делать бесконечный retry; для 503 предложить повтор, source budget — меньшую страницу. GET не запускает jobs/LLM/embeddings/search/acquisition и не меняет доменное состояние. Максимум 64 уникальных точных версий на запрос, concurrency 4, общий deadline 15 секунд. SQL read-only с statement_timeout=2s. Полный source text читается внутренним adapter только для существующего material DTO, но не отдаётся фронту. При больших объёмах JSON feed queries могут упереться в timeout; дальнейшие индексы/projected read storage — отдельный шаг, не скрытая новая платформа.

Новые internal Insight маршруты: `/insight-service/internal/read/companies/{companyId}/personal`, `/personal/{id}`, `/personal/{id}/history`, `/general`, `/general/{id}` (везде GET + существующий internal secret middleware). Клиентский Bearer сам по себе не открывает internal API. Никаких изменений UserService, business role enum/default или login-user/person ACL mapping; отдельный контракт роли ведёт Артём.

## Релиз и ограничения

Новая реализация — owner API в InsightService. Существующие repository/SQL/cursors переиспользованы локально, без self-HTTP; новая БД, миграция и повторная генерация не нужны. `MaterialVersion` дополнен optional nullable provenance, отсутствующее поле не сериализуется и не меняет старые canonical payloads.

Source-кандидат отдельно от ingress/deployment. Gateway сейчас не маршрутизирует эти новые public reads. Подготовленный конфиг добавляет только GET/HEAD для трёх точных форм путей из таблицы выше в private InsightService, без catch-all; `/insight-service/internal/*` не открывается. JWT проверяет существующий Gateway, который заменяет пользовательские identity headers. Insight guard требует один canonical UUID `X-User-Id` до query/DB/source reads. Внутренний secret не заменяет user identity. Сам header не является проверкой JWT при прямом доступе; сервис должен оставаться внутри доверенной сети.

### Согласованный cutover и breaking change

1. Выполнить native build/regression и public contract smoke Insight внутри разрешённого существующего контура.
2. После успешных проверок remote-исполнитель применяет согласованный узкий Gateway ingress, проверяет Bearer/replaced identity, current membership, source scope и реальные HTTP DTO через внешний origin.
3. В согласованном релизном окне переключить frontend base path и выпустить Agent removal candidate.
4. Проверить, что старые Agent paths отвечают 404 и отсутствуют в Agent OpenAPI. Alias, redirect и fallback не добавляются.

Breaking change: **вместо старого `/agent-service/insights…` использовать `/insight-service/insights…`**. Существующие IDs, query и непрозрачные cursors не пересоздаются. Не удалять работающие Agent routes до новых smoke; после cutover не оставлять их как legacy API. Rollback — восстановление сохранённых предыдущих версий согласованного релиза, не постоянный public alias.

### Подтверждение будущего cutover

Сохранённое поручение пользователя (оно не означает, что production уже изменён):

> Реми: «Для переноса инсайтов дополнительно потребуется открыть в Gateway только новые GET/HEAD-маршруты с Bearer-проверкой и удалить старые из Agent. Разрешаешь это переключение после успешных проверок? Internal-ручки останутся закрыты.»
> Пользователь: «да»

Разрешение ограничено этим переключением после проверок. Internal routes, POST, публичные порты и credentials не меняются. Применение выполняет согласованный remote-исполнитель, не cloud source worker.

### Интегрированный shared category contract

Shared nullable поле и `InsightCategoryPolicy` из `7720106` интегрированы обычным merge без переписывания frozen веток. Personal `currentRevision.category`, history `items[].revision.category` и general `insight.category` сохраняют ровно записанное `risk|opportunity|event|null`; отсутствующее legacy значение остаётся null. Категория независима от `kind`, importance, evidence/status, источника и знака суммы. Неизвестная или смешанная направленность не превращается в event; inference и backfill отсутствуют. Подробная семантика: [insight-category.md](insight-category.md).

Category filter применяется в существующем SQL до пагинации: к текущим personal revisions и к general insights только после выбора последнего разрешённого job для news/version. Старый matching job не воскрешается. Cursor связан с category вместе с прежними company/kind/filters; смена или удаление category при продолжении даёт 400. Нефильтрованные старые cursors сохраняют прежний четырёхэлементный filter key. Интеграционные тесты проверяют known/null projection и history, raw-query валидацию и смешанные cursors. Привязка категории теперь реализована в source candidate; native package gate и Gateway/ingress smoke по-прежнему pending до фактической проверки.

### Проверки и честные ограничения

Новые `PublicInsight*Tests` используют fake dependencies и TestServer: trusted-header admission, current membership, scope/hash/time, nested evidence, budget/concurrency, precise Decimal strings, unknown/stale indicators, strict raw query, Swagger discriminated union и error contracts. Они не проверяют production JWT/ingress. Linked-source cloud harness заменяет организационные Result/Dapper интерфейсы; его PASS не является native build всего сервиса. Native package gate требует разрешённый executor с AlfaHackaton packages; known org-feed403 не обходится.

```sh
dotnet restore InsightService.slnx --configfile NuGet.config
dotnet build InsightService.slnx -c Release --no-restore
dotnet test Tests/InsightService.Tests.csproj -c Release --no-build --no-restore --filter 'FullyQualifiedName~PublicInsight'
dotnet publish Api/InsightService.Api.csproj -c Release --no-restore -o ./publish
```

Full existing PostgreSQL regression suite требует заранее разрешённый synthetic `INSIGHT_TEST_POSTGRES` fixture по инструкции проекта; в рамках cloud-переноса новые PostgreSQL/Compose/environments не создаются. Native release gate и ingress smoke до их фактического запуска остаются pending.

Swagger Insight: opt-in `/insight-service/swagger/index.html`, spec `/insight-service/swagger/v1/swagger.json`; public Bearer и internal secret описаны раздельно. Документация не расширяет доступ. Agent Swagger после cutover больше не содержит insight read paths.

Не реализованы write-actions/deepen/feedback/assignment, general revision history/current invalidation tracking, persona адаптация (ждём контракт Артёма), нормализованный UI status enum и общие bank-position labels. Обсудить карточку можно существующим чатом текстом со стабильным ID; структурированная card-action admission в этот slice не добавлена.
