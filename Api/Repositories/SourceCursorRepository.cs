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
    public async Task<SourceCursor> GetSourceCursor(string source = "context")
    {
        using var transaction = await InsightTransaction.OpenAsync(context);
        var json = await transaction.FirstOrDefault<string>(new QueryObject(source == "context" ? InsightSql.ReadSourceCursor : InsightSql.ReadNamedSourceCursor, new
        {
            source
        }));
        return json is null ? new SourceCursor(0, 0, null) : JsonSerializer.Deserialize<SourceCursor>(json, AnalysisProtocol.Json)!;
    }

    public async Task<Result<SourceCursor>> SaveSourceCursor(SourceCursor cursor, string source = "context")
    {
        if (source is not ("context" or "company") || cursor.Sequence < 0 ||
            cursor.AfterQuestion?.Length > 200)
        {
            return new(new Error("A bounded source cursor is required.", ErrorSource.Client, ErrorType.CorruptedInfo) { HttpStatusCode = HttpStatusCode.BadRequest });
        }

        using var transaction = await InsightTransaction.OpenAsync(context);
        var version = await transaction.FirstOrDefault<long?>(new QueryObject(source == "context" ? InsightSql.ReadSourceCursorVersion : InsightSql.ReadNamedSourceCursorVersion, new
        {
            source
        })) ?? 0;
        if (version != cursor.Version)
        {
            return new(new Error("Another consumer advanced the source cursor.", ErrorSource.Client, ErrorType.CorruptedInfo) { HttpStatusCode = HttpStatusCode.Conflict });
        }

        var next = cursor with
        {
            Version = cursor.Version + 1
        };
        await transaction.Command(new QueryObject(source == "context" ? InsightSql.UpsertSourceCursor : InsightSql.UpsertNamedSourceCursor, new
        {
            v = next.Version,
            source,
            p = JsonSerializer.Serialize(next, AnalysisProtocol.Json)
        }));
        transaction.Commit();
        return new(next, null);
    }
}
