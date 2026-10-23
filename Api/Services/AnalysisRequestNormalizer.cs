using InsightService.Contracts.Analyses;
using InsightService.Contracts.Calculations;
using InsightService.Models;
using InsightService.Models.Persistence;
using InsightService.Models.Policies;
using static InsightService.Services.AnalysisService;

namespace InsightService.Services;

internal static class AnalysisRequestNormalizer
{
    public static (AnalysisRequest, PreparedDraft[]) Normalize(AnalysisRequest r, bool normalizeQuestions = true)
    {
        if (r.CompanyId == Guid.Empty)
        {
            throw new ApiException(400, "invalid_company", "companyId must be a nonempty application Guid.");
        }

        RequireId(r.NewsMaterialId, "newsMaterialId");
        RequireId(r.NewsVersionId, "newsVersionId");
        if (r.Trigger is not null)
        {
            RequireId(r.Trigger.MaterialId, "trigger materialId");
            RequireId(r.Trigger.VersionId, "trigger versionId");
        }
        if (r.Mechanisms is not null)
        {
            if (r.Mechanisms.Length is < 1 or > 12)
            {
                throw new ApiException(400, "invalid_mechanism_scope", "Use 1-12 explicit mechanisms or omit the scope.");
            }

            foreach (var mechanism in r.Mechanisms)
            {
                QuestionKey("scope", new(mechanism, "absolute", "qualitative", "unknown", null));
            }

            r = r with
            {
                Mechanisms = r.Mechanisms.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray()
            };
        }
        if (r.AsOf == default || r.Drafts is null || r.Drafts.Length > 32)
        {
            throw new ApiException(400, "invalid_request", "asOf and a drafts array of at most 32 entries are required.");
        }

        if (r.BankProfileId is not null)
        {
            RequireId(r.BankProfileId, "bankProfileId");
        }

        var keys = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<PreparedDraft>();
        foreach (var draft in r.Drafts)
        {
            if (draft is null)
            {
                throw new ApiException(400, "invalid_draft", "drafts cannot contain null.");
            }

            if (string.IsNullOrWhiteSpace(draft.EffectKey) || draft.EffectKey.Length > 200 ||
                draft.EffectKey != draft.EffectKey.Trim() || draft.EffectKey.Any(char.IsControl))
            {
                throw new ApiException(400, "invalid_effect_key", "effectKey must be a nonempty bounded stable identifier.");
            }

            var question = draft.Question is null ? null : normalizeQuestions ? NormalizeQuestion(draft.Question) : draft.Question;
            var stableKey = question is null ? draft.EffectKey : QuestionIdentityPolicy.RawQuestionKey(draft.LiteralObjectRef, question);
            if (!keys.Add(stableKey))
            {
                throw new ApiException(400, "duplicate_effect_key", "An analysis cannot contain the same business question twice.");
            }

            if (string.IsNullOrWhiteSpace(draft.LiteralObjectRef) || draft.LiteralObjectRef.Length > 1000 ||
                string.IsNullOrWhiteSpace(draft.Conclusion) || draft.Conclusion.Length > 16000 ||
                string.IsNullOrWhiteSpace(draft.Status) || draft.Status.Length > 100 ||
                draft.SourceRefs is null || draft.SourceRefs.Length is < 1 or > 64)
            {
                throw new ApiException(400, "invalid_draft", "A bounded literal object, conclusion, status and 1-64 source references are required.");
            }

            if (draft.ModelExplanation?.Length > 16000)
            {
                throw new ApiException(400, "invalid_model_explanation", "Raw model explanation must be bounded.");
            }

            if (draft.ExpectedPreviousRevisionId is not null)
            {
                RequireId(draft.ExpectedPreviousRevisionId, "expectedPreviousRevisionId");
            }

            if (draft.Assessment is { } assessment && (assessment.Conditions is null || assessment.Unknowns is null
                || assessment.Conditions.Length > 16 || assessment.Unknowns.Length > 16
                || assessment.Conditions.Any(c => c is null || string.IsNullOrWhiteSpace(c.Fact) || c.Fact.Length > 1000
                    || string.IsNullOrWhiteSpace(c.Predicate) || c.Predicate.Length > 1000
                    || c.Status is not ("supported" or "contradicted" or "unknown") || c.SourceRefs is null
                    || c.SourceRefs.Any(s => !draft.SourceRefs.Contains(s)) || c.Status != "unknown" && c.SourceRefs.Length == 0)
                || assessment.Unknowns.Any(u => u is null || string.IsNullOrWhiteSpace(u.Fact) || u.Fact.Length > 1000
                    || u.Scope is not ("company" or "market") || u.Query?.Length > 2000 || u.MaterialId?.Length > 200)))
            {
                throw new ApiException(400, "invalid_assessment", "Conditions must have bound evidence; missing facts require bounded scope and queries.");
            }

            if (draft.Question is { } q && draft.Calculation is { } calc && (calc.Unit != q.Unit || calc.Period != q.TargetPeriod
                || q.TargetPeriod == "unknown" || q.Frame == "budget_delta" && q.BaselinePeriod == "unknown"))
            {
                throw new ApiException(400, "incompatible_question_calculation", "Calculation must match a known question unit, period and comparison baseline.");
            }

            if (draft.Impact is { } impact && (string.IsNullOrWhiteSpace(impact.Metric) || impact.Metric.Length > 200
                || string.IsNullOrWhiteSpace(impact.AmountBasis) || impact.AmountBasis.Length > 200
                || impact.EconomicObjectId?.Length > 500 || impact.EvidenceStatus is not ("supported" or "conditional" or "unknown" or "contradicted" or "unaffected")
                || impact.Importance is < 0 or > 5 || impact.Urgency is < 0 or > 5 || impact.Novelty is < 0 or > 5))
            {
                throw new ApiException(400, "invalid_impact_metadata", "Impact metadata requires a metric/basis, explicit evidence status and 0–5 priority components.");
            }

            foreach (var source in draft.SourceRefs)
            {
                if (source is null)
                {
                    throw new ApiException(400, "invalid_source_ref", "Source refs cannot contain null.");
                }

                RequireId(source.MaterialId, "source materialId");
                RequireId(source.VersionId, "source versionId");
                if (source.ContentHash is null || source.ContentHash.Length != 64 || !source.ContentHash.All(Uri.IsHexDigit))
                {
                    throw new ApiException(400, "invalid_source_hash", "Source hashes must be SHA256 hex strings.");
                }
            }
            var references = draft.SourceRefs.Select(s => s with { ContentHash = s.ContentHash.ToLowerInvariant() })
                .Distinct().OrderBy(s => s.MaterialId, StringComparer.Ordinal).ThenBy(s => s.VersionId, StringComparer.Ordinal)
                .ThenBy(s => s.ContentHash, StringComparer.Ordinal).ToArray();
            Calculation? calculation = null;
            string? value = null;
            if (draft.Calculation is not null)
            {
                (calculation, value) = DecimalCalculator.Calculate(draft.Calculation);
            }

            var normalizedDraft = draft with
            {
                EffectKey = stableKey,
                Question = question,
                SourceRefs = references,
                Calculation = calculation
            };
            if (draft.Question is not null && calculation is not null)
            {
                normalizedDraft = normalizedDraft with
                {
                    ModelExplanation = draft.ModelExplanation ?? draft.Conclusion,
                    Conclusion = CalculatedConclusion(draft.Question.Frame, value!, calculation.Unit, calculation.Period, draft.LiteralObjectRef)
                };
            }

            result.Add(new PreparedDraft(normalizedDraft, value));
        }
        var ordered = result.OrderBy(x => x.Draft.EffectKey, StringComparer.Ordinal).ToArray();
        return (r with
        {
            AsOf = r.AsOf.ToUniversalTime(),
            Drafts = ordered.Select(x => x.Draft).ToArray()
        }, ordered);
    }

}
