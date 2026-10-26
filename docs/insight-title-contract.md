# Versioned personal insight titles

Each newly published personal insight requires an agent-written `title`: nonblank, single line, at most 180 UTF-16 code units. It is separate from `conclusion`, not a truncation/formatter, and must not contain opaque internal identities. The original title/body are preserved without rewriting. Missing or malformed new published titles produce compact repairable `TITLE_REQUIRED`/`TITLE_INVALID` HTTP 422. Internal no_insight diagnostics require no fabricated title.

`EffectDraft.title` and `EffectRevision.title` are additive nullable fields. Null is omitted on internal request/revision serialization, preserving legacy receipt hashes. The persisted revision JSON contains the new title; no SQL column migration, backfill or historical update is performed. Legacy read titles stay null.

Frontend mapping on the existing public Bearer reads:

- Feed `GET /insight-service/insights`: personal `items[].currentRevision.title`.
- Detail `GET /insight-service/insights/personal/{id}`: `currentRevision.title`.
- History `GET /insight-service/insights/personal/{id}/history`: each `items[].revision.title`.
- General insights already carry `insight.title`; that independent existing contract is unchanged.

For example a new personal revision may expose `title: "Рост расходов по условиям займа"` and a separate grounded conclusion with sourced indicators. A legacy revision exposes `title: null`; the backend does not invent a headline from its body. No frontend changes are included.

## Numeric consistency

No extra claim object or LLM calculation is introduced. Existing `calculationClaim` binds one exact decimal-result quote which may appear in title, body or once in each. If both fields contain the result, reuse the identical quoted amount-and-unit span; each field is independently checked against the server-calculated canonical value, unit and period. A correct body cannot hide an incorrect title (`800` versus `8000`) and vice versa. Qualitative title/body do not need a fake claim. Other numerical assertions remain outside the restricted literal contract; newly published no-calculation titles must stay qualitative. Worded amounts, scales and arbitrary natural-language financial truth are not claimed to be universally verified.

## Coordinated rollout and native checks

Pause and drain all analysis writers (jobs and chat-triggered writes), deploy Insight then Agent, verify both, resume only after both succeed. Do not resume an old producer against the new required-title admission contract. Existing receipt replay and nullable read compatibility remain.

No new test database is authorized. The repository's PgTestDatabase performs CREATE/DROP DATABASE per instance, so an unfiltered native test suite is not permissible merely by setting INSIGHT_TEST_POSTGRES. New safe class filters (literal class names followed by a dot) are CalculationNarrativeTests., CalculationNarrativeServiceTests., CalculationNarrativeProjectionTests., UnpublishedAnalysisTests., and InsightTitleTests.; these use only in-memory fixtures/fake dependencies. InsightTitlePersistenceTests. and every other PostgreSQL class remain excluded until an approved existing-fixture strategy exists. A SQL rollback probe is not equivalent to a committed repository/history test.

Native API build and the explicitly selected non-DB classes can be run on the existing approved executor with organization packages. Cloud linked tests substitute Result/Dapper interfaces; they are not a native or PostgreSQL PASS.
