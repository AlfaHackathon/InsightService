using AlfaHackaton.DapperContext;
using AlfaHackaton.DapperContext.Common.Interfaces.Dapper;
using InsightService.Settings;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

/// <summary>Each test instance owns a real PostgreSQL database and official migrations.</summary>
public sealed class PgTestDatabase : IDisposable
{
    private readonly string database = "insight_test_" + Guid.NewGuid().ToString("N");
    private readonly string admin = Environment.GetEnvironmentVariable("INSIGHT_TEST_POSTGRES")
        ?? "Host=127.0.0.1;Port=55441;Database=postgres;Username=postgres;Pooling=false";
    private readonly ServiceProvider services;
    public string ConnectionString { get; }
    public IDapperContext Context => services.GetRequiredService<IDapperContext>();
    public PgTestDatabase()
    {
        using (var connection = new NpgsqlConnection(admin))
        {
            connection.Open();
            using var command = new NpgsqlCommand("CREATE DATABASE " + database, connection);
            command.ExecuteNonQuery();
        }
        var target = new NpgsqlConnectionStringBuilder(admin) { Database = database, Pooling = false };
        ConnectionString = target.ToString();
        var settings = new DatabaseSettings { ConnectionString = ConnectionString };
        var collection = new ServiceCollection();
        collection.AddDapper(settings).AddMigrations(settings, typeof(Program).Assembly);
        services = collection.BuildServiceProvider();
        services.UseMigrations();
    }
    public void Migrate() => services.UseMigrations();
    public NpgsqlConnection Open()
    {
        var connection = new NpgsqlConnection(ConnectionString);
        connection.Open();
        return connection;
    }
    public void Dispose()
    {
        services.Dispose();
        using var connection = new NpgsqlConnection(admin);
        connection.Open();
        using var command = new NpgsqlCommand("DROP DATABASE " + database + " WITH (FORCE)", connection);
        command.ExecuteNonQuery();
    }
}
