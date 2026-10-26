#!/usr/bin/env bash
set -euo pipefail

# Run only explicitly listed classes whose fixtures do not construct PgTestDatabase.
# Native restore/build must have passed first. This script starts no DB or app environment.
repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$repo_root"

classes=(
    InsightConnectionTicketTests
    InsightUpdateTests
    InsightUpdateTransportTests
    InsightWebSocketPublicationIntegrationTests
    InsightTitleTests
    CalculationNarrativeTests
    CalculationNarrativeServiceTests
    CalculationNarrativeProjectionTests
    UnpublishedAnalysisTests
    PublicInsightReadTests
    PublicInsightSourceLinkTests
    PublicInsightIndicatorTests
    PublicInsightOwnerMvcTests
    PublicInsightHttpClientTests
    PublicInsightCategoryCursorTests
    CalculationQuestionContractTests
    SwaggerTests
)

filter=""
for class in "${classes[@]}"; do
    filter+="${filter:+|}FullyQualifiedName~${class}."
done

"${DOTNET_BIN:-dotnet}" test Tests/InsightService.Tests.csproj -c Release --no-build --no-restore --filter "$filter"
