using System.Net;
using AlfaHackaton.Result;
using AlfaHackaton.Result.Errors;
using InsightService.Contracts.Analyses;
using InsightService.Contracts.Context;

namespace InsightService.Models.Policies;

internal static class ReassessmentScopePolicy
{
    public static async Task<Result<bool>> IsIndependentUnknown(AnalysisRequest request, EffectDraft draft, EffectResponse existing,
        Func<string, string, Task<Result<MaterialVersion>>> material)
    {
        var previous = existing.CurrentRevision;
        if (draft.Question is null || previous.Question is null ||
            previous.Impact is not { EvidenceStatus: "supported", EconomicObjectId: { Length: > 0 } economicObject } ||
            (draft.Impact?.EvidenceStatus ?? "unknown") != "unknown" ||
            draft.Impact?.EconomicObjectId == economicObject)
        {
            return new(false, null);
        }

        // Dimensions alone identify a comparison, not the obligation being reassessed.
        // Reusing an old news version with an unrelated trigger is not a correction.
        var precedingNews = existing.History.LastOrDefault(r => (r.NewsMaterialId ?? existing.NewsMaterialId) == request.NewsMaterialId);
        if (precedingNews is not null && precedingNews.NewsVersionId != request.NewsVersionId)
        {
            var correction = await Corrects(request, draft, request.NewsMaterialId, request.NewsVersionId, [precedingNews.NewsVersionId], material);
            if (correction.Error is { } error)
            {
                return new(error);
            }
            if (correction.Content)
            {
                return new(false, null);
            }
        }

        // A new version of an actual dependency can invalidate an old object binding.
        // Merely citing the same retrieved company documents is not new evidence.
        if (request.Trigger is not { } trigger ||
            !previous.SourceRefs.Any(r => r.MaterialId == trigger.MaterialId) ||
            previous.SourceRefs.Any(r => r.MaterialId == trigger.MaterialId && r.VersionId == trigger.VersionId) ||
            !draft.SourceRefs.Any(r => r.MaterialId == trigger.MaterialId && r.VersionId == trigger.VersionId))
        {
            return new(true, null);
        }

        var dependency = await Corrects(request, draft, trigger.MaterialId, trigger.VersionId,
            previous.SourceRefs.Where(r => r.MaterialId == trigger.MaterialId).Select(r => r.VersionId).ToArray(), material);
        return dependency.Error is { } dependencyError ? new(dependencyError) : new(!dependency.Content, null);
    }

    private static async Task<Result<bool>> Corrects(AnalysisRequest request, EffectDraft draft, string materialId, string versionId,
        string[] previousVersions, Func<string, string, Task<Result<MaterialVersion>>> material)
    {
        var version = versionId;
        DateTimeOffset? successorKnownAt = null;
        var visited = new HashSet<string>(StringComparer.Ordinal);
        for (var depth = 0; depth < 64 && visited.Add(version); depth++)
        {
            var resolved = await material(materialId, version);
            if (resolved.Error is { } error)
            {
                return new(error);
            }
            var source = resolved.Content!;
            if (source.MaterialId != materialId || source.VersionId != version || source.KnownAt > request.AsOf ||
                source.CompanyIds.Length > 0 && !source.CompanyIds.Contains(request.CompanyId) ||
                version == versionId && !draft.SourceRefs.Any(r => r.MaterialId == source.MaterialId && r.VersionId == source.VersionId && r.ContentHash == source.ContentHash))
            {
                return new(new Error("The reassessment dependency does not match its exact source scope and hash.", ErrorSource.Client, ErrorType.CorruptedInfo) { HttpStatusCode = HttpStatusCode.UnprocessableEntity });
            }
            if (successorKnownAt is { } knownAt && source.KnownAt > knownAt)
            {
                return new(false, null);
            }
            if (depth > 0 && previousVersions.Contains(version, StringComparer.Ordinal))
            {
                return new(true, null);
            }
            if (source.CorrectsVersionId is not { } preceding)
            {
                break;
            }
            successorKnownAt = source.KnownAt;
            version = preceding;
        }
        return new(false, null);
    }
}
