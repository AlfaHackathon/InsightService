using InsightService.Contracts.Analyses;
using InsightService.Contracts.Calculations;
using InsightService.Contracts.Context;
using InsightService.Models;
namespace InsightService.Models.Policies;

public static class ApplicabilityPolicy
{
    public static async Task Validate(EffectDraft draft, Guid company,
        Func<string, string, Task<MaterialVersion>> material)
    {
        if (draft.Question is null) return; // Historical non-question write contract.
        var app = draft.Assessment?.Applicability;
        if (app is not null)
        {
            if (app.Status is not ("established" or "unknown" or "excluded") ||
                app.Basis is not ("documented_exposure" or "profile_signal" or "market_only" or "not_established") ||
                string.IsNullOrWhiteSpace(app.Explanation) || app.Explanation.Length > 2000 ||
                app.SourceRefs is null || app.SourceRefs.Length > 16 || app.Assumptions is null ||
                app.Assumptions.Length > 8 || app.Assumptions.Any(s => string.IsNullOrWhiteSpace(s) || s.Length > 1000))
                throw new ApiException(400, "invalid_applicability", "A bounded applicability assessment is required.");
            if (app.SourceRefs.Any(r => !draft.SourceRefs.Contains(r)))
                throw new ApiException(422, "unbound_applicability", "Applicability must cite this question's exact sources.");
        }
        var evidence = draft.Impact?.EvidenceStatus ?? "unknown";
        var needsExposure = evidence is "supported" or "conditional" or "unaffected";
        if (needsExposure || app?.Status is "established" or "excluded")
        {
            var documented = false;
            foreach (var reference in app?.SourceRefs ?? [])
            {
                var source = await material(reference.MaterialId, reference.VersionId);
                documented |= source.Kind == "company" && source.CompanyIds.Contains(company)
                    && source.SourceId != "provided-bank-csv" && source.ContentHash == reference.ContentHash;
            }
            if (app is null || app.Basis != "documented_exposure" || !documented ||
                (evidence is "supported" or "conditional" && app.Status != "established") ||
                (evidence == "unaffected" && app.Status is not ("established" or "excluded")))
                throw new ApiException(422, "applicability_not_established",
                    "Company exposure requires a cited own documentary source; a profile flag or public macro source is insufficient. Preserve unknown.");
        }
        if (evidence == "conditional" && app?.Assumptions.Length is not > 0)
            throw new ApiException(422, "scenario_assumptions_required", "Conditional requires an explicit scenario on established company exposure.");
        if (evidence == "supported" && ((app?.Assumptions.Length ?? 0) != 0 ||
            (draft.Assessment?.Unknowns.Length ?? 0) != 0 || draft.Assessment?.Conditions.Any(c => c.Status != "supported") == true))
            throw new ApiException(422, "unresolved_supported_claim", "A supported consequence cannot retain unresolved required inputs or scenario assumptions.");
        if (evidence == "unknown" && draft.Calculation is not null)
            throw new ApiException(422, "unknown_calculation", "Unknown impact cannot be represented by a monetary calculation or zero.");
    }

    public static string? PublicationClass(EffectDraft draft) => draft.Question is null ? null :
        draft.Impact?.EvidenceStatus switch
        { "supported" => "consequence", "conditional" => "scenario", "unaffected" => "unaffected", "contradicted" => "review", _ => "missing_data" };
}
