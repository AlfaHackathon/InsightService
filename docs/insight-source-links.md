# Public source links

Source-only candidate based on combined PR #15, SHA `4f3d5358f423b27650b39dd8f49a93d21c898d6b`, tree `7ea1f44965ba36dcf13519bad8815c3b49e7cef9`. Title, narrative, no_insight, WS and membership/source authorization are preserved. No frontend, VPS, stored-card edits, credentials or model calls.

## Frontend contract

Personal/general feed and detail return `sources[]`; personal history returns `items[].sources[]`. Each source retains its current fields and adds `url: string|null` and `newsId: string|null`:

```json
{
  "materialId": "news-1111111111111111111111111111111111111111",
  "versionId": "v-2222222222222222222222222222222222222222",
  "contentHash": "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
  "sourceId": "Tass",
  "kind": "news",
  "title": "Synthetic publisher announcement",
  "url": "https://publisher.example.test/economy/123?from=feed&lang=ru#original",
  "newsId": null
}
```

Render `title` as text and use `url` for an anchor only when non-null. For a new tab, use `rel="noopener noreferrer"`. With null URL render the title without an anchor. Do not build links from `sourceId`, `materialId`, `versionId`, `newsId`, title, conclusion or internal service routes. Narrative/body is separate: this change neither appends Markdown links nor modifies persisted conclusion/title. The independent plain-text work owns narrative presentation.

`url` is copied verbatim only from the authorized exact `MaterialVersion.provenance.sourceUrl`, allowing HTTP(S), no userinfo, whitespace, control characters or backslashes, length <=4096. The existing `provenance.sourceUrl` is retained with the identical safe value. Missing/unsafe URL is null. Missing provenance is still null. Parser document ID is not present in today's exact material contract, so `newsId` is honestly null; private `locator` and material IDs are never substituted. No lookup of latest news or mutable source by title/ID occurs. Lead/excerpt/unknown completeness is not upgraded.

Every reference is still checked for material/version/hash, knownAt<=revision asOf and current company/bank scope before projection. Duplicate citations share one source metadata entry per material/version, without collapsing distinct versions or mutating saved refs. History uses each revision's exact source versions and URLs. General sources remain public-only. Denied sources omit feed cards while preserving the cursor; detail/history return 404. URLs create no additional backend HTTP requests or source budget.

## Provenance trace and coordinated source prerequisites

Verified source baselines: WebNewsParserService `0d6bba3db2df5c9948f74afd6952ffb43450a739`, AgentService `96be3cb331b69a0a2bee71a0c82d6f0453f6aaa9`, ContextService `5321943051b6efa7deec037d6b3a24ca054532c5`.

1. Parser stores the publisher URL in `NewsDocument.Url`, but its get-by-id/stored-search `NewsSearchResponse` omits it. The paired parser branch adds optional `url` to that DTO and copies only the safe stored URL in both projections. It never derives a URL from Mongo ID or externalId.
2. Agent `ParserNewsClient` previously stripped URL and admitted a lead without sourceUrl; stored `ExternalNewsClient` explicitly replaced URL with null. The paired Agent branch validates optional `url`, forwards it as `sourceUrl` to Context admission, and preserves stored discovery URLs. Legacy absent/null URL remains absent/null; Kafka reference identity, source spelling, retry policy, lead completeness, durable job-before-ack and replay are unchanged. Tass discovery also rejects unsafe URLs.
3. Context `NewsAdmissionService` already persists admission `sourceUrl` as material provenance, with externalId in private locator. URL participates in version identity. No Context changes are necessary and no parser Mongo ID is present in this material contract.
4. Insight already retained URL in nested provenance. This branch exposes the explicit anchor field through the same verified source projection for feed/detail/history and adds no links to prose.

The companion source branches are `codex/parser-source-url-20261026` in parser and Agent. Root owns integration and deploy sequencing. Existing cards/materials with no URL stay null. No migration, replay, regeneration, backfill or manual repair is performed; changing admission URL can create a new material version, so blanket replay is not part of this change.

## Verification limits and native gate

Native restore with the unchanged organizational `NuGet.config` failed with `NU1301 / proxy tunnel 403` at `nuget.pkg.github.com/AlfaHackathon/index.json`. No package-feed bypass or production package substitution. Native build/test/publish remains unverified.

Separate linked-source harness tests actual public-read, projection, policy, source transport, MVC/middleware, swagger and WS source using explicit Result/repository fakes and public NuGet packages. It starts neither PostgreSQL nor a separate app environment; TestServer is in-process. This is not native package/SQL verification. Its 176 tests pass, including new real-shape material fixtures, exact-version history URLs, deduplication, missing URLs, unsafe URLs and cross-company/bank denials. Parser linked tests exercise real NewsService get/search with an in-memory repository and explicit Kafka/worker substitutes: 12 pass. Agent's 43 focused tests, typecheck, lint/boundary checks and build pass without DB/model calls. Harness and logs are delivered separately, not wired into production projects.

Root's existing authorized native executor must restore/build the actual solution and run the explicit no-Postgres subset, now including `PublicInsightSourceLinkTests`, plus parser `dotnet test Tests/Api.Tests.csproj -c Release --filter FullyQualifiedName~NewsSourceLinkTests`. Full persistence regression requires the already authorized existing synthetic fixture; do not provision a new test database. Only the single remote writer may publish/deploy. Verify authenticated feed/detail/history anchor fields, absent URL and source ACL through the real Gateway after native gates.
