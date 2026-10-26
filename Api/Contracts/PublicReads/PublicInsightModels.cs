using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace InsightService.Contracts.PublicReads;

// Explicit allowlists, including nested values: future internal revision/source fields stay internal.
public sealed record PublicInsightSourceRef(string MaterialId, string VersionId, string ContentHash);
public sealed record PublicInsightProvenance(string Acquisition, string? SourceUrl, DateTimeOffset RetrievedAt,
    string TextCompleteness, string? OriginVersionId);
public sealed record PublicInsightSource(string MaterialId, string VersionId, string ContentHash, string SourceId,
    string Kind, string Title, DateTimeOffset KnownAt, DateTimeOffset? PublishedAt, PublicInsightProvenance? Provenance);
public sealed record PublicInsightImportance(
    [property: Range(1, 5)] int? Score,
    [property: Required, MinLength(1), MaxLength(2000)] string Rationale,
    [property: Required, MaxLength(16)] PublicInsightSourceRef[] EvidenceRefs);
public sealed record PublicInsightImpact(string Metric, string AmountBasis, string? EconomicObjectId, string EvidenceStatus);
public sealed record PublicInsightQuestion(string Mechanism, string Frame, string Unit, string TargetPeriod,
    string? BaselinePeriod, string? Basis);
public sealed record PublicInsightApplicability(string Status, string Basis, string Explanation,
    PublicInsightSourceRef[] SourceRefs, string[] Assumptions);
public sealed record PublicInsightCondition(string Fact, string Predicate, string Status, PublicInsightSourceRef[] SourceRefs,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Id,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Assertion,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string[]? DependsOn);
public sealed record PublicInsightMissingFact(string Fact, string? Query, string Scope, string? MaterialId,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Id);
public sealed record PublicInsightAssessment(PublicInsightCondition[] Conditions, PublicInsightMissingFact[] Unknowns,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] PublicInsightApplicability? Applicability);
public sealed record PublicInsightTrigger(string MaterialId, string VersionId);
public sealed record PublicInsightRevision(string RevisionId, string? PreviousRevisionId, string NewsVersionId,
    DateTimeOffset AsOf, string Conclusion, string Status, PublicInsightSourceRef[] SourceRefs, DateTimeOffset CreatedAt,
    PublicInsightImpact? Impact, PublicInsightImportance? CompanyImportance, string? NewsMaterialId,
    PublicInsightQuestion? Question, PublicInsightAssessment? Assessment, PublicInsightTrigger? Trigger,
    string? PublicationClass, string? Category = null);
public sealed record PublicInsightIndicator(string Name, string Metric, string Value, string Unit, string Period,
    string? BaselinePeriod, string EvidenceStatus, PublicInsightSourceRef[] SourceRefs, bool? ArithmeticOnly);
public sealed record PublicIndicatorAvailability(string Reason, string Explanation);
public sealed record PublicGeneralInsightQuality(string DecisionQuestion, string DistinctContribution, string Priority, string Novelty);
public sealed record PublicGeneralInsight(string Title, string Conclusion, string Mechanism, string[] Applicability,
    string[] Exclusions, string[] Unknowns, PublicInsightSourceRef[] SourceRefs,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string[]? SignalIds = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] PublicGeneralInsightQuality? Quality = null,
    string? Category = null);

[JsonDerivedType(typeof(PublicPersonalInsightCard))]
[JsonDerivedType(typeof(PublicGeneralInsightCard))]
public abstract record PublicInsightCard(string Kind, string Id, Guid CompanyId);
public sealed record PublicPersonalInsightCard(string Id, Guid CompanyId, string NewsMaterialId, string LiteralObjectRef,
    PublicInsightRevision CurrentRevision, bool RequiresReassessment, PublicInsightSource[] Sources,
    PublicInsightIndicator[] Indicators, PublicIndicatorAvailability IndicatorAvailability) : PublicInsightCard("personal", Id, CompanyId);
public sealed record PublicGeneralInsightCard(string Id, Guid CompanyId, string NewsMaterialId, string NewsVersionId,
    DateTimeOffset AsOf, DateTimeOffset UpdatedAt, string TextCompleteness, string CompanyStatus,
    PublicGeneralInsight Insight, PublicInsightSource[] Sources, PublicInsightIndicator[] Indicators,
    PublicIndicatorAvailability IndicatorAvailability) : PublicInsightCard("general", Id, CompanyId)
{
    // A persisted job snapshot has no current invalidation assessment. Null never means fresh.
    public bool? RequiresReassessment => null;
}
public sealed record PublicInsightFeed(Guid CompanyId, IReadOnlyList<PublicInsightCard> Items, string? NextCursor)
{
    public bool Exhaustive => false;
}
public sealed record PublicInsightHistoryItem(PublicInsightRevision Revision, PublicInsightSource[] Sources,
    PublicInsightIndicator[] Indicators, PublicIndicatorAvailability IndicatorAvailability);
public sealed record PublicInsightHistory(Guid CompanyId, string InsightId, IReadOnlyList<PublicInsightHistoryItem> Items, string? NextCursor);
