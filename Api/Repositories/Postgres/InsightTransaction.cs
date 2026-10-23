using AlfaHackaton.DapperContext.Common.Interfaces.Dapper;
using AlfaHackaton.DapperContext.Dapper;
using InsightService.Resources;

namespace InsightService.Repositories.Postgres;

/// <summary>
/// Keeps repository reads and writes on one organization transaction under the insight lock.
/// Reads stay on that transaction rather than opening IDapperContext's separate connections.
/// </summary>
internal static class InsightTransaction
{
    public static async Task<ITransaction> OpenAsync(IDapperContext context)
    {
        var transaction = context.BeginTransaction();
        try
        {
            await transaction.Command(new QueryObject(InsightSql.TransactionLock));
            return transaction;
        }
        catch
        {
            transaction.Dispose();
            throw;
        }
    }

}
