using System.Text.Json;
using AlfaHackaton.DapperContext.Common.Interfaces.Dapper;
using AlfaHackaton.DapperContext.Dapper;
using InsightService.Common;
using InsightService.Contracts.Analyses;
using InsightService.Models;
using InsightService.Models.Persistence;
using InsightService.Models.Policies;
using InsightService.Resources;

namespace InsightService.Repositories;

internal static class EffectRevisionWriter
{
    internal static async Task<SavedEffectResponse> SaveDraft(ITransaction transaction, AnalysisRequest request, PreparedDraft item, DateTimeOffset completedAt)
    {
        var draft = item.Draft;
        var canonical = draft.Question is not null;
        var effectId = await ResolveEffectId(transaction, request, draft);
        var current = await EffectReader.ReadEffect(transaction, effectId);
        await EnsureIdentity(transaction, request, draft, effectId, current);

        var revision = new EffectRevision("revision_" + Guid.NewGuid().ToString("N"),
            current?.CurrentRevision.RevisionId, request.NewsVersionId, request.AsOf,
            draft.Conclusion, draft.Status, draft.SourceRefs, draft.Calculation, item.CalculationResult,
            draft.Calculation is null ? null : true, completedAt, draft.Impact, request.NewsMaterialId,
            draft.Question, draft.Assessment, request.Trigger, draft.ModelExplanation, ApplicabilityPolicy.PublicationClass(draft));
        await SaveRevision(transaction, effectId, revision, (current?.History.Count ?? 0) + 1, canonical);
        if (canonical)
        {
            await RefreshDependencies(transaction, effectId, draft, request.AsOf);
        }
        return new SavedEffectResponse(effectId, revision.RevisionId, revision.PreviousRevisionId, false, revision);
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

    private static async Task EnsureIdentity(ITransaction transaction, AnalysisRequest request, EffectDraft draft, string effectId, EffectResponse? current)
    {
        var canonical = draft.Question is not null;
        if (canonical && current is not null && current.EffectKey != draft.EffectKey)
        {
            if (current.CurrentRevision.Question is not { } oldQuestion || QuestionIdentityPolicy.QuestionKey(current.LiteralObjectRef, oldQuestion) != draft.EffectKey)
            {
                throw new ApiException(409, "effect_identity_conflict", "The previous question does not normalize to the requested identity.");
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
                throw new ApiException(409, "stale_previous_revision", "The effect changed before this transaction; no batch result was saved.");
            }
            if (current.LiteralObjectRef != draft.LiteralObjectRef || current.BankProfileId != request.BankProfileId)
            {
                throw new ApiException(409, "effect_identity_conflict", "The literal object or bank-profile bridge conflicts with the stored effect.");
            }
            return;
        }
        if (draft.ExpectedPreviousRevisionId is not null)
        {
            throw new ApiException(409, "missing_previous_effect", "A new effect cannot reference a missing previous revision.");
        }
        await transaction.Command(new QueryObject(canonical ? InsightSql.InsertQuestion : InsightSql.InsertEffect, new
        {
            id = effectId,
            company = request.CompanyId.ToString("D"),
            bank = request.BankProfileId,
            material = request.NewsMaterialId,
            key = draft.EffectKey,
            literal = draft.LiteralObjectRef
        }));
    }

    private static async Task SaveRevision(ITransaction transaction, string effectId, EffectRevision revision, int sequence, bool canonical)
    {
        await transaction.Command(new QueryObject(canonical ? InsightSql.InsertQuestionRevision : InsightSql.InsertEffectRevision, new
        {
            revision = revision.RevisionId,
            effect = effectId,
            previous = revision.PreviousRevisionId,
            version = revision.NewsVersionId,
            sequence,
            json = JsonSerializer.Serialize(revision, AnalysisProtocol.Json)
        }));
        var changed = await transaction.CommandWithCount(new QueryObject(canonical ? InsightSql.AdvanceQuestionHead : InsightSql.AdvanceEffectHead, new
        {
            revision = revision.RevisionId,
            effect = effectId,
            previous = revision.PreviousRevisionId
        }));
        if (changed != 1)
        {
            throw new ApiException(409, "stale_previous_revision", "The effect changed before this update; no batch result was saved.");
        }
    }

    private static async Task RefreshDependencies(ITransaction transaction, string effectId, EffectDraft draft, DateTimeOffset asOf)
    {
        await transaction.Command(new QueryObject(InsightSql.DeleteQuestionDependencies, new
        {
            id = effectId
        }));
        var references = draft.SourceRefs.Select(s => (Material: s.MaterialId, Version: (string?)s.VersionId))
            .Concat((draft.Assessment?.Unknowns ?? []).Where(u => u.MaterialId is not null).Select(u => (u.MaterialId!, (string?)null)))
            .DistinctBy(s => s.Item1);
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
