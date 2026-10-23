
using System.Text.Json.Serialization;

namespace InsightService.Contracts.Analyses;

public sealed record SavedEffectResponse(string EffectId, string RevisionId, string? PreviousRevisionId,
    bool Duplicate, EffectRevision CurrentRevision);
