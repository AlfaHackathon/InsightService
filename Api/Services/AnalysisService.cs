using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
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
    public async Task<RecipientPage> Recipients(DateTimeOffset asOf, string? after, string? materialId) => await repository.Recipients(asOf, after, materialId);
    public async Task<ReactionPage> React(SourceChangeRequest change) => await repository.React(change);
    public const string ReceiptSemantics = AnalysisProtocol.ReceiptSemantics;

    public async Task<AnalysisReceipt> GetAnalysis(Guid companyId, string materialId, string versionId, MaterialTrigger? trigger = null)
    {
        RequireCompany(companyId);
        RequireId(materialId, "newsMaterialId");
        RequireId(versionId, "newsVersionId");
        return (await repository.FindAnalysis(companyId, materialId, versionId, trigger))?.Receipt
            ?? throw new ApiException(404, "analysis_not_found", "No completed analysis receipt exists for this news version.");
    }

    public async Task<IReadOnlyList<EffectResponse>> GetEffects(Guid companyId, string materialId)
    {
        RequireCompany(companyId);
        RequireId(materialId, "newsMaterialId");
        return await repository.GetEffects(companyId, materialId);
    }
    public async Task<EffectSnapshot> GetSnapshot(Guid companyId, string materialId, DateTimeOffset? asOf = null)
    {
        RequireCompany(companyId);
        RequireId(materialId, "newsMaterialId");
        return await repository.GetSnapshot(companyId, materialId, asOf);
    }

    public async Task<EffectResponse> GetEffect(string effectId)
    {
        RequireId(effectId, "effectId");
        return await repository.GetEffect(effectId) ?? throw new ApiException(404, "effect_not_found", "Effect was not found.");
    }

    public async Task<AnalysisReceipt> Save(AnalysisRequest request, CancellationToken ct = default)
    {
        var (normalized, prepared) = AnalysisRequestNormalizer.Normalize(request);
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(normalized, Json))));
        async Task<AnalysisReceipt?> CompletedReceipt()
        {
            var priorReceipt = await repository.FindAnalysis(normalized.CompanyId, normalized.NewsMaterialId, normalized.NewsVersionId, normalized.Trigger);
            if (priorReceipt is null)
            {
                return null;
            }

            if (priorReceipt.RequestHash != hash && priorReceipt.RequestHash != Convert.ToHexStringLower(SHA256.HashData(
                Encoding.UTF8.GetBytes(JsonSerializer.Serialize(AnalysisRequestNormalizer.Normalize(request, false).Item1, Json)))))
            {
                throw new ApiException(409, "analysis_payload_conflict", "This completed news analysis has a different payload.");
            }

            return Duplicate(priorReceipt.Receipt);
        }
        var alreadyCompleted = await CompletedReceipt();
        if (alreadyCompleted is not null)
        {
            return alreadyCompleted;
        }

        var cache = new Dictionary<(string Material, string Version), MaterialVersion>();
        async Task<MaterialVersion> Material(string material, string version)
        {
            if (!cache.TryGetValue((material, version), out var value))
            {
                if (cache.Count >= 128)
                {
                    throw new ApiException(422, "source_budget", "Analysis is limited to 128 resolved source versions.");
                }

                value = await resolver.GetAsync(material, version, ct);
                if (value.MaterialId != material || value.VersionId != version)
                {
                    throw new ApiException(502, "invalid_context_identity", "ContextService returned a different material identity.");
                }

                cache.Add((material, version), value);
            }
            if (value.KnownAt > normalized.AsOf)
            {
                throw new ApiException(422, "source_not_known", "A source version was not known at analysis asOf.");
            }

            if (value.CompanyIds.Length > 0 && !value.CompanyIds.Contains(normalized.CompanyId))
            {
                throw new ApiException(422, "source_company_scope", "A source version belongs to another company scope.");
            }

            if (value.Kind == "company" && !value.CompanyIds.Contains(normalized.CompanyId))
            {
                throw new ApiException(422, "source_company_scope", "Company material requires the target company Guid.");
            }

            return value;
        }

        var news = await Material(normalized.NewsMaterialId, normalized.NewsVersionId);
        var trigger = normalized.Trigger is null ? null : await Material(normalized.Trigger.MaterialId, normalized.Trigger.VersionId);
        if (news.Kind != "news")
        {
            throw new ApiException(422, "not_news", "Analysis must refer to a news material version.");
        }

        var current = new Dictionary<string, EffectResponse>(StringComparer.Ordinal);
        foreach (var effect in await repository.GetEffects(normalized.CompanyId, normalized.NewsMaterialId))
        {
            var key = effect.EffectKey;
            if (effect.CurrentRevision.Question is { } q)
            {
                try
                {
                    key = QuestionKey(effect.LiteralObjectRef, q);
                }
                catch (ApiException ex) when (ex.Code == "invalid_question_identity") { /* Preserve unmigrated history without blocking unrelated questions. */ }
            }
            if (!current.TryAdd(key, effect))
            {
                throw new ApiException(409, "ambiguous_legacy_question", "Multiple stored histories normalize to this question; reconcile explicitly before writing.");
            }
        }
        foreach (var item in prepared)
        {
            var draft = item.Draft;
            if (normalized.Mechanisms?.Length > 0 && draft.Question is { } scopedQuestion && !normalized.Mechanisms.Contains(scopedQuestion.Mechanism))
            {
                throw new ApiException(422, "question_outside_scope", "This draft mechanism is outside the explicit analysis scope.");
            }

            if (trigger is not null && (draft.Question is null || !draft.SourceRefs.Any(s => s.MaterialId == trigger.MaterialId && s.VersionId == trigger.VersionId && s.ContentHash == trigger.ContentHash)))
            {
                throw new ApiException(422, "trigger_citation_required", "A source revision reassessment must cite its exact triggering original and identify the question.");
            }

            if (!draft.SourceRefs.Any(s => s.MaterialId == news.MaterialId && s.VersionId == news.VersionId && s.ContentHash == news.ContentHash))
            {
                throw new ApiException(422, "current_news_citation_required", "Each effect must cite the exact current news version and hash.");
            }

            foreach (var reference in draft.SourceRefs)
            {
                var source = await Material(reference.MaterialId, reference.VersionId);
                if (source.ContentHash != reference.ContentHash)
                {
                    throw new ApiException(422, "source_hash_mismatch", "A source hash does not match its immutable original version.");
                }
            }
            await ApplicabilityPolicy.Validate(draft, normalized.CompanyId, Material);
            if (current.TryGetValue(draft.EffectKey, out var existing))
            {
                if (draft.ExpectedPreviousRevisionId != existing.CurrentRevision.RevisionId)
                {
                    var concurrentlyCompleted = await CompletedReceipt();
                    if (concurrentlyCompleted is not null)
                    {
                        return concurrentlyCompleted;
                    }

                    throw new ApiException(409, "stale_previous_revision", "expectedPreviousRevisionId must be the actual latest revision.");
                }
                if (draft.LiteralObjectRef != existing.LiteralObjectRef)
                {
                    throw new ApiException(409, "literal_object_conflict", "An existing effectKey cannot be rebound to a different literal object.");
                }

                if (normalized.AsOf < existing.CurrentRevision.AsOf)
                {
                    throw new ApiException(409, "stale_analysis_time", "An unprocessed historical event cannot replace a more recent assessment; reassess at current knowledge time.");
                }

                if (trigger is not null)
                {
                    continue;
                }
                // Independent news continue the same question. A correction must belong
                // to the actual most recent revision of its own news, even if another
                // independent news has advanced the question head in between.
                var precedingNews = draft.Question is null ? existing.CurrentRevision :
                    existing.History.LastOrDefault(r => r.NewsMaterialId == news.MaterialId);
                if (precedingNews is null)
                {
                    continue;
                }

                var ancestor = news;
                var reached = false;
                for (var depth = 0; depth < 64 && ancestor.CorrectsVersionId is not null; depth++)
                {
                    if (ancestor.CorrectsVersionId == precedingNews.NewsVersionId)
                    {
                        reached = true;
                        break;
                    }
                    ancestor = await Material(news.MaterialId, ancestor.CorrectsVersionId);
                }
                if (!reached)
                {
                    throw new ApiException(409, "news_correction_lineage", "The news version must correct the effect's actual preceding news version.");
                }
            }
            else if (draft.ExpectedPreviousRevisionId is not null)
            {
                throw new ApiException(409, "missing_previous_effect", "A new effect cannot reference an unknown previous revision.");
            }
        }
        return await repository.Save(normalized, hash, prepared);
    }

    public static AnalysisReceipt Duplicate(AnalysisReceipt receipt) => AnalysisProtocol.Duplicate(receipt);

    private static void RequireCompany(Guid id) => RequestValidation.RequireCompany(id);
    public static void RequireId(string? value, string name) => RequestValidation.RequireId(value, name);

    public static QuestionIdentity NormalizeQuestion(QuestionIdentity question) => QuestionIdentityPolicy.NormalizeQuestion(question);
    public static string QuestionKey(string literal, QuestionIdentity input) => QuestionIdentityPolicy.QuestionKey(literal, input);
    public static string ProcessingVersion(string version, MaterialTrigger? trigger) => QuestionIdentityPolicy.ProcessingVersion(version, trigger);
    public static string CalculatedConclusion(string frame, string value, string unit, string period, string literal) => QuestionIdentityPolicy.CalculatedConclusion(frame, value, unit, period, literal);

}
