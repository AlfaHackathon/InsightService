using Npgsql;
using Xunit;

public sealed class TimestampPrecisionTests : IDisposable
{
    private readonly PgTestDatabase database = new();
    [Theory]
    [InlineData("2026-10-23T08:01:02.1234567+03:00")]
    [InlineData("2026-10-23T08:01:02.0000001Z")]
    [InlineData("2026-10-23T08:01:02.9999999-04:30")]
    [InlineData("9999-12-31T23:59:59.9999999+00:00")]
    [InlineData("0001-01-01T00:00:00+00:00")]
    public async Task SqlKnowledgeTimeExactlyMatchesDotNetHundredNanosecondTicks(string value)
    {
        using var connection = database.Open();
        using var command = new NpgsqlCommand("SELECT insight_utc_ticks(@value)", connection);
        command.Parameters.AddWithValue("value", value);
        var actual = Assert.IsType<long>(await command.ExecuteScalarAsync());
        Assert.Equal(DateTimeOffset.Parse(value).UtcTicks, actual);
    }
    public void Dispose() => database.Dispose();
}
