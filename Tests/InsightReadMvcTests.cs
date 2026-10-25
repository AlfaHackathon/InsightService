using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using InsightService.Contracts.Reads;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Xunit;

public sealed class InsightReadMvcTests : IDisposable
{
    private readonly PgTestDatabase database = new();
    private string Root => $"/insight-service/internal/read/companies/{InsightReadTestData.Company:D}";
    private WebApplicationFactory<Program> Factory() => new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        builder.UseSetting("ConnectionStrings:Postgres", database.ConnectionString)
            .UseSetting("Internal:Secret", "synthetic-insight-read-test")
            .UseContentRoot(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../Api")))
            .ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Postgres"] = database.ConnectionString,
                ["Internal:Secret"] = "synthetic-insight-read-test"
            })));
    private static HttpClient Authorized(WebApplicationFactory<Program> factory)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Internal-Secret", "synthetic-insight-read-test");
        return client;
    }

    [Theory]
    [InlineData("personal")]
    [InlineData("personal/q-a")]
    [InlineData("personal/q-a/history")]
    [InlineData("general")]
    [InlineData("general/guessed-id")]
    public async Task EveryReadRouteRequiresInternalCredentialBeforeReadingOrDecodingIdentifiers(string suffix)
    {
        using var factory = Factory();
        using var client = factory.CreateClient();
        using var response = await client.GetAsync(Root + "/" + suffix);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("internal_access_denied", (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
    }

    [Fact]
    public async Task PersonalHttpContractHasBoundedPagesSeparateHistoryAndUniformScopedNotFound()
    {
        await InsightReadTestData.Personal(database);
        using var factory = Factory();
        using var client = Authorized(factory);
        var page = await client.GetFromJsonAsync<InsightReadPage<PersonalInsightRead>>(Root + "/personal?limit=1");
        Assert.Equal("q-z", Assert.Single(page!.Items).Id);
        Assert.NotNull(page.NextCursor);
        var next = await client.GetFromJsonAsync<InsightReadPage<PersonalInsightRead>>(Root + "/personal?limit=1&cursor=" + Uri.EscapeDataString(page.NextCursor));
        Assert.Equal("q-a", Assert.Single(next!.Items).Id);
        var filtered = await client.GetFromJsonAsync<InsightReadPage<PersonalInsightRead>>(Root + "/personal?status=unknown&mechanism=liquidity&newsMaterialId=current-news&minImportance=4");
        Assert.Equal("q-a", Assert.Single(filtered!.Items).Id);
        var detail = await client.GetFromJsonAsync<JsonElement>(Root + "/personal/q-a");
        Assert.False(detail.TryGetProperty("history", out _));
        Assert.False(detail.TryGetProperty("bankProfileId", out _));
        Assert.True(detail.GetProperty("requiresReassessment").GetBoolean());
        Assert.Equal("r-a2", detail.GetProperty("currentRevision").GetProperty("revisionId").GetString());
        Assert.Equal("unknown", detail.GetProperty("currentRevision").GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, detail.GetProperty("currentRevision").GetProperty("calculationResult").ValueKind);
        var history = await client.GetFromJsonAsync<JsonElement>(Root + "/personal/q-a/history?limit=1");
        Assert.Equal("r-a2", history.GetProperty("items")[0].GetProperty("revisionId").GetString());
        var historyCursor = history.GetProperty("nextCursor").GetString();
        Assert.NotNull(historyCursor);
        var older = await client.GetFromJsonAsync<JsonElement>(Root + "/personal/q-a/history?limit=1&cursor=" + Uri.EscapeDataString(historyCursor));
        Assert.Equal("r-a1", older.GetProperty("items")[0].GetProperty("revisionId").GetString());
        Assert.Equal(JsonValueKind.Null, older.GetProperty("nextCursor").ValueKind);
        foreach (var path in new[] { "/personal/q-foreign", "/personal/q-foreign/history", "/personal/absent", "/personal/absent/history" })
        {
            using var missing = await client.GetAsync(Root + path);
            Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
            var body = await missing.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("insight_not_found", body.GetProperty("code").GetString());
            Assert.Single(body.EnumerateObject());
        }
    }

    [Fact]
    public async Task GeneralHttpResponseIsProjectedAndOpaqueIdentifierDoesNotGrantAccessToOtherCompany()
    {
        await InsightReadTestData.Jobs(database);
        using var factory = Factory();
        using var client = Authorized(factory);
        var page = await client.GetFromJsonAsync<InsightReadPage<GeneralInsightRead>>(Root + "/general");
        Assert.Equal(3, page!.Items.Count);
        var partial = page.Items[0];
        Assert.Equal("partial-job", partial.JobId);
        Assert.Equal("completed", partial.CompanyStatus);
        Assert.Equal("lead", partial.TextCompleteness);
        using var detail = await client.GetAsync(Root + "/general/" + partial.Id);
        Assert.Equal(HttpStatusCode.OK, detail.StatusCode);
        var raw = await detail.Content.ReadAsStringAsync();
        Assert.DoesNotContain(InsightReadTestData.Hidden, raw);
        Assert.DoesNotContain(InsightReadTestData.OtherCompany.ToString("D"), raw);
        var json = JsonSerializer.Deserialize<JsonElement>(raw);
        Assert.False(json.TryGetProperty("checkpoint", out _));
        Assert.False(json.TryGetProperty("roleViews", out _));
        Assert.Equal("partial-job:0", json.GetProperty("insight").GetProperty("title").GetString());
        using var denied = await client.GetAsync($"/insight-service/internal/read/companies/{InsightReadTestData.OtherCompany:D}/general/{partial.Id}");
        Assert.Equal(HttpStatusCode.NotFound, denied.StatusCode);
        Assert.Equal("insight_not_found", (await denied.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
    }

    [Theory]
    [InlineData("personal?limit=0")]
    [InlineData("personal?limit=21")]
    [InlineData("personal?limit=invalid")]
    [InlineData("personal?minImportance=0")]
    [InlineData("personal?minImportance=6")]
    [InlineData("personal?cursor=(invalid)")]
    [InlineData("personal/q-a/history?limit=21")]
    [InlineData("general?status=unknown")]
    [InlineData("general?mechanism=liquidity")]
    [InlineData("general?minImportance=3")]
    [InlineData("general/(invalid)")]
    public async Task InvalidBindingsCursorsAndUnsupportedFiltersUseBadRequestEnvelope(string suffix)
    {
        using var factory = Factory();
        using var client = Authorized(factory);
        using var response = await client.GetAsync(Root + "/" + suffix);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("invalid_request", body.GetProperty("code").GetString());
        Assert.Equal("The request body or parameters are invalid.", body.GetProperty("message").GetString());
    }

    public void Dispose() => database.Dispose();
}
