using AlfaHackaton.DapperContext.Dapper;
using InsightService.Common;
using InsightService.Contracts.Analyses;
using InsightService.Contracts.Calculations;
using InsightService.Models;
using InsightService.Models.Persistence;
using InsightService.Models.Policies;
using InsightService.Resources;
using System.Text.Json;
using InsightService.Repositories.Postgres;
using AlfaHackaton.DapperContext.Common.Interfaces.Dapper;
namespace InsightService.Repositories;

internal static class EffectRevisionWriter
{
    internal static async Task<SavedEffectResponse> SaveDraft(PgSession connection, AnalysisRequest request, PreparedDraft item, DateTimeOffset completedAt)
    {
        var draft = item.Draft;
        var canonical = draft.Question is not null;
        var effectId = canonical ? PersistenceIdentity.StableId("question", request.CompanyId.ToString("D"), draft.EffectKey)
            : PersistenceIdentity.StableId("effect", request.CompanyId.ToString("D"), request.NewsMaterialId, draft.EffectKey);
        // A normalized key may continue one historical question. Preserve its id and lineage.
        if (canonical && draft.ExpectedPreviousRevisionId is { } previous)
        {
            var find = new QueryObject(InsightSql.FindPreviousQuestion);
            find.WithParameter("@company", request.CompanyId.ToString("D"));
            find.WithParameter("@previous", previous);
            if ((await connection.Scalar(find)) is string historicalId) effectId = historicalId;
        }
        var current = await EffectReader.ReadEffect(connection, effectId);
        if (canonical && current is not null && current.EffectKey != draft.EffectKey)
        {
            if (current.CurrentRevision.Question is not { } oldQuestion || QuestionIdentityPolicy.QuestionKey(current.LiteralObjectRef, oldQuestion) != draft.EffectKey)
                throw new ApiException(409, "effect_identity_conflict", "The previous question does not normalize to the requested identity.");
            var rekey = new QueryObject(InsightSql.RekeyQuestion);
            rekey.WithParameter("@key", draft.EffectKey); rekey.WithParameter("@id", effectId); await connection.Execute(rekey);
        }
        if (current is not null)
        {
            if (draft.ExpectedPreviousRevisionId != current.CurrentRevision.RevisionId)
                throw new ApiException(409, "stale_previous_revision", "The effect changed before this transaction; no batch result was saved.");
            if (current.LiteralObjectRef != draft.LiteralObjectRef || current.BankProfileId != request.BankProfileId)
                throw new ApiException(409, "effect_identity_conflict", "The literal object or bank-profile bridge conflicts with the stored effect.");
        }
        else
        {
            if (draft.ExpectedPreviousRevisionId is not null)
                throw new ApiException(409, "missing_previous_effect", "A new effect cannot reference a missing previous revision.");

            var insert = new QueryObject(canonical ? InsightSql.InsertQuestion : InsightSql.InsertEffect);
            insert.WithParameter("@id", effectId);
            insert.WithParameter("@company", request.CompanyId.ToString("D"));
            insert.WithParameter("@bank", (object?)request.BankProfileId ?? DBNull.Value);
            insert.WithParameter("@material", request.NewsMaterialId);
            insert.WithParameter("@key", draft.EffectKey);
            insert.WithParameter("@literal", draft.LiteralObjectRef);
            await connection.Execute(insert);
        }
        var revision = new EffectRevision("revision_" + Guid.NewGuid().ToString("N"),
            current?.CurrentRevision.RevisionId, request.NewsVersionId, request.AsOf,
            draft.Conclusion, draft.Status, draft.SourceRefs, draft.Calculation, item.CalculationResult,
            draft.Calculation is null ? null : true, completedAt, draft.Impact, request.NewsMaterialId, draft.Question, draft.Assessment, request.Trigger, draft.ModelExplanation, ApplicabilityPolicy.PublicationClass(draft));
        {

            var insert = new QueryObject(canonical ? InsightSql.InsertQuestionRevision : InsightSql.InsertEffectRevision);
            insert.WithParameter("@revision", revision.RevisionId);
            insert.WithParameter("@effect", effectId);
            insert.WithParameter("@previous", (object?)revision.PreviousRevisionId ?? DBNull.Value);
            insert.WithParameter("@version", request.NewsVersionId);
            insert.WithParameter("@sequence", (current?.History.Count ?? 0) + 1);
            insert.WithParameter("@json", JsonSerializer.Serialize(revision, AnalysisProtocol.Json));
            await connection.Execute(insert);
        }
        {

            var update = new QueryObject(canonical ? InsightSql.AdvanceQuestionHead : InsightSql.AdvanceEffectHead);
            update.WithParameter("@revision", revision.RevisionId);
            update.WithParameter("@effect", effectId);
            update.WithParameter("@previous", (object?)revision.PreviousRevisionId ?? DBNull.Value);
            if ((await connection.Execute(update)) != 1)
                throw new ApiException(409, "stale_previous_revision", "The effect changed before this update; no batch result was saved.");
        }

        if (canonical)
        {
            var dependencies = new QueryObject(InsightSql.DeleteQuestionDependencies);
            dependencies.WithParameter("@id", effectId); await connection.Execute(dependencies);
            var refs = draft.SourceRefs.Select(s => (s.MaterialId, Version: (string?)s.VersionId))
                .Concat((draft.Assessment?.Unknowns ?? []).Where(u => u.MaterialId is not null).Select(u => (u.MaterialId!, (string?)null)))
                .DistinctBy(s => s.Item1);
            foreach (var (material, version) in refs)
            {
                var insert = new QueryObject(InsightSql.InsertQuestionDependency);
                insert.WithParameter("@id", effectId); insert.WithParameter("@material", material);
                insert.WithParameter("@version", (object?)version ?? DBNull.Value); await connection.Execute(insert);
            }
            foreach (var source in draft.SourceRefs)
            {
                var clear = new QueryObject(InsightSql.ClearQuestionInvalidation);
                clear.WithParameter("@id", effectId); clear.WithParameter("@material", source.MaterialId);
                clear.WithParameter("@version", source.VersionId);
                if ((await connection.Execute(clear)) > 0)
                {
                    var validity = new QueryObject(InsightSql.InsertClearedValidityEvent);
                    validity.WithParameter("@id", effectId); validity.WithParameter("@material", source.MaterialId);
                    validity.WithParameter("@version", source.VersionId); validity.WithParameter("@known", request.AsOf.UtcTicks); await connection.Execute(validity);
                }
                clear.Sql = InsightSql.ClearInvalidationTime;
                await connection.Execute(clear);
            }
        }
        return new SavedEffectResponse(effectId, revision.RevisionId, revision.PreviousRevisionId, false, revision);
    }
}
