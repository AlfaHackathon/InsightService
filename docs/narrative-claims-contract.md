# Grounded agent conclusion and literal calculation claims

New conclusions are preserved verbatim. Neither admission normalization nor canonical projection renders a conclusion template. Calculation, result, source evidence and model audit remain separate. No stored rows/history are rewritten.

An optional internal `calculationClaim` accompanies a numeric result in a newly calculated draft:

```json
{
  "conclusion": "Расходы могут вырасти на 8 000 RUB в 2027 году. Проверьте условия договора; фактическая оплата не подтверждена.",
  "calculationClaim": { "quote": "8 000 RUB", "value": "8000", "unit": "RUB", "period": "2027" }
}
```

This example omits the required calculation/source/question fields for brevity. Insight independently evaluates the existing Decimal AST. The exact unique quoted literal must denote that signed result, with matching unit/period. Quote grammar is a signed ASCII decimal with optional thousands spaces/NBSP/narrow-NBSP, invariant decimal point, then an uppercase three-letter literal unit or RUB aliases `₽`/`руб.`. No comma decimals, scales, exponents, or worded amounts. Numeric facts outside the bound result and declared period belong in structured assessment, not the calculated conclusion. A calculated qualitative conclusion may omit a claim. No-calculation qualitative insights continue without a claim; a claim without a calculation is rejected.

This is literal consistency validation, not universal NLP, real-world truth checking, proof of payment, or verification of arbitrary worded amounts and directional prose. Producers must not assert unsupported numerical claims. Unsupported prose remains subject to grounded reasoning/evaluations. Invalid quote/value/unit/period or an unbound numerical assertion returns compact repairable HTTP 422 `CONCLUSION_*`. The server never substitutes prose, copies a previous calculation, or silently converts a model result.

The native Agent tool asks for optional `calculationClaimQuote` only. Agent derives the literal value/unit by string parsing and takes period from that same draft calculation; it does not calculate/round or invent an omitted quote. Backend validates again. Internal claim is persisted with a new revision; the existing frontend DTO continues to expose conclusion and sourced indicators, without the audit claim.

## Compatibility and rollout

Fields are additive and ignored on null serialization. Existing receipt comparisons try exact historical normalization hashes only for existing receipts; the old template is retained solely inside `LegacyReceiptConclusion` for hash reconstruction, never new admission or reads. Exact historical retries return the original duplicate receipt before source acquisition or new claim validation. Changed payload remains a conflict. Legacy records/history need no backfill.

Deploy the compatible Agent producer first, then Insight validation in one coordinated quiet worker window. Old Insight ignores the additive field and retains old behavior until upgraded; new Insight rejects unsupported newly calculated prose from old producers. Quiesce in-flight old producers during cutover. Roll back the pair if necessary; do not rewrite history. No SQL migration or ingress/auth change is needed.

Insufficient-evidence publication is a separate coordinated receipt boundary: producer acceptance of a coherent unknown is internal analysis, not permission to publish a card. Pair conditional repair with that boundary before running live generation.

## Validation

Cloud linked-source model-free tests cover verbatim normalization, canonical projection, quote/value/unit/period, no-claim/qualitative paths, prepare/save parity, historical receipt replay without acquiring sources, public feed/detail/history and Swagger. The harness substitutes organization Result/Dapper interfaces and is not a native build or PostgreSQL gate. Native commands on an executor with approved organization packages and existing test DB access:

```sh
dotnet restore InsightService.slnx
dotnet build InsightService.slnx -c Release --no-restore
dotnet test Tests/InsightService.Tests.csproj -c Release --no-build
dotnet format whitespace InsightService.slnx --no-restore --verify-no-changes
```

Use the repository's existing test DB configuration; do not create another deployed stack. Cloud does not retry the denied private package feed, call models, or touch production.
