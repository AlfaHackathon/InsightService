using System.Net;
using System.Text.Json;
using AlfaHackaton.DapperContext.Common.Interfaces.Dapper;
using AlfaHackaton.DapperContext.Dapper;
using AlfaHackaton.Result;
using AlfaHackaton.Result.Errors;
using InsightService.Common;
using InsightService.Contracts.Analyses;
using InsightService.Contracts.SourceReactions;
using InsightService.Repositories.Postgres;
using InsightService.Resources;

namespace InsightService.Repositories;

internal sealed class SourceReactionRepository(IDapperContext context)
{
    private const int PageSize = 64;
    public async Task<Result<ReactionPage>> React(SourceChangeRequest change)
    {
        if (RequestValidation.RequireId(change.EntityId, "changed entityId").Error is { } changedEntityIdError)
        {
            return new(changedEntityIdError);
        }
        if (RequestValidation.RequireId(change.VersionId, "changed versionId").Error is { } changedVersionIdError)
        {
            return new(changedVersionIdError);
        }
        if (change.KnownAt == default ||
            change.CompanyIds is null ||
            change.CompanyIds.Length > 512 ||
            change.Category is not ("material:company" or "material:market" or "group" or "role" or "company"))
        {
            return new(new Error("A bounded source or projection change is required.", ErrorSource.Client, ErrorType.CorruptedInfo) { HttpStatusCode = HttpStatusCode.BadRequest });
        }

        var projection = !change.Category.StartsWith("material:", StringComparison.Ordinal);
        using var transaction = await InsightTransaction.OpenAsync(context);
        var ids = await transaction.ListOrEmpty<string>(new QueryObject(InsightSql.FindAffectedQuestions,
            new
            {
                category = change.Category,
                after = change.AfterQuestion ?? "",
                time = change.KnownAt.UtcTicks,
                projection = projection ? 1 : 0,
                material = change.EntityId,
                version = change.VersionId,
                companies = JsonSerializer.Serialize(change.CompanyIds)
            }));
        var work = new List<QuestionWork>();
        foreach (var id in ids.Take(PageSize))
        {
            if (id.StartsWith("watch_", StringComparison.Ordinal))
            {
                var json = await transaction.CommandWithResponse<string>(new QueryObject(InsightSql.ReadAnalysisWatch, new
                {
                    id = id[6..]
                }));
                var receipt = JsonSerializer.Deserialize<AnalysisReceipt>(json, AnalysisProtocol.Json)!;
                work.Add(new QuestionWork(null, receipt.CompanyId, receipt.BankProfileId, receipt.NewsMaterialId, receipt.NewsVersionId, "analysis_watch", receipt.AnalysisId));
                continue;
            }

            var question = (await EffectReader.ReadEffect(transaction, id))!;
            if (!projection)
            {
                await Invalidate(transaction, id, change);
            }

            var revision = projection ? question.History.Last(h => h.AsOf <= change.KnownAt) : question.CurrentRevision;
            work.Add(new QuestionWork(id, question.CompanyId, question.BankProfileId, revision.NewsMaterialId!, revision.NewsVersionId));
        }

        transaction.Commit();
        return new(new ReactionPage(work.ToArray(), ids.Count > PageSize ? ids[PageSize - 1] : null), null);
    }

    private static async Task Invalidate(ITransaction transaction, string questionId, SourceChangeRequest change)
    {
        var parameters = new
        {
            id = questionId,
            material = change.EntityId,
            version = change.VersionId,
            known = change.KnownAt.UtcTicks
        };
        await transaction.Command(new QueryObject(InsightSql.UpsertQuestionInvalidation, parameters));
        await transaction.Command(new QueryObject(InsightSql.UpsertInvalidationTime, parameters));
        await transaction.Command(new QueryObject(InsightSql.InsertInvalidationEvent, parameters));
    }
}
