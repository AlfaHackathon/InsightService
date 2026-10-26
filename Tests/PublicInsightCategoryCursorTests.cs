using System.Text.Json;
using InsightService.Common;
using InsightService.Contracts.PublicReads;
using InsightService.Repositories;
using InsightService.Services.PublicReads;
using Xunit;

public sealed class PublicInsightCategoryCursorTests
{
    private static string Cursor(string kind, string? category)
    {
        var filter = new List<object?> { null, null, null, null };
        if (category is not null)
        {
            filter.Add(category);
        }

        return Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(new
        {
            version = 1,
            companyId = PublicInsightReadFixture.Company,
            kind,
            filter = JsonSerializer.Serialize(filter, AnalysisProtocol.Json),
            after = 100L,
            id = "known-id",
            index = 0
        }, AnalysisProtocol.Json)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    [Theory]
    [InlineData("personal", "risk", "opportunity")]
    [InlineData("personal", "risk", null)]
    [InlineData("personal", null, "risk")]
    [InlineData("general", "risk", "event")]
    [InlineData("general", "risk", null)]
    [InlineData("general", null, "opportunity")]
    public async Task ExistingRepositoryCursorRejectsCategoryChangesBeforeAnyPersistenceOrSourceWork(string kind, string? oldCategory, string? newCategory)
    {
        var fixture = new PublicInsightReadFixture();
        // A persistence dependency is intentionally absent: cursor binding must fail before BeginTransaction.
        // This executes the actual local repository/cursor, without starting a PostgreSQL service or database.
        var store = new LocalPublicInsightReadStore(new InsightReadRepository(null!));
        var service = new PublicInsightReadService(fixture, fixture, store);
        var error = await Assert.ThrowsAsync<PublicInsightReadException>(() => service.Feed(PublicInsightReadFixture.User,
            new PublicInsightFeedQuery { Kind = kind, Category = newCategory, Cursor = Cursor(kind, oldCategory) }, default));
        Assert.Equal(400, error.StatusCode);
        Assert.Equal("INVALID_CURSOR", error.Code);
        Assert.Equal(0, fixture.SourceCalls);
    }
}
