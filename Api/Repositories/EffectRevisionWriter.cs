using System.Net;
using System.Text.Json;
using AlfaHackaton.DapperContext.Common.Interfaces.Dapper;
using AlfaHackaton.DapperContext.Dapper;
using AlfaHackaton.Result;
using AlfaHackaton.Result.Errors;
using InsightService.Common;
using InsightService.Contracts.Analyses;
using InsightService.Models.Persistence;
using InsightService.Models.Policies;
using InsightService.Resources;

namespace InsightService.Repositories;

internal static class EffectRevisionWriter
{
    internal static async Task<Result<SavedEffectResponse>> SaveDraft(ITransaction transaction, AnalysisRequest request, PreparedDraft item, DateTimeOffset completedAt)
    {
        var draft = item.Draft;
        var canonical = draft.Question is not null;
        var effectId = await ResolveEffectId(transaction, request, draft);
        var current = await EffectReader.ReadEffect(transaction, effectId);
        if ((await EnsureIdentity(transaction, request, draft, effectId, current)).Error is { } identityError)
        {
            return new(identityError);
        }
        var revision = new EffectRevision("revision_" + Guid.NewGuid().ToString("N"),
            current?.CurrentRevision.RevisionId,
            request.NewsVersionId,
            request.AsOf,
            draft.Conclusion,
            draft.Status,
            draft.SourceRefs,
            draft.Calculation,
            item.CalculationResult,
            draft.Calculation is null ? null : true,
            completedAt,
            draft.Impact,
            request.NewsMaterialId,
            draft.Question,
            draft.Assessment,
            request.Trigger,
            draft.ModelExplanation,
            ApplicabilityPolicy.PublicationClass(draft));
        if ((await SaveRevision(transaction, effectId, revision, (current?.History.Count ?? 0) + 1, canonical)).Error is { } revisionError)
        {
            return new(revisionError);
        }
        if (canonical)
        {
            await RefreshDependencies(transaction, effectId, draft, request.AsOf);
        }

        return new(new SavedEffectResponse(effectId, revision.RevisionId, revision.PreviousRevisionId, false, revision), null);
    }

    private static async Task<string> ResolveEffectId(ITransaction transaction, AnalysisRequest request, EffectDraft draft)
    {
        var company = request.CompanyId.ToString("D");
        if (draft.Question is null)
        {
            return PersistenceIdentity.StableId("effect", company, request.NewsMaterialId, draft.EffectKey);
        }

        // A normalized key may continue one historical question. Preserve its id and lineage.
        if (draft.ExpectedPreviousRevisionId is { } previous)
        {
            var historicalId = await transaction.FirstOrDefault<string>(new QueryObject(InsightSql.FindPreviousQuestion, new
            {
                company,
                previous
            }));
            if (historicalId is not null)
            {
                return historicalId;
            }
        }

        return PersistenceIdentity.StableId("question", company, draft.EffectKey);
    }

    private static async Task<Result> EnsureIdentity(ITransaction transaction, AnalysisRequest request, EffectDraft draft, string effectId, EffectResponse? current)
    {
        var canonical = draft.Question is not null;
        if (canonical &&
            current is not null &&
            current.EffectKey != draft.EffectKey)
        {
            if (current.CurrentRevision.Question is not { } oldQuestion)
            {
                return new(new Error("The previous question does not normalize to the requested identity.", ErrorSource.Client, ErrorType.CorruptedInfo) { HttpStatusCode = HttpStatusCode.Conflict });
            }
            var historicalKey = QuestionIdentityPolicy.QuestionKey(current.LiteralObjectRef, oldQuestion);
            if (historicalKey.Error is { } identityError)
            {
                return new(identityError);
            }
            if (historicalKey.Content != draft.EffectKey)
            {
                return new(new Error("The previous question does not normalize to the requested identity.", ErrorSource.Client, ErrorType.CorruptedInfo) { HttpStatusCode = HttpStatusCode.Conflict });
            }

            await transaction.Command(new QueryObject(InsightSql.RekeyQuestion, new
            {
                key = draft.EffectKey,
                id = effectId
            }));
        }

        if (current is not null)
        {
            if (draft.ExpectedPreviousRevisionId != current.CurrentRevision.RevisionId)
            {
                return new(new Error("The effect changed before this transaction; no batch result was saved.", ErrorSource.Client, ErrorType.CorruptedInfo) { HttpStatusCode = HttpStatusCode.Conflict });
            }

            if (current.LiteralObjectRef != draft.LiteralObjectRef ||
                current.BankProfileId != request.BankProfileId)
            {
                return new(new Error("The literal object or bank-profile bridge conflicts with the stored effect.", ErrorSource.Client, ErrorType.CorruptedInfo) { HttpStatusCode = HttpStatusCode.Conflict });
            }

            return new();
        }

        if (draft.ExpectedPreviousRevisionId is not null)
        {
            return new(new Error("A new effect cannot reference a missing previous revision.", ErrorSource.Client, ErrorType.CorruptedInfo) { HttpStatusCode = HttpStatusCode.Conflict });
        }

        await transaction.Command(new QueryObject(canonical ? InsightSql.InsertQuestion : InsightSql.InsertEffect,
            new
            {
                id = effectId,
                company = request.CompanyId.ToString("D"),
                bank = request.BankProfileId,
                material = request.NewsMaterialId,
                key = draft.EffectKey,
                literal = draft.LiteralObjectRef
            }));
        return new();
    }

    private static async Task<Result> SaveRevision(ITransaction transaction, string effectId, EffectRevision revision, int sequence, bool canonical)
    {
        await transaction.Command(new QueryObject(canonical ? InsightSql.InsertQuestionRevision : InsightSql.InsertEffectRevision,
            new
            {
                revision = revision.RevisionId,
                effect = effectId,
                previous = revision.PreviousRevisionId,
                version = revision.NewsVersionId,
                sequence,
                json = JsonSerializer.Serialize(revision, AnalysisProtocol.Json)
            }));
        var changed = await transaction.CommandWithCount(new QueryObject(canonical ? InsightSql.AdvanceQuestionHead : InsightSql.AdvanceEffectHead,
            new
            {
                revision = revision.RevisionId,
                effect = effectId,
                previous = revision.PreviousRevisionId
            }));
        if (changed != 1)
        {
            return new(new Error("The effect changed before this update; no batch result was saved.", ErrorSource.Client, ErrorType.CorruptedInfo) { HttpStatusCode = HttpStatusCode.Conflict });
        }

        return new();
    }

    private static async Task RefreshDependencies(ITransaction transaction, string effectId, EffectDraft draft, DateTimeOffset asOf)
    {
        await transaction.Command(new QueryObject(InsightSql.DeleteQuestionDependencies, new
        {
            id = effectId
        }));
        var references = draft.SourceRefs.Select(s => (Material: s.MaterialId, Version: (string?)s.VersionId)).Concat((draft.Assessment?.Unknowns ?? []).Where(u => u.MaterialId is not null).Select(u => (u.MaterialId!, (string?)null))).DistinctBy(s => s.Item1);
        foreach (var (material, version) in references)
        {
            await transaction.Command(new QueryObject(InsightSql.InsertQuestionDependency, new
            {
                id = effectId,
                material,
                version
            }));
        }

        foreach (var source in draft.SourceRefs)
        {
            var parameters = new
            {
                id = effectId,
                material = source.MaterialId,
                version = source.VersionId,
                known = asOf.UtcTicks
            };
            if (await transaction.CommandWithCount(new QueryObject(InsightSql.ClearQuestionInvalidation, parameters)) > 0)
            {
                await transaction.Command(new QueryObject(InsightSql.InsertClearedValidityEvent, parameters));
            }

            await transaction.Command(new QueryObject(InsightSql.ClearInvalidationTime, parameters));
        }
    }
}
