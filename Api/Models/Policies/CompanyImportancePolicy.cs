using System.Net;
using AlfaHackaton.Result;
using AlfaHackaton.Result.Errors;
using InsightService.Contracts.Analyses;
using InsightService.Contracts.Context;

namespace InsightService.Models.Policies;

public static class CompanyImportancePolicy
{
    public static Error? ShapeError(EffectDraft draft)
    {
        if (draft.CompanyImportance is not { } rating)
        {
            return null;
        }
        if (rating.Score is < 1 or > 5 ||
            draft.Impact is { } impact && impact.Importance != (rating.Score ?? 0) || string.IsNullOrWhiteSpace(rating.Rationale) || rating.Rationale.Length > 2000 ||
            draft.SourceRefs is null || rating.EvidenceRefs is null || rating.EvidenceRefs.Length > 16 ||
            rating.EvidenceRefs.Any(r => r is null || !draft.SourceRefs.Contains(r)) ||
            rating.Score is not null && rating.EvidenceRefs.Length == 0)
        {
            return new Error("Company importance needs a 1..5 or null score, bounded rationale, exact draft evidence and a matching legacy component (score, or zero for null); assessed scores require evidence.", ErrorSource.Client, ErrorType.CorruptedInfo) { HttpStatusCode = HttpStatusCode.BadRequest };
        }
        return null;
    }

    public static async Task<Result> Validate(EffectDraft draft, Guid company,
        Func<string, string, Task<Result<MaterialVersion>>> material)
    {
        if (ShapeError(draft) is { } shapeError)
        {
            return new(shapeError);
        }
        if (draft.CompanyImportance is not { Score: not null } rating)
        {
            return new();
        }
        var own = false;
        foreach (var reference in rating.EvidenceRefs)
        {
            var resolved = await material(reference.MaterialId, reference.VersionId);
            if (resolved.Error is { } error)
            {
                return new(error);
            }
            var source = resolved.Content!;
            if (source.MaterialId != reference.MaterialId || source.VersionId != reference.VersionId || source.ContentHash != reference.ContentHash)
            {
                return new(Invalid("Importance evidence must match its exact immutable source identity and hash."));
            }
            own |= source.Kind == "company" && source.CompanyIds.Contains(company);
        }
        return own ? new() : new(Invalid("An assessed company importance needs cited own-company evidence; otherwise keep score null."));
    }

    private static Error Invalid(string message) => new(message, ErrorSource.Client, ErrorType.CorruptedInfo)
    {
        HttpStatusCode = HttpStatusCode.UnprocessableEntity
    };
}
