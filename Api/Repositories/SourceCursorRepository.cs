using System.Net;
using System.Text.Json;
using AlfaHackaton.DapperContext.Common.Interfaces.Dapper;
using AlfaHackaton.DapperContext.Dapper;
using AlfaHackaton.Result;
using AlfaHackaton.Result.Errors;
using InsightService.Common;
using InsightService.Contracts.NewsJobs;
using InsightService.Repositories.Postgres;
using InsightService.Resources;

namespace InsightService.Repositories;

internal sealed class SourceCursorRepository(IDapperContext context)
{
    public async Task<SourceCursor> GetSourceCursor()
    {
        using var transaction = await InsightTransaction.OpenAsync(context);
        var json = await transaction.FirstOrDefault<string>(new QueryObject(InsightSql.ReadSourceCursor));
        return json is null ? new SourceCursor(0, 0, null) : JsonSerializer.Deserialize<SourceCursor>(json, AnalysisProtocol.Json)!;
    }

    public async Task<Result<SourceCursor>> SaveSourceCursor(SourceCursor cursor)
    {
        if (cursor.Sequence < 0 ||
            cursor.AfterQuestion?.Length > 200)
        {
            return new(new Error("A bounded source cursor is required.", ErrorSource.Client, ErrorType.CorruptedInfo) { HttpStatusCode = HttpStatusCode.BadRequest });
        }

        using var transaction = await InsightTransaction.OpenAsync(context);
        var version = await transaction.FirstOrDefault<long?>(new QueryObject(InsightSql.ReadSourceCursorVersion)) ?? 0;
        if (version != cursor.Version)
        {
            return new(new Error("Another consumer advanced the source cursor.", ErrorSource.Client, ErrorType.CorruptedInfo) { HttpStatusCode = HttpStatusCode.Conflict });
        }

        var next = cursor with
        {
            Version = cursor.Version + 1
        };
        await transaction.Command(new QueryObject(InsightSql.UpsertSourceCursor, new
        {
            v = next.Version,
            p = JsonSerializer.Serialize(next, AnalysisProtocol.Json)
        }));
        transaction.Commit();
        return new(next, null);
    }
}
