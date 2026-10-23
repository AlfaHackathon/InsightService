using System.Text.Json;
using System.Text.Json.Serialization;
using InsightService.Contracts.Context;

namespace InsightService.Contracts.Analyses;

public sealed record DraftPreparation(AnalysisRequest Request, EffectDraft[] Drafts, long? ExpectedStateVersion = null);
public sealed record AnalysisScope(Guid CompanyId, string NewsMaterialId, string NewsVersionId, DateTimeOffset AsOf,
    string? BankProfileId = null, MaterialTrigger? Trigger = null, string[]? Mechanisms = null, bool ProjectionOnly = false);
public sealed record DepthSource(string MaterialId, string VersionId, string ContentHash, string SourceId, DateTimeOffset KnownAt);
public sealed record DepthInventory(Guid CompanyId, DateTimeOffset AsOf, bool CompleteForLoadedScope, DepthSource[] Sources);
public sealed record EvidenceRelevance(SourceRef SourceRef, string Status, string Reason, string Quote);
public sealed record DepthRequest(Guid CompanyId, AnalysisScope Request, JsonElement Plan, DepthInventory Inventory,
    bool HasProfile, bool NewsCorrection, EvidenceRelevance[]? Relevance = null);
public sealed record DepthDecisionResponse(string PolicyVersion, string Level, string Reason,
    int OwnOriginalCount, bool InventoryCompleteForLoadedScope, SourceRef[] SourceRefs, int[] GeneralInsightIndexes,
    string Applicability, string[] MissingFacts,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] EvidenceRelevance[]? Relevance = null);
