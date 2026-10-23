using AlfaHackaton.DapperContext.Dapper;
using InsightService.Common;
using InsightService.Contracts.Analyses;
using InsightService.Resources;
using System.Text.Json;
using InsightService.Repositories.Postgres;
using AlfaHackaton.DapperContext.Common.Interfaces.Dapper;
namespace InsightService.Repositories;

internal static class EffectReader
{
    internal static async Task<EffectResponse?> ReadEffect(PgSession connection, string effectId, DateTimeOffset? asOf = null)
    {

        var canonical = effectId.StartsWith("question_", StringComparison.Ordinal);
        var command = new QueryObject(canonical ? InsightSql.ReadQuestionHead : InsightSql.ReadEffectHead);
        command.WithParameter("@id", effectId);
        string key, material, literal, head; string? bank; Guid company;
        using (var reader = await connection.Rows(command))
        {
            if (!reader.Read()) return null;
            key = reader.GetString(0); company = Guid.Parse(reader.GetString(1));
            bank = reader.IsDBNull(2) ? null : reader.GetString(2);
            material = reader.GetString(3); literal = reader.GetString(4); head = reader.GetString(5);
        }

        var historyCommand = new QueryObject(canonical ? InsightSql.ReadQuestionHistory : InsightSql.ReadEffectHistory);
        historyCommand.WithParameter("@id", effectId);
        var history = new List<EffectRevision>();
        using (var reader = await connection.Rows(historyCommand))
            while (reader.Read()) history.Add(JsonSerializer.Deserialize<EffectRevision>(reader.GetString(0), AnalysisProtocol.Json)!);
        var invalid = new QueryObject(InsightSql.HasPendingInvalidation);
        invalid.WithParameter("@id", effectId);
        invalid.WithParameter("@asof", asOf?.UtcTicks ?? long.MaxValue);
        return new EffectResponse(effectId, key, company, bank, material, literal,
            history.Single(r => r.RevisionId == head), history, canonical && (long)(await connection.Scalar(invalid))! != 0);
    }

}
