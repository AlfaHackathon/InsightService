using InsightService.Contracts.Context;
using System.Text.Json.Serialization;

namespace InsightService.Contracts.Analyses;

public sealed record RetrievalStep(int Round, string[] Queries, SourceRef[] RetrievedSources, string StopReason);
