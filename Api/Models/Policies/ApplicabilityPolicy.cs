using System.Net;
using AlfaHackaton.Result;
using AlfaHackaton.Result.Errors;
using InsightService.Contracts.Analyses;
using InsightService.Contracts.Context;
using InsightService.Contracts.Questions;

namespace InsightService.Models.Policies;

public static class ApplicabilityPolicy
{
    public static async Task<Result> Validate(EffectDraft draft, Guid company, Func<string, string, Task<Result<MaterialVersion>>> material)
    {
        var hasDependencies = draft.Assessment?.Conditions?.Any(c => c is not null &&
            (c.Id is not null || c.Assertion is not null || c.DependsOn is not null)) == true ||
            draft.Assessment?.Unknowns?.Any(u => u?.Id is not null) == true;
        if (hasDependencies && ValidateDependencies(draft) is { } dependencyError)
        {
            return new(dependencyError);
        }

        if (draft.Question is null && !hasDependencies)
        {
            return new(); // Historical non-question write contract.
        }

        var app = draft.Assessment?.Applicability;
        if (app is not null)
        {
            if (app.Status is not ("established" or "unknown" or "excluded") ||
                app.Basis is not ("documented_exposure" or "profile_signal" or "market_only" or "not_established") ||
                string.IsNullOrWhiteSpace(app.Explanation) ||
                app.Explanation.Length > 2000 ||
                app.SourceRefs is null ||
                app.SourceRefs.Length > 16 ||
                app.Assumptions is null ||
                app.Assumptions.Length > 8 ||
                app.Assumptions.Any(s => string.IsNullOrWhiteSpace(s) ||
                    s.Length > 1000))
            {
                return new(new Error("A bounded applicability assessment is required.", ErrorSource.Client, ErrorType.CorruptedInfo) { HttpStatusCode = HttpStatusCode.BadRequest });
            }

            if (app.SourceRefs.Any(r => !draft.SourceRefs.Contains(r)))
            {
                return new(new Error("Applicability must cite this question's exact sources.", ErrorSource.Client, ErrorType.CorruptedInfo) { HttpStatusCode = HttpStatusCode.UnprocessableEntity });
            }
        }

        var evidence = draft.Impact?.EvidenceStatus ?? "unknown";
        var needsExposure = evidence is "supported" or "conditional" or "unaffected";
        if (needsExposure ||
            app?.Status is "established" or "excluded")
        {
            var documented = false;
            foreach (var reference in app?.SourceRefs ?? [])
            {
                var resolved = await material(reference.MaterialId, reference.VersionId);
                if (resolved.Error is { } resolutionError)
                {
                    return new(resolutionError);
                }
                var source = resolved.Content!;
                documented |= source.Kind == "company" &&
                    source.CompanyIds.Contains(company) &&
                    source.SourceId != "provided-bank-csv" &&
                    source.ContentHash == reference.ContentHash;
            }

            if (app is null ||
                app.Basis != "documented_exposure" ||
                !documented ||
                (evidence is "supported" or "conditional" &&
                    app.Status != "established") ||
                (evidence == "unaffected" &&
                    app.Status is not ("established" or "excluded")))
            {
                return new(new Error("Company exposure requires a cited own documentary source; a profile flag or public macro source is insufficient. Preserve unknown.", ErrorSource.Client, ErrorType.CorruptedInfo) { HttpStatusCode = HttpStatusCode.UnprocessableEntity });
            }
        }

        if (evidence == "conditional" &&
            app?.Assumptions.Length is not > 0)
        {
            return new(new Error("Conditional requires an explicit scenario on established company exposure.", ErrorSource.Client, ErrorType.CorruptedInfo) { HttpStatusCode = HttpStatusCode.UnprocessableEntity });
        }

        if (evidence == "supported" &&
            ((app?.Assumptions.Length ?? 0) != 0 ||
                (draft.Assessment?.Unknowns.Length ?? 0) != 0 ||
                draft.Assessment?.Conditions.Any(c => c.Status != "supported") == true))
        {
            return new(new Error("A supported consequence cannot retain unresolved required inputs or scenario assumptions.", ErrorSource.Client, ErrorType.CorruptedInfo) { HttpStatusCode = HttpStatusCode.UnprocessableEntity });
        }

        if (evidence == "unknown" &&
            draft.Calculation is not null)
        {
            return new(new Error("Unknown impact cannot be represented by a monetary calculation or zero.", ErrorSource.Client, ErrorType.CorruptedInfo) { HttpStatusCode = HttpStatusCode.UnprocessableEntity });
        }

        return new();
    }

    private static Error? ValidateDependencies(EffectDraft draft)
    {
        var assessment = draft.Assessment!;
        if (assessment.Conditions is null || assessment.Unknowns is null ||
            assessment.Conditions.Length > 16 || assessment.Unknowns.Length > 16)
        {
            return InvalidDependencies();
        }

        var ids = new HashSet<string>(StringComparer.Ordinal);
        var conditions = new Dictionary<string, ApplicabilityCondition>(StringComparer.Ordinal);
        foreach (var condition in assessment.Conditions)
        {
            if (condition is null || !ValidId(condition.Id) || !ids.Add(condition.Id!) ||
                condition.Status is not ("supported" or "contradicted" or "unknown") ||
                condition.Assertion is not ("observed" or "derived" or "conditional") ||
                condition.DependsOn is null || condition.DependsOn.Length > 16 ||
                condition.DependsOn.Any(id => !ValidId(id)) ||
                condition.DependsOn.Distinct(StringComparer.Ordinal).Count() != condition.DependsOn.Length ||
                (condition.Assertion == "observed" ? condition.DependsOn.Length != 0 : condition.DependsOn.Length == 0))
            {
                return InvalidDependencies();
            }
            conditions.Add(condition.Id!, condition);
        }
        foreach (var unknown in assessment.Unknowns)
        {
            if (unknown is null || !ValidId(unknown.Id) || !ids.Add(unknown.Id!))
            {
                return InvalidDependencies();
            }
        }
        if (conditions.Values.Any(c => c.DependsOn!.Any(id => !ids.Contains(id))))
        {
            return InvalidDependencies();
        }

        // Validate the whole graph, including unresolved claims and disconnected branches.
        var visiting = new HashSet<string>(StringComparer.Ordinal);
        var visited = new HashSet<string>(StringComparer.Ordinal);
        bool Acyclic(string id)
        {
            if (visited.Contains(id) || !conditions.TryGetValue(id, out var condition))
            {
                return true;
            }
            if (!visiting.Add(id))
            {
                return false;
            }
            if (!condition.DependsOn!.All(Acyclic))
            {
                return false;
            }
            visiting.Remove(id);
            visited.Add(id);
            return true;
        }
        if (!conditions.Keys.All(Acyclic))
        {
            return InvalidDependencies();
        }

        var support = new Dictionary<string, bool>(StringComparer.Ordinal);
        bool Supported(string id)
        {
            if (!support.TryGetValue(id, out var supported))
            {
                supported = conditions.TryGetValue(id, out var condition) && condition.Status == "supported" &&
                    condition.DependsOn!.All(Supported);
                support.Add(id, supported);
            }
            return supported;
        }
        foreach (var condition in conditions.Values)
        {
            if (condition.Assertion == "derived" && condition.Status == "supported" &&
                !condition.DependsOn!.All(Supported))
            {
                return new("A supported derived condition requires supported evidence dependencies.", ErrorSource.Client, ErrorType.CorruptedInfo)
                { HttpStatusCode = HttpStatusCode.UnprocessableEntity };
            }
            if (condition.Assertion == "conditional" &&
                (condition.Status != "unknown" || draft.Impact?.EvidenceStatus != "conditional" ||
                    assessment.Applicability is not { Status: "established", Basis: "documented_exposure", Assumptions.Length: > 0 }))
            {
                return new("Conditional evidence requires an unresolved scenario on established exposure with explicit assumptions.", ErrorSource.Client, ErrorType.CorruptedInfo)
                { HttpStatusCode = HttpStatusCode.UnprocessableEntity };
            }
        }
        return null;
    }

    private static bool ValidId(string? id) => id is { Length: > 0 and <= 64 } && id[0] is >= 'a' and <= 'z' &&
        id.All(c => c is >= 'a' and <= 'z' or >= '0' and <= '9' or '_' or '-');

    private static Error InvalidDependencies() => new("Invalid evidence dependencies.", ErrorSource.Client, ErrorType.CorruptedInfo)
    { HttpStatusCode = HttpStatusCode.BadRequest };

    public static string? PublicationClass(EffectDraft draft) => draft.Question is null ? null : draft.Impact?.EvidenceStatus switch
    {
        "supported" => "consequence",
        "conditional" => "scenario",
        "unaffected" => "unaffected",
        "contradicted" => "review",
        _ => "missing_data"
    };
}
