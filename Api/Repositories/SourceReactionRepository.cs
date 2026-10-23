using AlfaHackaton.DapperContext.Dapper;
using InsightService.Repositories.Postgres;
using InsightService.Common;
using InsightService.Contracts.Analyses;
using InsightService.Contracts.SourceReactions;
using InsightService.Models;
using InsightService.Resources;
using System.Text.Json;

namespace InsightService.Repositories;

internal sealed class SourceReactionRepository(Func<Task<PgSession>> open)
{
    public async Task<ReactionPage> React(SourceChangeRequest change)
    {
        RequestValidation.RequireId(change.EntityId, "changed entityId"); RequestValidation.RequireId(change.VersionId, "changed versionId");
        if (change.KnownAt == default || change.CompanyIds is null || change.CompanyIds.Length > 512
            || change.Category is not ("material:company" or "material:market" or "group" or "role" or "company"))
            throw new ApiException(400, "invalid_source_change", "A bounded source or projection change is required.");
        var projection = !change.Category.StartsWith("material:", StringComparison.Ordinal);
        using var connection = await open();
        var query = new QueryObject(InsightSql.FindAffectedQuestions);
        query.WithParameter("@category", change.Category);
        query.WithParameter("@after", change.AfterQuestion ?? ""); query.WithParameter("@time", change.KnownAt.UtcTicks);
        query.WithParameter("@projection", projection ? 1 : 0); query.WithParameter("@material", change.EntityId);
        query.WithParameter("@version", change.VersionId); query.WithParameter("@companies", JsonSerializer.Serialize(change.CompanyIds));
        var ids = new List<string>(); using (var reader = await connection.Rows(query)) while (reader.Read()) ids.Add(reader.GetString(0));
        var work = new List<QuestionWork>();
        foreach (var id in ids.Take(64))
        {
            if (id.StartsWith("watch_", StringComparison.Ordinal))
            {
                var watch = new QueryObject(InsightSql.ReadAnalysisWatch);
                watch.WithParameter("@id", id[6..]);
                var receipt = JsonSerializer.Deserialize<AnalysisReceipt>((string)(await connection.Scalar(watch))!, AnalysisProtocol.Json)!;
                work.Add(new(null, receipt.CompanyId, receipt.BankProfileId, receipt.NewsMaterialId,
                    receipt.NewsVersionId, "analysis_watch", receipt.AnalysisId));
                continue;
            }
            var question = (await EffectReader.ReadEffect(connection, id))!;
            if (!projection)
            {
                var invalidate = new QueryObject(InsightSql.UpsertQuestionInvalidation);
                invalidate.WithParameter("@id", id); invalidate.WithParameter("@material", change.EntityId);
                invalidate.WithParameter("@version", change.VersionId); await connection.Execute(invalidate);
                invalidate.Sql = InsightSql.UpsertInvalidationTime;
                invalidate.WithParameter("@known", change.KnownAt.UtcTicks); await connection.Execute(invalidate);
                invalidate.Sql = InsightSql.InsertInvalidationEvent;
                await connection.Execute(invalidate);
            }
            var revision = projection ? question.History.Last(h => h.AsOf <= change.KnownAt) : question.CurrentRevision;
            work.Add(new(id, question.CompanyId, question.BankProfileId,
                revision.NewsMaterialId!, revision.NewsVersionId));
        }
        connection.Commit(); return new(work.ToArray(), ids.Count > 64 ? ids[63] : null);
    }
}
