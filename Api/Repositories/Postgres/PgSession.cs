using AlfaHackaton.DapperContext.Common.Interfaces.Dapper;
using AlfaHackaton.DapperContext.Dapper;
using InsightService.Resources;
using System.Text.Json;

namespace InsightService.Repositories.Postgres;

/// <summary>
/// Owns one real organization ITransaction for the whole repository operation.
/// ITransaction supports QueryFirst but no list API: Rows wraps the SELECT in
/// a JSON aggregate and executes it through that same transaction. It never uses
/// IDapperContext.ListOrEmpty, which would read from a different connection.
/// </summary>
internal sealed class PgSession : IDisposable
{
    private readonly ITransaction transaction;
    private PgSession(ITransaction transaction) => this.transaction = transaction;
    public static async Task<PgSession> Open(IDapperContext context)
    {
        var session = new PgSession(context.BeginTransaction());
        try
        {
            await session.transaction.Command(new QueryObject(InsightSql.TransactionLock));
            return session;
        }
        catch { session.Dispose(); throw; }
    }
    public async Task<PgRows> Rows(QueryObject query)
    {
        var sql = string.Format(System.Globalization.CultureInfo.InvariantCulture, InsightSql.RowsEnvelope, query.Sql.TrimEnd(';'));
        var json = await transaction.CommandWithResponse<string>(new QueryObject(sql, query.Parameters));
        return new PgRows(JsonDocument.Parse(json));
    }
    public async Task<object?> Scalar(QueryObject query)
    {
        using var rows = await Rows(query);
        return rows.Read() ? rows.Value(0) : null;
    }
    public Task<long> Execute(QueryObject command)
    {
        var sql = string.Format(System.Globalization.CultureInfo.InvariantCulture, InsightSql.AffectedRowsEnvelope, command.Sql.TrimEnd(';'));
        return transaction.CommandWithResponse<long>(new QueryObject(sql, command.Parameters));
    }
    public void Commit() => transaction.Commit();
    public void Dispose() => transaction.Dispose();
}

internal static class QueryParameters
{
    public static void WithParameter(this QueryObject query, string name, object? value)
    {
        query.Parameters ??= new Dictionary<string, object?>();
        ((Dictionary<string, object?>)query.Parameters)[name.TrimStart('@', '$')] = value is DBNull ? null : value;
    }
}

internal sealed class PgRows(JsonDocument document) : IDisposable
{
    private int position = -1;
    public bool Read() => ++position < document.RootElement.GetArrayLength();
    private JsonElement Cell(int ordinal) => document.RootElement[position].EnumerateObject().ElementAt(ordinal).Value;
    public bool IsDBNull(int ordinal) => Cell(ordinal).ValueKind == JsonValueKind.Null;
    public string GetString(int ordinal) => Cell(ordinal).GetString()!;
    public long GetInt64(int ordinal) => Cell(ordinal).GetInt64();
    public object? Value(int ordinal) => Cell(ordinal).ValueKind switch
    {
        JsonValueKind.Null => null,
        JsonValueKind.Number => Cell(ordinal).GetInt64(),
        JsonValueKind.True => 1L,
        JsonValueKind.False => 0L,
        _ => GetString(ordinal)
    };
    public void Dispose() => document.Dispose();
}
