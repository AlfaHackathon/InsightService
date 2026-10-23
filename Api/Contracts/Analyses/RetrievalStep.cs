using InsightService.Contracts.Context;

namespace InsightService.Contracts.Analyses;

public sealed record RetrievalStep(int Round, string[] Queries, SourceRef[] RetrievedSources, string StopReason);
