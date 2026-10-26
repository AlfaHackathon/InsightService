# Обновления опубликованных инсайтов: frontend contract

Source candidate. Production deployment, network/auth settings и реальный ingress smoke выполняет root после native gates. Frontend в этой ветке не реализован.

## Подключение браузера

1. `POST /insight-service/insights/ws-ticket` с существующим `Authorization: Bearer <JWT>`, без body/query. Ответ `200`, `Cache-Control: private, no-store`:

   ```json
   {"ticket":"<64 lowercase hex characters>","expiresAt":"<UTC instant>","connectionExpiresAt":"<UTC instant>"}
   ```

2. До `expiresAt` открыть `wss://<тот же Gateway host>/insight-service/insights/ws` с двумя subprotocols: `insight.v1` и `insight.ticket.<ticket>`. Browser API: `new WebSocket(url, ["insight.v1", "insight.ticket." + ticket])`. Выбранный сервером subprotocol — только `insight.v1`, без credential.
3. После `ready` перечитать существующую ленту через GET. Это закрывает окно между baseline сервера и подключением UI. WebSocket не возвращает initial cards.
4. На `insight.upsert` обновить карточку через `GET /insight-service/insights/{kind}/{id}` и при необходимости перечитать `GET /insight-service/insights?kind=personal|general`. ID непрозрачный, в URL segment его нужно корректно кодировать.
5. На heartbeat отправить строго `{"eventType":"pong"}`. При disconnect получить новый ticket, переподключиться с backoff и перечитать GET. Старый ticket повторно не использовать.

JWT не передаётся в URL, cookie или WS subprotocol. Ticket тоже отсутствует в URL: он только в ответе POST и request `Sec-WebSocket-Protocol`. Не сохранять/логировать JWT, ticket, response body выдачи или credential-bearing protocol header. В исходниках нет logging этих значений; стандартные access logs могут сохранять только пути без секретных query. Root должен сохранить redaction заголовка `Sec-WebSocket-Protocol` и тела ticket response, если включает расширенный HTTP/ingress logging. HTTPS/WSS требуется на внешнем origin.

Браузер не задаёт `Authorization` для upgrade. Gateway сохраняет существующую JWT signature/issuer/audience/lifetime проверку для POST, очищает внешние `X-User-Id`, `X-User-Role`, `X-Insight-Ws-Expires` и передаёт expiry только из проверенного JWT. На точном WS path он очищает identity/expiry и пропускает только корректную форму ticket upgrade; сам credential атомарно проверяет InsightService до 101. Произвольные query, client companyId и wildcard/internal ingress отсутствуют. Browser Origin должен совпадать с публичным Gateway host; cross-origin policy/CORS не расширяются этой веткой. Route WS сохраняет original Host для такой же проверки в Insight.

## Сообщения

```json
{"eventType":"ready","time":"<UTC observed time>","expiresAt":"<connection deadline>"}
{"eventType":"insight.upsert","id":"<existing card id>","kind":"personal","revisionId":"<existing revision id>","version":"<opaque version>","time":"<UTC observed time>"}
{"eventType":"insight.upsert","id":"<existing card id>","kind":"general","revisionId":null,"version":"<opaque version>","time":"<UTC observed time>"}
{"eventType":"heartbeat","time":"<UTC observed time>"}
```

`insight.upsert` означает «новая или изменённая опубликованная карточка, перечитай GET». `version` непрозрачная строка для dedupe `(kind,id,version)`, не число и не глобальная последовательность. Personal version включает revision ID и current/stale assessment; general использует сохранённый updatedAt. `time` — момент наблюдения сервером, не время commit. Никаких private body, company IDs, source text, checkpoint/receipt или LLM tokens в сообщении нет. GET остаётся authoritative и может вернуть 404, если текущие права или состояние изменились.

Компания и bank scope берутся из существующего membership resolver при выдаче ticket и повторно проверяются до upgrade и при polling/отправке. Ticket привязан к user, company/bank membership и connection expiry; перед событием карточка проходит существующий public Detail с source ACL/hash/time проверками. Смена membership закрывает соединение; scope никогда не переключается на другую компанию внутри открытого socket.

`no_insight`, `internal_failed`, failed/pending, diagnostic `missing_data`, unknown evidence и legacy unknown/no-impact не создают карточечных событий. General events выдаются только для completed company status из существующих completed/partial published job reads. Нормализация, receipt и canonical projection чужой narrative-ветки не изменены. Дополнительный event gate совместим с её no-publication policy; при интеграции PR #13 нельзя откатывать её public Detail publication gate.

## Доставка и ограничения

Источник — committed read-модели существующей PostgreSQL, через `IPublicInsightReadStore`; это polling current heads, а не новая durable changefeed. Write hooks, новая таблица/миграция, брокер, Redis и LLM отсутствуют. Read-only queries видят только committed данные; изменённый кандидат повторно проверяется через public Detail до события. Concurrent revision retry переносится на следующий poll. Нет событий из in-flight transaction или внутренних failed receipts.

Poll каждые 5 секунд; стабильный порядок внутри poll — `kind`, затем `id` ordinal. Каждые 5 секунд выполняется полный ограниченный обход существующих cursor pages, поэтому поздний commit или обновление старой карточки не теряется за timestamp watermark. Межстраничного snapshot и порядка commit между соединениями нет; следующий poll перечитывает состояние. Несколько промежуточных ревизий могут схлопнуться до текущей. Exactly-once, durable replay, все промежуточные статусы и reconnect cursor не обещаются. Дедупликация сервером ограничена текущим соединением; после reconnect всегда GET.

Ограничения на соединение: 1000 scanned heads, 100 страниц, page size 20, poll budget 15 секунд, очередь 64 сообщения, send deadline 5 секунд. При превышении или dependency failure — resync/disconnect, не молчаливое отбрасывание событий. Heartbeat каждые 10 секунд; без pong 30 секунд — disconnect. Входящие application messages: один text frame до 256 байт, только pong. Все задачи отменяются и ожидаются; graceful close имеет budget 2 секунды, после которого возможен abort. `1013` — временная недоступность/slow client, `1008` — membership/pong policy, normal close — expiry/reconnect. При сетевом сбое close code может отсутствовать: восстановление одинаковое — новый ticket + GET.

Ticket: случайный 256-bit secret, хранится только SHA-256 key, TTL не более 30 секунд и JWT expiry, single-use consume под lock. Replay/expired/unknown ticket отклоняется до upgrade. Максимум 1024 outstanding tickets на процесс и 4 на user; превышение 429. Connection deadline — минимум JWT expiry и 15 минут от authenticated issuance request. Account access не продлевается.

Ticket store process-local: restart или выдача/upgrade на разных replicas дают rejection, после чего нужен новый ticket. Candidate рассчитан на существующий одиночный Insight instance; multi-replica требует affinity или отдельного согласованного решения, не добавленного в этой задаче. AuthService revoke/introspection в существующем auth отсутствует: JWT logout/revoke до exp не обнаруживается новым механизмом; membership проверяется заново.

## Проверки и release gates

Нативный restore обоих проектов в cloud: `NU1301`, proxy tunnel `403` для `nuget.pkg.github.com/AlfaHackathon/index.json`. Feed не обходился, production package references не подменялись. Native build/publish/full PostgreSQL regression остаются blocked до разрешённого executor с организационными пакетами и существующим synthetic DB fixture. Новые DB/Compose/environment не запускались.

Linked-source harness отдельно от production projects компилирует реальные WS, public read, policy, controller, middleware и Gateway source; подменяет только недоступные organizational Result/repository/Auth package seams явными fakes. Проверки JWT signature/issuer/audience/lifetime в Gateway source используют public JWT library; organizational validator fake не подтверждает native package behavior. TestServer проверяет настоящий browser-compatible ticket -> WS handshake, выбранный subprotocol и доставку revision event без custom Authorization header. Fake tests проверяют publication/ACL, dedupe, queue overflow, slow sender, heartbeat/pong, cancellation, expiry, membership changes, atomic ticket replay rejection и Gateway header/path/protocol admission. Linked PASS не является native build или production smoke.

Cloud result 2026-10-26: linked Release build/test PASS, **114 passed / 0 failed / 0 skipped**: 24 WebSocket/ticket tests, 16 Gateway tests, 74 existing public-read regressions. SDK 10.0.403/runtime 10.0.14; TestServer 10.0.13, public JWT library 8.14.0. Diff whitespace checks PASS. Два независимых review (spec/standards) закрыты после исправления heartbeat starvation и hostile duplicate-ticket protocol exception. Native restore обоих репозиториев — BLOCKED 403; native build/publish не подтверждены.

Root gates: интегрировать narrative PR #13 без отката, native restore/build/tests/publish, затем координированный deploy обоих сервисов. Проверить снаружи HTTPS/WSS: authenticated ticket, anonymous ticket denial, real JWT expiry, ticket replay/TTL, company/bank switch, source denial, корректный upgrade/heartbeat/reconnect GET и отсутствие internal/wildcard route. Production здесь не менялся.
