using AlfaHackaton.DapperContext.Dapper;
using InsightService.Common;
using InsightService.Contracts.NewsJobs;
using InsightService.Models;
using InsightService.Resources;
using System.Text.Json;
using InsightService.Repositories.Postgres;
using AlfaHackaton.DapperContext.Common.Interfaces.Dapper;
namespace InsightService.Repositories;

internal sealed class SourceCursorRepository(Func<Task<PgSession>> open)
{
    public async Task<SourceCursor> GetSourceCursor()
    {
        using var c = await open(); var cmd = new QueryObject(InsightSql.ReadSourceCursor);
        return (await c.Scalar(cmd)) is string json ? JsonSerializer.Deserialize<SourceCursor>(json, AnalysisProtocol.Json)! : new(0, 0, null);
    }
    public async Task<SourceCursor> SaveSourceCursor(SourceCursor cursor)
    {
        if (cursor.Sequence < 0 || cursor.AfterQuestion?.Length > 200) throw new ApiException(400, "invalid_source_cursor", "A bounded source cursor is required.");
        using var c = await open(); var cmd = new QueryObject(InsightSql.ReadSourceCursorVersion); var old = (await c.Scalar(cmd));
        if ((old is null ? 0 : (long)old) != cursor.Version) throw new ApiException(409, "source_cursor_conflict", "Another consumer advanced the source cursor.");
        var next = cursor with { Version = cursor.Version + 1 };
        cmd.Sql = InsightSql.UpsertSourceCursor;
        cmd.WithParameter("@v", next.Version); cmd.WithParameter("@p", JsonSerializer.Serialize(next, AnalysisProtocol.Json)); await c.Execute(cmd);
        c.Commit(); return next;
    }
}
