using InsightService.Contracts.Analyses;
namespace InsightService.Contracts.Reads;

// Current head only; history has a separate bounded read.
public sealed record PersonalInsightRead(string Id, Guid CompanyId, string NewsMaterialId,
    string LiteralObjectRef, EffectRevision CurrentRevision, bool RequiresReassessment);
