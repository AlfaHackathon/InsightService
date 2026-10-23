using InsightService.Contracts.Analyses;
using InsightService.Contracts.Context;
using InsightService.Contracts.Questions;

namespace InsightService.Contracts.Projections;

public sealed record CompanyProjection(Guid CompanyId, string Status, AnalysisReceipt? Receipt = null,
    string? Error = null, bool? ContextComplete = null, string[]? StaleEffectIds = null);
public sealed record GroupExpansion(string GroupId, Guid[] CompanyIds, string[] MissingGroups,
    string[][] Cycles, bool Complete, SourceRef[] SourceRefs, string? AggregationRule = null);
public sealed record ProjectionRequest(CompanyProjection[] Companies, GroupExpansion[]? Groups = null, DateTimeOffset? AsOf = null);
public sealed record RoleProjectionRequest(string PersonId, Guid CompanyId, DateTimeOffset AsOf,
    CompanyProjection[] Companies, GroupExpansion[]? Groups = null);
public sealed record RoleProfile(string PersonId, Guid CompanyId, string Role, string[] Responsibilities,
    Guid[] AllowedCompanyIds, string[] AllowedMaterialIds, DateTimeOffset KnownAt, SourceRef[] SourceRefs);
public sealed record VisibleRoleProfile(string PersonId, Guid CompanyId, string Role, string[] Responsibilities);
public sealed record CompanyIdentity(Guid CompanyId, DateTimeOffset KnownAt);
public sealed record CanonicalFact(Guid CompanyId, string EffectId, EffectRevision Revision, int Priority, bool RequiresReassessment);
public sealed record CanonicalProjection(CompanyProjection[] Companies, CanonicalFact[] Facts);
public sealed record GroupProjectionResponse(GroupProjection[] Groups);
public sealed record RoleProjectionResponse(VisibleRoleProfile Profile, CanonicalFact[] Facts, GroupProjection[] Groups);
public sealed record GroupCoverage(int ExpectedCompanies, int AnalyzedCompanies, Guid[] MissingCompanyIds,
    string[] MissingGroups, string[][] Cycles, string[] UnknownEffects, string[] DuplicateObjects,
    string[] UnrevisedEffectIds, Guid[] EmptyCompanyIds, Guid[] PartialContextCompanyIds, bool AggregationRuleConfirmed);
public sealed record TotalCoverage(bool Complete, Guid[] MissingCompanyIds, string[] UnresolvedEffectIds, string[] DuplicateObjects);
public sealed record GroupTotal(string Metric, string AmountBasis, string Unit, string Period, QuestionIdentity? Question,
    string KnownSubtotal, string? FullTotal, TotalCoverage Coverage, string[] EffectIds, Guid[] CompanyIds);
public sealed record GroupProjection(string GroupId, Guid[] CompanyIds, bool Complete, GroupCoverage Coverage,
    GroupTotal[] Totals, SourceRef[] SourceRefs);
public sealed record QuestionPreparation(string LiteralObjectRef, QuestionIdentity Question);
