using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AlfaHackaton.Result;
using InsightService.Common;
using InsightService.Contracts.Analyses;
using InsightService.Contracts.Context;
using InsightService.Contracts.Questions;
using InsightService.Contracts.SourceReactions;
using InsightService.Models;
using InsightService.Models.Policies;
using InsightService.Repositories.Interfaces;
using InsightService.Services.Interfaces;

namespace InsightService.Services;

public sealed class AnalysisService(IEffectRepository repository, ISourceResolver resolver) : IAnalysisService
{
    public static readonly JsonSerializerOptions Json = AnalysisProtocol.Json;
    public async Task<Result<RecipientPage>> Recipients(DateTimeOffset asOf, string? after, string? materialId) => await repository.Recipients(asOf, after, materialId);
    public async Task<Result<ReactionPage>> React(SourceChangeRequest change) => await repository.React(change);
    public const string ReceiptSemantics = AnalysisProtocol.ReceiptSemantics;
    public async Task<Result<AnalysisReceipt>> GetAnalysis(Guid companyId, string materialId, string versionId, MaterialTrigger? trigger = null)
    {
        if (RequireCompany(companyId).Error is { } companyError)
        {
            return new(companyError);
        }
        if (RequireId(materialId, "newsMaterialId").Error is { } newsMaterialIdError)
        {
            return new(newsMaterialIdError);
        }
        if (RequireId(versionId, "newsVersionId").Error is { } newsVersionIdError)
        {
            return new(newsVersionIdError);
        }
        var findAnalysisResult = (await repository.FindAnalysis(companyId, materialId, versionId, trigger))?.Receipt;
        if (findAnalysisResult is null)
        {
            return new(new ApiError(404, "analysis_not_found", "No completed analysis receipt exists for this news version."));
        }

        return new(findAnalysisResult, null);
    }

    public async Task<Result<IReadOnlyList<EffectResponse>>> GetEffects(Guid companyId, string materialId)
    {
        if (RequireCompany(companyId).Error is { } companyError2)
        {
            return new(companyError2);
        }
        if (RequireId(materialId, "newsMaterialId").Error is { } newsMaterialIdError2)
        {
            return new(newsMaterialIdError2);
        }
        return new(await repository.GetEffects(companyId, materialId), null);
    }

    public async Task<Result<EffectSnapshot>> GetSnapshot(Guid companyId, string materialId, DateTimeOffset? asOf = null)
    {
        if (RequireCompany(companyId).Error is { } companyError3)
        {
            return new(companyError3);
        }
        if (RequireId(materialId, "newsMaterialId").Error is { } newsMaterialIdError3)
        {
            return new(newsMaterialIdError3);
        }
        return new(await repository.GetSnapshot(companyId, materialId, asOf), null);
    }

    public async Task<Result<EffectResponse>> GetEffect(string effectId)
    {
        if (RequireId(effectId, "effectId").Error is { } effectIdError)
        {
            return new(effectIdError);
        }
        var getEffectResult = await repository.GetEffect(effectId);
        if (getEffectResult is null)
        {
            return new(new ApiError(404, "effect_not_found", "Effect was not found."));
        }

        return new(getEffectResult, null);
    }

    public async Task<Result<AnalysisReceipt>> Save(AnalysisRequest request, CancellationToken ct = default)
    {
        var normalization = AnalysisRequestNormalizer.Normalize(request);
        if (normalization.Error is { } normalizationError)
        {
            return new(normalizationError);
        }

        var (normalized, prepared) = normalization.Content!;
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(normalized, Json))));
        async Task<Result<AnalysisReceipt?>> CompletedReceipt()
        {
            var priorReceipt = await repository.FindAnalysis(normalized.CompanyId, normalized.NewsMaterialId, normalized.NewsVersionId, normalized.Trigger);
            if (priorReceipt is null)
            {
                return new(null, null);
            }

            if (priorReceipt.RequestHash != hash)
            {
                var legacyNormalization = AnalysisRequestNormalizer.Normalize(request, false);
                if (legacyNormalization.Error is { } legacyError)
                {
                    return new(legacyError);
                }
                var legacyHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
                    JsonSerializer.Serialize(legacyNormalization.Content.Item1, Json))));
                if (priorReceipt.RequestHash != legacyHash)
                {
                    return new(new ApiError(409, "analysis_payload_conflict", "This completed news analysis has a different payload."));
                }
            }

            return new(Duplicate(priorReceipt.Receipt), null);
        }

        var completedReceipt = await CompletedReceipt();
        if (completedReceipt.Error is { } completedReceiptError)
        {
            return new(completedReceiptError);
        }

        var alreadyCompleted = completedReceipt.Content!;
        if (alreadyCompleted is not null)
        {
            return new(alreadyCompleted, null);
        }

        var cache = new Dictionary<(string Material, string Version), MaterialVersion>();
        async Task<Result<MaterialVersion>> Material(string material, string version)
        {
            if (!cache.TryGetValue((material, version), out var value))
            {
                if (cache.Count >= 128)
                {
                    return new(new ApiError(422, "source_budget", "Analysis is limited to 128 resolved source versions."));
                }

                var materialResolution = await resolver.GetAsync(material, version, ct);
                if (materialResolution.Error is { } materialResolutionError)
                {
                    return new(materialResolutionError);
                }

                value = materialResolution.Content!;
                if (value.MaterialId != material ||
                    value.VersionId != version)
                {
                    return new(new ApiError(502, "invalid_context_identity", "ContextService returned a different material identity."));
                }

                cache.Add((material, version), value);
            }

            if (value.KnownAt > normalized.AsOf)
            {
                return new(new ApiError(422, "source_not_known", "A source version was not known at analysis asOf."));
            }

            if (value.CompanyIds.Length > 0 &&
                !value.CompanyIds.Contains(normalized.CompanyId))
            {
                return new(new ApiError(422, "source_company_scope", "A source version belongs to another company scope."));
            }

            if (value.Kind == "company" &&
                !value.CompanyIds.Contains(normalized.CompanyId))
            {
                return new(new ApiError(422, "source_company_scope", "Company material requires the target company Guid."));
            }

            return new(value, null);
        }

        var newsMaterialIdResolution = await Material(normalized.NewsMaterialId, normalized.NewsVersionId);
        if (newsMaterialIdResolution.Error is { } newsMaterialIdResolutionError)
        {
            return new(newsMaterialIdResolutionError);
        }

        var news = newsMaterialIdResolution.Content!;
        MaterialVersion? trigger = null;
        if (normalized.Trigger is { } triggerReference)
        {
            var materialIdResolution = await Material(triggerReference.MaterialId, triggerReference.VersionId);
            if (materialIdResolution.Error is { } triggerError)
            {
                return new(triggerError);
            }
            trigger = materialIdResolution.Content;
        }

        if (news.Kind != "news")
        {
            return new(new ApiError(422, "not_news", "Analysis must refer to a news material version."));
        }

        var current = new Dictionary<string, EffectResponse>(StringComparer.Ordinal);
        foreach (var effect in await repository.GetEffects(normalized.CompanyId, normalized.NewsMaterialId))
        {
            var key = effect.EffectKey;
            if (effect.CurrentRevision.Question is { } q)
            {
                var questionKey = QuestionKey(effect.LiteralObjectRef, q);
                // Preserve unmigrated history without blocking unrelated questions.
                if (questionKey.Error is null)
                {
                    key = questionKey.Content!;
                }
                else if (questionKey.Error is not ApiError { Code: "invalid_question_identity" })
                {
                    return new(questionKey.Error);
                }

            }

            if (!current.TryAdd(key, effect))
            {
                return new(new ApiError(409, "ambiguous_legacy_question", "Multiple stored histories normalize to this question; reconcile explicitly before writing."));
            }
        }

        foreach (var item in prepared)
        {
            var draft = item.Draft;
            if (normalized.Mechanisms?.Length > 0 &&
                draft.Question is { } scopedQuestion &&
                !normalized.Mechanisms.Contains(scopedQuestion.Mechanism))
            {
                return new(new ApiError(422, "question_outside_scope", "This draft mechanism is outside the explicit analysis scope."));
            }

            if (trigger is not null &&
                (draft.Question is null ||
                    !draft.SourceRefs.Any(s => s.MaterialId == trigger.MaterialId &&
                        s.VersionId == trigger.VersionId &&
                        s.ContentHash == trigger.ContentHash)))
            {
                return new(new ApiError(422, "trigger_citation_required", "A source revision reassessment must cite its exact triggering original and identify the question."));
            }

            if (!draft.SourceRefs.Any(s => s.MaterialId == news.MaterialId &&
                    s.VersionId == news.VersionId &&
                    s.ContentHash == news.ContentHash))
            {
                return new(new ApiError(422, "current_news_citation_required", "Each effect must cite the exact current news version and hash."));
            }

            foreach (var reference in draft.SourceRefs)
            {
                var materialIdResolution2 = await Material(reference.MaterialId, reference.VersionId);
                if (materialIdResolution2.Error is { } materialIdResolution2Error)
                {
                    return new(materialIdResolution2Error);
                }

                var source = materialIdResolution2.Content!;
                if (source.ContentHash != reference.ContentHash)
                {
                    return new(new ApiError(422, "source_hash_mismatch", "A source hash does not match its immutable original version."));
                }
            }

            if ((await ApplicabilityPolicy.Validate(draft, normalized.CompanyId, Material)).Error is { } applicabilityError)
            {
                return new(applicabilityError);
            }
            if (current.TryGetValue(draft.EffectKey, out var existing))
            {
                if (draft.ExpectedPreviousRevisionId != existing.CurrentRevision.RevisionId)
                {
                    var completedReceipt2 = await CompletedReceipt();
                    if (completedReceipt2.Error is { } completedReceipt2Error)
                    {
                        return new(completedReceipt2Error);
                    }

                    var concurrentlyCompleted = completedReceipt2.Content!;
                    if (concurrentlyCompleted is not null)
                    {
                        return new(concurrentlyCompleted, null);
                    }

                    return new(new ApiError(409, "stale_previous_revision", "expectedPreviousRevisionId must be the actual latest revision."));
                }

                if (draft.LiteralObjectRef != existing.LiteralObjectRef)
                {
                    return new(new ApiError(409, "literal_object_conflict", "An existing effectKey cannot be rebound to a different literal object."));
                }

                if (normalized.AsOf < existing.CurrentRevision.AsOf)
                {
                    return new(new ApiError(409, "stale_analysis_time", "An unprocessed historical event cannot replace a more recent assessment; reassess at current knowledge time."));
                }

                if (trigger is not null)
                {
                    continue;
                }

                // Independent news continue the same question. A correction must belong
                // to the actual most recent revision of its own news, even if another
                // independent news has advanced the question head in between.
                var precedingNews = draft.Question is null ? existing.CurrentRevision : existing.History.LastOrDefault(r => r.NewsMaterialId == news.MaterialId);
                if (precedingNews is null)
                {
                    continue;
                }

                var ancestor = news;
                var reached = false;
                for (var depth = 0; depth < 64 &&
                    ancestor.CorrectsVersionId is not null; depth++)
                {
                    if (ancestor.CorrectsVersionId == precedingNews.NewsVersionId)
                    {
                        reached = true;
                        break;
                    }

                    var materialIdResolution3 = await Material(news.MaterialId, ancestor.CorrectsVersionId);
                    if (materialIdResolution3.Error is { } materialIdResolution3Error)
                    {
                        return new(materialIdResolution3Error);
                    }

                    ancestor = materialIdResolution3.Content!;
                }

                if (!reached)
                {
                    return new(new ApiError(409, "news_correction_lineage", "The news version must correct the effect's actual preceding news version."));
                }
            }
            else if (draft.ExpectedPreviousRevisionId is not null)
            {
                return new(new ApiError(409, "missing_previous_effect", "A new effect cannot reference an unknown previous revision."));
            }
        }

        return await repository.Save(normalized, hash, prepared);
    }

    public static AnalysisReceipt Duplicate(AnalysisReceipt receipt) => AnalysisProtocol.Duplicate(receipt);
    private static Result RequireCompany(Guid id) => RequestValidation.RequireCompany(id);
    public static Result RequireId(string? value, string name) => RequestValidation.RequireId(value, name);
    public static QuestionIdentity NormalizeQuestion(QuestionIdentity question) => QuestionIdentityPolicy.NormalizeQuestion(question);
    public static Result<string> QuestionKey(string literal, QuestionIdentity input) => QuestionIdentityPolicy.QuestionKey(literal, input);
    public static string ProcessingVersion(string version, MaterialTrigger? trigger) => QuestionIdentityPolicy.ProcessingVersion(version, trigger);
    public static string CalculatedConclusion(string frame, string value, string unit, string period, string literal) => QuestionIdentityPolicy.CalculatedConclusion(frame, value, unit, period, literal);
}
