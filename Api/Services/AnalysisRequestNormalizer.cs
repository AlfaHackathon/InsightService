using System.Net;
using AlfaHackaton.Result;
using AlfaHackaton.Result.Errors;
using InsightService.Contracts.Analyses;
using InsightService.Contracts.Calculations;
using InsightService.Models.Persistence;
using InsightService.Models.Policies;
using static InsightService.Services.AnalysisService;

namespace InsightService.Services;

internal static class AnalysisRequestNormalizer
{
    public static Result<(AnalysisRequest, PreparedDraft[])> Normalize(AnalysisRequest r, bool normalizeQuestions = true)
    {
        if (r.CompanyId == Guid.Empty)
        {
            return new(new Error("companyId must be a nonempty application Guid.", ErrorSource.Client, ErrorType.CorruptedInfo) { HttpStatusCode = HttpStatusCode.BadRequest });
        }

        if (RequireId(r.NewsMaterialId, "newsMaterialId").Error is { } newsMaterialIdError)
        {
            return new(newsMaterialIdError);
        }
        if (RequireId(r.NewsVersionId, "newsVersionId").Error is { } newsVersionIdError)
        {
            return new(newsVersionIdError);
        }
        if (r.Trigger is not null)
        {
            if (RequireId(r.Trigger.MaterialId, "trigger materialId").Error is { } triggerMaterialIdError)
            {
                return new(triggerMaterialIdError);
            }
            if (RequireId(r.Trigger.VersionId, "trigger versionId").Error is { } triggerVersionIdError)
            {
                return new(triggerVersionIdError);
            }
        }

        if (r.Mechanisms is not null)
        {
            if (r.Mechanisms.Length is < 1 or > 12)
            {
                return new(new Error("Use 1-12 explicit mechanisms or omit the scope.", ErrorSource.Client, ErrorType.CorruptedInfo) { HttpStatusCode = HttpStatusCode.BadRequest });
            }

            foreach (var mechanism in r.Mechanisms)
            {
                if (QuestionKey("scope", new(mechanism, "absolute", "qualitative", "unknown", null)).Error is { } mechanismError)
                {
                    return new(mechanismError);
                }

            }

            r = r with
            {
                Mechanisms = r.Mechanisms.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray()
            };
        }

        if (r.AsOf == default ||
            r.Drafts is null ||
            r.Drafts.Length > 32)
        {
            return new(new Error("asOf and a drafts array of at most 32 entries are required.", ErrorSource.Client, ErrorType.CorruptedInfo) { HttpStatusCode = HttpStatusCode.BadRequest });
        }

        if (r.BankProfileId is not null)
        {
            if (RequireId(r.BankProfileId, "bankProfileId").Error is { } bankProfileIdError)
            {
                return new(bankProfileIdError);
            }
        }

        var keys = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<PreparedDraft>();
        foreach (var draft in r.Drafts)
        {
            if (draft is null)
            {
                return new(new Error("drafts cannot contain null.", ErrorSource.Client, ErrorType.CorruptedInfo) { HttpStatusCode = HttpStatusCode.BadRequest });
            }

            if (string.IsNullOrWhiteSpace(draft.EffectKey) ||
                draft.EffectKey.Length > 200 ||
                draft.EffectKey != draft.EffectKey.Trim() ||
                draft.EffectKey.Any(char.IsControl))
            {
                return new(new Error("effectKey must be a nonempty bounded stable identifier.", ErrorSource.Client, ErrorType.CorruptedInfo) { HttpStatusCode = HttpStatusCode.BadRequest });
            }

            var question = draft.Question is null ? null : normalizeQuestions ? NormalizeQuestion(draft.Question) : draft.Question;
            var stableKey = draft.EffectKey;
            if (question is not null)
            {
                var questionIdentity = QuestionIdentityPolicy.RawQuestionKey(draft.LiteralObjectRef, question);
                if (questionIdentity.Error is { } identityError)
                {
                    return new(identityError);
                }
                stableKey = questionIdentity.Content!;
            }

            if (!keys.Add(stableKey))
            {
                return new(new Error("An analysis cannot contain the same business question twice.", ErrorSource.Client, ErrorType.CorruptedInfo) { HttpStatusCode = HttpStatusCode.BadRequest });
            }

            if (string.IsNullOrWhiteSpace(draft.LiteralObjectRef) ||
                draft.LiteralObjectRef.Length > 1000 ||
                string.IsNullOrWhiteSpace(draft.Conclusion) ||
                draft.Conclusion.Length > 16000 ||
                string.IsNullOrWhiteSpace(draft.Status) ||
                draft.Status.Length > 100 ||
                draft.SourceRefs is null ||
                draft.SourceRefs.Length is < 1 or > 64)
            {
                return new(new Error("A bounded literal object, conclusion, status and 1-64 source references are required.", ErrorSource.Client, ErrorType.CorruptedInfo) { HttpStatusCode = HttpStatusCode.BadRequest });
            }

            if (!InsightCategoryPolicy.IsValid(draft.Category))
            {
                return new(new Error("category must be risk, opportunity, event or null.", ErrorSource.Client, ErrorType.CorruptedInfo) { HttpStatusCode = HttpStatusCode.BadRequest });
            }

            if (CompanyImportancePolicy.ShapeError(draft) is { } importanceError)
            {
                return new(importanceError);
            }

            if (draft.ModelExplanation?.Length > 16000)
            {
                return new(new Error("Raw model explanation must be bounded.", ErrorSource.Client, ErrorType.CorruptedInfo) { HttpStatusCode = HttpStatusCode.BadRequest });
            }

            if (draft.ExpectedPreviousRevisionId is not null)
            {
                if (RequireId(draft.ExpectedPreviousRevisionId, "expectedPreviousRevisionId").Error is { } expectedPreviousRevisionIdError)
                {
                    return new(expectedPreviousRevisionIdError);
                }
            }

            if (draft.Assessment is { } assessment &&
                (assessment.Conditions is null ||
                    assessment.Unknowns is null ||
                    assessment.Conditions.Length > 16 ||
                    assessment.Unknowns.Length > 16 ||
                    assessment.Conditions.Any(c => c is null ||
                        string.IsNullOrWhiteSpace(c.Fact) ||
                        c.Fact.Length > 1000 ||
                        string.IsNullOrWhiteSpace(c.Predicate) ||
                        c.Predicate.Length > 1000 ||
                        c.Status is not ("supported" or "contradicted" or "unknown") ||
                        c.SourceRefs is null ||
                        c.SourceRefs.Any(s => !draft.SourceRefs.Contains(s)) ||
                        c.Status != "unknown" &&
                        c.SourceRefs.Length == 0) ||
                    assessment.Unknowns.Any(u => u is null ||
                        string.IsNullOrWhiteSpace(u.Fact) ||
                        u.Fact.Length > 1000 ||
                        u.Scope is not ("company" or "market") ||
                        u.Query?.Length > 2000 ||
                        u.MaterialId?.Length > 200)))
            {
                return new(new Error("Conditions must have bound evidence; missing facts require bounded scope and queries.", ErrorSource.Client, ErrorType.CorruptedInfo) { HttpStatusCode = HttpStatusCode.BadRequest });
            }

            if (draft.Question is { } q &&
                draft.Calculation is { } calc &&
                (calc.Unit != q.Unit ||
                    calc.Period != q.TargetPeriod ||
                    q.TargetPeriod == "unknown" ||
                    q.Frame == "budget_delta" &&
                    q.BaselinePeriod == "unknown"))
            {
                return new(new Error("Calculation must match a known question unit, period and comparison baseline.", ErrorSource.Client, ErrorType.CorruptedInfo) { HttpStatusCode = HttpStatusCode.BadRequest });
            }

            if (draft.Impact is { } impact &&
                (string.IsNullOrWhiteSpace(impact.Metric) ||
                    impact.Metric.Length > 200 ||
                    string.IsNullOrWhiteSpace(impact.AmountBasis) ||
                    impact.AmountBasis.Length > 200 ||
                    impact.EconomicObjectId?.Length > 500 ||
                    impact.EvidenceStatus is not ("supported" or "conditional" or "unknown" or "contradicted" or "unaffected") ||
                    impact.Importance is < 0 or > 5 ||
                    impact.Urgency is < 0 or > 5 ||
                    impact.Novelty is < 0 or > 5))
            {
                return new(new Error("Impact metadata requires a metric/basis, explicit evidence status and 0–5 priority components.", ErrorSource.Client, ErrorType.CorruptedInfo) { HttpStatusCode = HttpStatusCode.BadRequest });
            }

            foreach (var source in draft.SourceRefs)
            {
                if (source is null)
                {
                    return new(new Error("Source refs cannot contain null.", ErrorSource.Client, ErrorType.CorruptedInfo) { HttpStatusCode = HttpStatusCode.BadRequest });
                }

                if (RequireId(source.MaterialId, "source materialId").Error is { } sourceMaterialIdError)
                {
                    return new(sourceMaterialIdError);
                }
                if (RequireId(source.VersionId, "source versionId").Error is { } sourceVersionIdError)
                {
                    return new(sourceVersionIdError);
                }
                if (source.ContentHash is null ||
                    source.ContentHash.Length != 64 ||
                    !source.ContentHash.All(Uri.IsHexDigit))
                {
                    return new(new Error("Source hashes must be SHA256 hex strings.", ErrorSource.Client, ErrorType.CorruptedInfo) { HttpStatusCode = HttpStatusCode.BadRequest });
                }
            }

            var references = draft.SourceRefs.Select(s => s with { ContentHash = s.ContentHash.ToLowerInvariant() }).Distinct().OrderBy(s => s.MaterialId, StringComparer.Ordinal).ThenBy(s => s.VersionId, StringComparer.Ordinal).ThenBy(s => s.ContentHash, StringComparer.Ordinal).ToArray();
            Calculation? calculation = null;
            string? value = null;
            if (draft.Calculation is not null)
            {
                var calculationResult = DecimalCalculator.Calculate(draft.Calculation);
                if (calculationResult.Error is { } calculationResultError)
                {
                    return new(calculationResultError);
                }

                (calculation, value) = calculationResult.Content!;
            }

            var normalizedDraft = draft with
            {
                EffectKey = stableKey,
                Question = question,
                SourceRefs = references,
                CompanyImportance = draft.CompanyImportance is { } importance ? importance with
                {
                    Rationale = importance.Rationale.Trim(),
                    EvidenceRefs = importance.EvidenceRefs.Select(s => s with { ContentHash = s.ContentHash.ToLowerInvariant() })
                        .Distinct().OrderBy(s => s.MaterialId, StringComparer.Ordinal).ThenBy(s => s.VersionId, StringComparer.Ordinal).ThenBy(s => s.ContentHash, StringComparer.Ordinal).ToArray()
                } : null,
                Calculation = calculation
            };
            if (draft.Question is not null &&
                calculation is not null)
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
        return new((r with
        {
            AsOf = r.AsOf.ToUniversalTime(),
            Drafts = ordered.Select(x => x.Draft).ToArray()
        }, ordered), null);
    }
}
