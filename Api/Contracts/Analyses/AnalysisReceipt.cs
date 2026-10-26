using System.Text.Json.Serialization;

namespace InsightService.Contracts.Analyses;

public sealed record AnalysisReceipt(string AnalysisId, Guid CompanyId, string? BankProfileId,
    string NewsMaterialId, string NewsVersionId, DateTimeOffset AsOf, DateTimeOffset CompletedAt,
    bool Duplicate, string ResultSemantics, IReadOnlyList<SavedEffectResponse> Effects,
    bool? ContextComplete = null, IReadOnlyList<string>? UnrevisedEffectIds = null, RetrievalStep[]? RetrievalTrace = null, MaterialTrigger? Trigger = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Outcome = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] EffectDraft[]? UnpublishedDrafts = null);
