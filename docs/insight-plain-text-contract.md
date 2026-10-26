# Plain-text generated insight fields

New generated personal and general insight title/conclusion fields must be plain text. There is no stripping, renderer or silent rewrite: formatting errors return bounded repairable `INSIGHT_PLAIN_TEXT_REQUIRED` (HTTP 422) and the agent explicitly resubmits corrected text. Paragraphs, numbers, signed values and exact calculation claim quotes stay unchanged. Source URLs belong in the existing source metadata fields.

The common producer/backend detector rejects strong emphasis markers, backticks/fences, ATX headings, Markdown inline/reference links/images, HTTP(S) source URLs in display text and a narrow unambiguous italic pattern. It is a small output contract, not a universal CommonMark/HTML parser. Mathematical single asterisks and ordinary paragraphs remain valid. Source originals, evidence quotations, assessments, material titles and read DTOs are not scanned or rewritten.

Personal admission checks run in prepare/save before title/claim checks; exact completed receipt replay still returns before new admission policy. General checkpoint writes check known plan/result/segmentBatches plan.generalInsights locations after lease/version ownership and before persistence. Exact pre-existing serialized General entries may carry unchanged, including their original source/version refs; changed or newly generated entries must be plain. This keeps old progress/history replayable without retroactively transforming saved cards. Legacy reads retain original text.

This is a separate follow-up to title/narrative/no-publication fixes. No frontend/parser/model-service changes, no SQL migrations/history rewrite. The separate native IDE0055 initializer formatting fix is preserved. The malformed result envelope observed in production is a different issue and is not claimed fixed here.

Cloud validation uses fake dependencies and the public-package linked harness; it does not prove native PostgreSQL commit behavior. New non-DB native class filter: `InsightPlainTextTests.` (literal class name plus dot). Do not run an unfiltered suite or instantiate PgTestDatabase under the no-new-test-DB restriction. A rollback-only SQL probe is not a repository commit/history test.

Coordinated release: pause and drain all analysis writers (jobs and chat), deploy Insight then compatible Agent, verify both, resume. Do not redeploy older candidates that omit the PR25 Agent owner context or the Insight format-only hotfix.
