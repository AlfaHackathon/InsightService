using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AlfaHackaton.Result;
using AlfaHackaton.Result.Errors;
using InsightService.Common;
using InsightService.Contracts.Analyses;
using InsightService.Contracts.Context;
using InsightService.Contracts.Questions;
using InsightService.Contracts.SourceReactions;
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
            return new(new Error("No completed analysis receipt exists for this news version.", ErrorSource.Client, ErrorType.NotFound) { HttpStatusCode = HttpStatusCode.NotFound });
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
            return new(new Error("Effect was not found.", ErrorSource.Client, ErrorType.NotFound) { HttpStatusCode = HttpStatusCode.NotFound });
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
                // Null optional claims serialize exactly like old requests. Only exact old
                // hashes may replay a receipt; legacy formatting cannot admit a new write.
                var compatible = false;
                foreach (var normalizeQuestions in new[] { true, false })
                {
                    foreach (var legacyCalculatedConclusion in new[] { false, true })
                    {
                        if (legacyCalculatedConclusion && request.Drafts.Any(d => d.CalculationClaim is not null))
                        {
                            continue;
                        }
                        var legacyNormalization = AnalysisRequestNormalizer.Normalize(request, normalizeQuestions, legacyCalculatedConclusion);
                        if (legacyNormalization.Error is not null)
                        {
                            continue;
                        }
                        var legacyHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
                            JsonSerializer.Serialize(legacyNormalization.Content.Item1, Json))));
                        compatible |= priorReceipt.RequestHash == legacyHash;
                    }
                }
                if (!compatible)
                {
                    return new(new Error("This completed news analysis has a different payload.", ErrorSource.Client, ErrorType.CorruptedInfo) { HttpStatusCode = HttpStatusCode.Conflict });
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
                    return new(new Error("Analysis is limited to 128 resolved source versions.", ErrorSource.Client, ErrorType.CorruptedInfo) { HttpStatusCode = HttpStatusCode.UnprocessableEntity });
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
                    return new(new Error("ContextService returned a different material identity.", ErrorSource.External, ErrorType.Undefined) { HttpStatusCode = HttpStatusCode.BadGateway });
                }

                cache.Add((material, version), value);
            }

            if (value.KnownAt > normalized.AsOf)
            {
                return new(new Error("A source version was not known at analysis asOf.", ErrorSource.Client, ErrorType.CorruptedInfo) { HttpStatusCode = HttpStatusCode.UnprocessableEntity });
            }

            if (value.CompanyIds.Length > 0 &&
                !value.CompanyIds.Contains(normalized.CompanyId))
            {
                return new(new Error("A source version belongs to another company scope.", ErrorSource.Client, ErrorType.CorruptedInfo) { HttpStatusCode = HttpStatusCode.UnprocessableEntity });
            }

            if (value.Kind == "company" &&
                !value.CompanyIds.Contains(normalized.CompanyId))
            {
                return new(new Error("Company material requires the target company Guid.", ErrorSource.Client, ErrorType.CorruptedInfo) { HttpStatusCode = HttpStatusCode.UnprocessableEntity });
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
            return new(new Error("Analysis must refer to a news material version.", ErrorSource.Client, ErrorType.CorruptedInfo) { HttpStatusCode = HttpStatusCode.UnprocessableEntity });
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
            }

            if (!current.TryAdd(key, effect))
            {
                return new(new Error("Multiple stored histories normalize to this question; reconcile explicitly before writing.", ErrorSource.Client, ErrorType.CorruptedInfo) { HttpStatusCode = HttpStatusCode.Conflict });
            }
        }

        foreach (var item in prepared)
        {
            var draft = item.Draft;
            if (normalized.Mechanisms?.Length > 0 &&
                draft.Question is { } scopedQuestion &&
                !normalized.Mechanisms.Contains(scopedQuestion.Mechanism))
            {
                return new(new Error("This draft mechanism is outside the explicit analysis scope.", ErrorSource.Client, ErrorType.CorruptedInfo) { HttpStatusCode = HttpStatusCode.UnprocessableEntity });
            }

            if (trigger is not null &&
                (draft.Question is null ||
                    !draft.SourceRefs.Any(s => s.MaterialId == trigger.MaterialId &&
                        s.VersionId == trigger.VersionId &&
                        s.ContentHash == trigger.ContentHash)))
            {
                return new(new Error("A source revision reassessment must cite its exact triggering original and identify the question.", ErrorSource.Client, ErrorType.CorruptedInfo) { HttpStatusCode = HttpStatusCode.UnprocessableEntity });
            }

            if (!draft.SourceRefs.Any(s => s.MaterialId == news.MaterialId &&
                    s.VersionId == news.VersionId &&
                    s.ContentHash == news.ContentHash))
            {
                return new(new Error("Each effect must cite the exact current news version and hash.", ErrorSource.Client, ErrorType.CorruptedInfo) { HttpStatusCode = HttpStatusCode.UnprocessableEntity });
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
                    return new(new Error("A source hash does not match its immutable original version.", ErrorSource.Client, ErrorType.CorruptedInfo) { HttpStatusCode = HttpStatusCode.UnprocessableEntity });
                }
            }

            if ((await ApplicabilityPolicy.Validate(draft, normalized.CompanyId, Material)).Error is { } applicabilityError)
            {
                return new(applicabilityError);
            }
            // Still validate source/hash/scope/applicability above. A diagnostic does
            // not claim a head update and must not enter revision/reassessment writes.
            if (AnalysisPublicationPolicy.IsDiagnostic(draft))
            {
                continue;
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

                    return new(new Error("expectedPreviousRevisionId must be the actual latest revision.", ErrorSource.Client, ErrorType.CorruptedInfo) { HttpStatusCode = HttpStatusCode.Conflict });
                }

                if (draft.LiteralObjectRef != existing.LiteralObjectRef)
                {
                    return new(new Error("An existing effectKey cannot be rebound to a different literal object.", ErrorSource.Client, ErrorType.CorruptedInfo) { HttpStatusCode = HttpStatusCode.Conflict });
                }

                if (normalized.AsOf < existing.CurrentRevision.AsOf)
                {
                    return new(new Error("An unprocessed historical event cannot replace a more recent assessment; reassess at current knowledge time.", ErrorSource.Client, ErrorType.CorruptedInfo) { HttpStatusCode = HttpStatusCode.Conflict });
                }

                var scope = await ReassessmentScopePolicy.IsIndependentUnknown(normalized, draft, existing, Material);
                if (scope.Error is { } scopeError)
                {
                    return new(scopeError);
                }
                if (scope.Content)
                {
                    return new(new Error("An independent unknown requires its own reassessment scope; it cannot replace an identified consequence without an object binding or a correction of its news or dependencies.", ErrorSource.Client, ErrorType.CorruptedInfo) { HttpStatusCode = HttpStatusCode.UnprocessableEntity });
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
                    return new(new Error("The news version must correct the effect's actual preceding news version.", ErrorSource.Client, ErrorType.CorruptedInfo) { HttpStatusCode = HttpStatusCode.Conflict });
                }
            }
            else if (draft.ExpectedPreviousRevisionId is not null)
            {
                return new(new Error("A new effect cannot reference an unknown previous revision.", ErrorSource.Client, ErrorType.CorruptedInfo) { HttpStatusCode = HttpStatusCode.Conflict });
            }
        }

        foreach (var item in prepared)
        {
            if (CalculationClaimPolicy.Validate(item.Draft, item.CalculationResult) is { } claimError)
            {
                return new(claimError);
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
}
