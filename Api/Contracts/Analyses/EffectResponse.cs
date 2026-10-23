
using System.Text.Json.Serialization;

namespace InsightService.Contracts.Analyses;

public sealed record EffectResponse(string EffectId, string EffectKey, Guid CompanyId, string? BankProfileId,
    string NewsMaterialId, string LiteralObjectRef, EffectRevision CurrentRevision, IReadOnlyList<EffectRevision> History,
    bool RequiresReassessment = false);
