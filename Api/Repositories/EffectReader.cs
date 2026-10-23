using System.Text.Json;
using AlfaHackaton.DapperContext.Common.Interfaces.Dapper;
using AlfaHackaton.DapperContext.Dapper;
using InsightService.Common;
using InsightService.Contracts.Analyses;
using InsightService.Models.Persistence;
using InsightService.Resources;
namespace InsightService.Repositories;

internal static class EffectReader
{
    internal static async Task<EffectResponse?> ReadEffect(ITransaction transaction, string effectId, DateTimeOffset? asOf = null)
    {

        var canonical = effectId.StartsWith("question_", StringComparison.Ordinal);
        var parameters = new
        {
            id = effectId
        };
        var head = await transaction.FirstOrDefault<EffectHead>(
            new QueryObject(canonical ? InsightSql.ReadQuestionHead : InsightSql.ReadEffectHead, parameters));
        if (head is null)
        {
            return null;
        }

        var revisions = await transaction.ListOrEmpty<string>(
            new QueryObject(canonical ? InsightSql.ReadQuestionHistory : InsightSql.ReadEffectHistory, parameters));
        var history = revisions.Select(json => JsonSerializer.Deserialize<EffectRevision>(json, AnalysisProtocol.Json)!).ToArray();
        var invalidated = canonical &&
            await transaction.CommandWithResponse<bool>(
            new QueryObject(InsightSql.HasPendingInvalidation, new
            {
                id = effectId,
                asof = asOf?.UtcTicks ?? long.MaxValue
            }));

        return new EffectResponse(effectId,
            head.EffectKey,
            head.CompanyId,
            head.BankProfileId,
            head.NewsMaterialId,
            head.LiteralObjectRef,
            history.Single(r => r.RevisionId == head.CurrentRevisionId),
            history,
            invalidated);
    }

}
