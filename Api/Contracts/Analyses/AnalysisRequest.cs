
using System.Text.Json.Serialization;

namespace InsightService.Contracts.Analyses;

public sealed record AnalysisRequest(Guid CompanyId, string? BankProfileId, string NewsMaterialId,
    string NewsVersionId, DateTimeOffset AsOf, EffectDraft[] Drafts,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? ContextComplete = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] RetrievalStep[]? RetrievalTrace = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] MaterialTrigger? Trigger = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] long? ExpectedStateVersion = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string[]? Mechanisms = null);
