using System.Text.Json;
using InsightService.Contracts.Reads;
using InsightService.Repositories;
using Microsoft.AspNetCore.Http;
using Xunit;

public sealed class InsightReadRepositoryTests : IDisposable
{
    private readonly PgTestDatabase database = new();
    private InsightReadRepository Reads() => new(database.Context);

    [Fact]
    public async Task PersonalReadsReturnCurrentHeadsFromCanonicalAndLegacyHistoryWithoutForeignCompanyData()
    {
        await InsightReadTestData.Personal(database);
        var page = await Reads().PersonalPage(InsightReadTestData.Company, new());
        Assert.Equal(["q-z", "q-a", "effect-m"], page.Items.Select(item => item.Id).ToArray());
        Assert.Null(page.NextCursor);
        Assert.All(page.Items, item => Assert.Equal(InsightReadTestData.Company, item.CompanyId));
        var detail = (await Reads().PersonalDetail(InsightReadTestData.Company, "q-a"))!;
        Assert.Equal("r-a2", detail.CurrentRevision.RevisionId);
        Assert.Equal("current-news", detail.NewsMaterialId);
        Assert.Equal("unknown", detail.CurrentRevision.Status);
        Assert.Null(detail.CurrentRevision.CalculationResult);
        Assert.Equal("missing_data", detail.CurrentRevision.PublicationClass);
        Assert.Equal("Actual company exposure remains unknown", Assert.Single(detail.CurrentRevision.Assessment!.Unknowns).Fact);
        Assert.Equal(InsightReadTestData.SourceHash, Assert.Single(detail.CurrentRevision.SourceRefs).ContentHash);
        Assert.Equal(4, detail.CurrentRevision.CompanyImportance!.Score);
        Assert.True(detail.RequiresReassessment); // A future validation cannot clear an already-known invalidation.
        Assert.False((await Reads().PersonalDetail(InsightReadTestData.Company, "q-z"))!.RequiresReassessment);
        Assert.Null(await Reads().PersonalDetail(InsightReadTestData.OtherCompany, "q-a"));
        Assert.Null(await Reads().PersonalHistory(InsightReadTestData.OtherCompany, "q-a", 1, null));
        Assert.Null(await Reads().PersonalDetail(InsightReadTestData.Company, "missing"));
    }

    [Fact]
    public async Task PersonalFiltersUseCurrentRevisionAndAssessedImportanceRatherThanHistoricalOrLegacyRanking()
    {
        await InsightReadTestData.Personal(database);
        var matching = await Reads().PersonalPage(InsightReadTestData.Company, new()
        {
            Status = "unknown", Mechanism = "liquidity", NewsMaterialId = "current-news", MinImportance = 4
        });
        Assert.Equal("q-a", Assert.Single(matching.Items).Id);
        Assert.Empty((await Reads().PersonalPage(InsightReadTestData.Company, new() { MinImportance = 5 })).Items);
        Assert.Empty((await Reads().PersonalPage(InsightReadTestData.Company, new() { NewsMaterialId = "origin-news" })).Items);
        Assert.Empty((await Reads().PersonalPage(InsightReadTestData.Company, new() { Status = "supported" })).Items);
        Assert.Equal("effect-m", Assert.Single((await Reads().PersonalPage(InsightReadTestData.Company, new() { Mechanism = "demand" })).Items).Id);
        Assert.Null((await Reads().PersonalDetail(InsightReadTestData.Company, "q-z"))!.CurrentRevision.CompanyImportance);
    }

    [Fact]
    public async Task PersonalKeysetContinuesAcrossTiedTimesWithoutDuplicatesAndRejectsScopeOrFilterChanges()
    {
        await InsightReadTestData.Personal(database);
        var first = await Reads().PersonalPage(InsightReadTestData.Company, new() { Limit = 1 });
        Assert.Equal("q-z", Assert.Single(first.Items).Id);
        Assert.NotNull(first.NextCursor);
        var second = await Reads().PersonalPage(InsightReadTestData.Company, new() { Limit = 1, Cursor = first.NextCursor });
        Assert.Equal("q-a", Assert.Single(second.Items).Id);
        var third = await Reads().PersonalPage(InsightReadTestData.Company, new() { Limit = 1, Cursor = second.NextCursor });
        Assert.Equal("effect-m", Assert.Single(third.Items).Id);
        Assert.Null(third.NextCursor);
        Assert.Equal(3, first.Items.Concat(second.Items).Concat(third.Items).Select(item => item.Id).Distinct().Count());
        await Assert.ThrowsAsync<BadHttpRequestException>(() => Reads().PersonalPage(InsightReadTestData.OtherCompany, new() { Cursor = first.NextCursor }));
        await Assert.ThrowsAsync<BadHttpRequestException>(() => Reads().PersonalPage(InsightReadTestData.Company, new() { Cursor = first.NextCursor, Status = "unknown" }));
        await Assert.ThrowsAsync<BadHttpRequestException>(() => Reads().GeneralPage(InsightReadTestData.Company, new() { Cursor = first.NextCursor }));
    }

    [Theory]
    [InlineData("q-a", "r-a2", "r-a1")]
    [InlineData("effect-m", "r-m2", "r-m1")]
    public async Task HistoryIsBoundedNewestFirstAndBindsContinuationToItsEffect(string id, string newest, string previous)
    {
        await InsightReadTestData.Personal(database);
        var first = (await Reads().PersonalHistory(InsightReadTestData.Company, id, 1, null))!;
        Assert.Equal(newest, Assert.Single(first.Items).RevisionId);
        Assert.NotNull(first.NextCursor);
        var second = (await Reads().PersonalHistory(InsightReadTestData.Company, id, 1, first.NextCursor))!;
        Assert.Equal(previous, Assert.Single(second.Items).RevisionId);
        Assert.Null(second.NextCursor);
        await Assert.ThrowsAsync<BadHttpRequestException>(() => Reads().PersonalHistory(InsightReadTestData.Company, "q-z", 1, first.NextCursor));
        await Assert.ThrowsAsync<BadHttpRequestException>(() => Reads().PersonalHistory(InsightReadTestData.OtherCompany, id, 1, first.NextCursor));
    }

    [Fact]
    public async Task GeneralReadsChooseLatestPermittedJobBeforeExpansionAndExposeOnlyOneScopedResult()
    {
        await InsightReadTestData.Jobs(database);
        var page = await Reads().GeneralPage(InsightReadTestData.Company, new());
        Assert.Equal(["partial-job", "permitted-latest", "permitted-latest"], page.Items.Select(item => item.JobId).ToArray());
        Assert.Equal([0, 1, 0], page.Items.Select(item => item.InsightIndex).ToArray());
        Assert.Equal("completed", page.Items[0].CompanyStatus);
        Assert.All(page.Items, item => Assert.Equal("lead", item.TextCompleteness));
        Assert.All(page.Items, item => Assert.Equal(InsightReadTestData.Company, item.CompanyId));
        Assert.Equal("Company exposure requires its own original", page.Items[0].Insight.GetProperty("unknowns")[0].GetString());
        var json = JsonSerializer.Serialize(page);
        Assert.DoesNotContain(InsightReadTestData.Hidden, json);
        Assert.DoesNotContain(InsightReadTestData.OtherCompany.ToString("D"), json);
        Assert.DoesNotContain("foreign-person", json);
        Assert.DoesNotContain("Checkpoint", json);
        var detail = (await Reads().GeneralDetail(InsightReadTestData.Company, page.Items[1].Id))!;
        Assert.Equal("permitted-latest:1", detail.Insight.GetProperty("title").GetString());
        Assert.Equal("newer-foreign", Assert.Single((await Reads().GeneralPage(InsightReadTestData.OtherCompany, new())).Items).JobId);
        Assert.Empty((await Reads().GeneralPage(Guid.Parse("33333333-3333-4333-8333-333333333333"), new())).Items);
    }

    [Fact]
    public async Task GeneralKeysetIncludesInsightIndexAndKeepsItsNewsAndCompanyScope()
    {
        await InsightReadTestData.Jobs(database);
        var first = await Reads().GeneralPage(InsightReadTestData.Company, new() { Limit = 1, NewsMaterialId = "shared-news" });
        Assert.Equal(1, Assert.Single(first.Items).InsightIndex);
        Assert.NotNull(first.NextCursor);
        var second = await Reads().GeneralPage(InsightReadTestData.Company, new() { Limit = 1, NewsMaterialId = "shared-news", Cursor = first.NextCursor });
        Assert.Equal(0, Assert.Single(second.Items).InsightIndex);
        Assert.NotEqual(first.Items[0].Id, second.Items[0].Id);
        Assert.Null(second.NextCursor);
        await Assert.ThrowsAsync<BadHttpRequestException>(() => Reads().GeneralPage(InsightReadTestData.Company, new() { Cursor = first.NextCursor }));
        await Assert.ThrowsAsync<BadHttpRequestException>(() => Reads().GeneralPage(InsightReadTestData.OtherCompany, new() { Cursor = first.NextCursor, NewsMaterialId = "shared-news" }));
        await Assert.ThrowsAsync<BadHttpRequestException>(() => Reads().GeneralPage(InsightReadTestData.Company, new() { MinImportance = 3 }));
    }

    [Fact]
    public async Task GeneralSegmentPlanKeepsAllTwentyFourInsightsAcrossPageAndDetailBoundaries()
    {
        await InsightReadTestData.Job(database, "segment-plan", 50, [InsightReadTestData.Company], count: 24);
        var first = await Reads().GeneralPage(InsightReadTestData.Company, new() { Limit = 20 });
        Assert.Equal(20, first.Items.Count);
        Assert.Equal(23, first.Items[0].InsightIndex);
        Assert.Equal(4, first.Items[^1].InsightIndex);
        Assert.NotNull(first.NextCursor);
        Assert.Equal("segment-plan:23", (await Reads().GeneralDetail(InsightReadTestData.Company, first.Items[0].Id))!.Insight.GetProperty("title").GetString());
        var second = await Reads().GeneralPage(InsightReadTestData.Company, new() { Limit = 20, Cursor = first.NextCursor });
        Assert.Equal([3, 2, 1, 0], second.Items.Select(item => item.InsightIndex).ToArray());
        Assert.Null(second.NextCursor);
        Assert.Equal(24, first.Items.Concat(second.Items).Select(item => item.Id).Distinct().Count());
    }

    [Fact]
    public async Task OlderGeneralDetailRemainsReadableWhenListingAdvancesButStillRequiresItsSelectedCompany()
    {
        await InsightReadTestData.Job(database, "older", 10, [InsightReadTestData.Company]);
        var older = Assert.Single((await Reads().GeneralPage(InsightReadTestData.Company, new())).Items);
        await InsightReadTestData.Job(database, "newer", 20, [InsightReadTestData.Company]);
        Assert.Equal("newer", Assert.Single((await Reads().GeneralPage(InsightReadTestData.Company, new())).Items).JobId);
        Assert.Equal("older", (await Reads().GeneralDetail(InsightReadTestData.Company, older.Id))!.JobId);
        Assert.Null(await Reads().GeneralDetail(InsightReadTestData.OtherCompany, older.Id));
    }

    [Theory]
    [InlineData("(invalid)")]
    [InlineData("bnVsbA")]
    [InlineData("e30")]
    public async Task InvalidCursorAndGeneralIdentifierFailAsBadRequests(string cursor)
    {
        await Assert.ThrowsAsync<BadHttpRequestException>(() => Reads().PersonalPage(InsightReadTestData.Company, new() { Cursor = cursor }));
        await Assert.ThrowsAsync<BadHttpRequestException>(() => Reads().GeneralDetail(InsightReadTestData.Company, cursor));
    }

    public void Dispose() => database.Dispose();
}
