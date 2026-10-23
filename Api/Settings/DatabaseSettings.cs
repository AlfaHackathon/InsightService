using AlfaHackaton.DapperContext.Common.Interfaces.Dapper.Settings;
namespace InsightService.Settings;

public sealed class DatabaseSettings : IDapperSettings
{
    public string ConnectionString { get; set; } = "";
    public static DatabaseSettings FromConfiguration(IConfiguration configuration)
    {
        var connection = configuration.GetConnectionString("Postgres") ?? configuration["Storage:ConnectionString"];
        if (string.IsNullOrWhiteSpace(connection)) throw new InvalidOperationException("ConnectionStrings:Postgres must configure the durable PostgreSQL database.");
        return new DatabaseSettings { ConnectionString = connection };
    }
}
