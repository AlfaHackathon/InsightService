using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using AlfaHackaton.Controllers.Controllers;
using InsightService.Controllers;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Xunit;

public sealed class MvcContractTests : IDisposable
{
    private readonly PgTestDatabase database = new();
    private WebApplicationFactory<Program> Factory() => new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        builder.UseSetting("ConnectionStrings:Postgres", database.ConnectionString).UseSetting("Internal:Secret", "synthetic-mvc-test-internal").UseContentRoot(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../Api")))
            .ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Postgres"] = database.ConnectionString,
                ["Internal:Secret"] = "synthetic-mvc-test-internal"
            })));
    private static HttpClient Authorized(WebApplicationFactory<Program> factory)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Internal-Secret", "synthetic-mvc-test-internal");
        return client;
    }

    [Fact]
    public async Task MvcBindingAndErrorsKeepExistingJsonContract()
    {
        Assert.True(typeof(BaseController).IsAssignableFrom(typeof(AnalysesController)));
        using var factory = Factory();
        using var client = Authorized(factory);
        using var health = await client.GetAsync("/health");
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
        using var healthy = JsonDocument.Parse(await health.Content.ReadAsStringAsync());
        Assert.Equal("insight-service", healthy.RootElement.GetProperty("service").GetString());
        foreach (var url in new[]
        {
            "/insight-service/internal/analyses?companyId=invalid&newsMaterialId=news&newsVersionId=v1",
            "/insight-service/internal/analyses?companyId=" + Guid.NewGuid() + "&newsMaterialId=news",
            "/insight-service/internal/question-recipients?asOf=invalid"
        })
        {
            using var response = await client.GetAsync(url);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Equal("invalid_request", json.RootElement.GetProperty("code").GetString());
            Assert.Equal("The request body or parameters are invalid.", json.RootElement.GetProperty("message").GetString());
        }
        using var malformed = await client.PostAsync("/insight-service/internal/analyses", new StringContent("{", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, malformed.StatusCode);
        Assert.Contains("invalid_request", await malformed.Content.ReadAsStringAsync());
        using var absent = await client.GetAsync($"/insight-service/internal/analyses?companyId={Guid.NewGuid()}&newsMaterialId=news&newsVersionId=v1");
        Assert.Equal(HttpStatusCode.NotFound, absent.StatusCode);
        using var body = JsonDocument.Parse(await absent.Content.ReadAsStringAsync());
        Assert.Equal("analysis_not_found", body.RootElement.GetProperty("code").GetString());
        Assert.False(body.RootElement.TryGetProperty("errorMessage", out _));
        using var anonymous = factory.CreateClient();
        using var forbidden = await anonymous.GetAsync("/insight-service/internal/source-cursor");
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
        Assert.Contains("internal_access_denied", await forbidden.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task ReturnedValidationErrorPreservesCodeAndStatus()
    {
        using var factory = Factory();
        using var client = Authorized(factory);
        using var response = await client.PostAsJsonAsync("/insight-service/internal/analyses", new
        {
            companyId = Guid.NewGuid(),
            newsMaterialId = "news",
            newsVersionId = "v1",
            asOf = "2026-10-23T10:00:00Z",
            drafts = new[]
            {
                new
                {
                    effectKey = "effect",
                    literalObjectRef = "contract",
                    conclusion = "Synthetic conclusion",
                    status = "forecast",
                    modelExplanation = new string('x', 16001),
                    sourceRefs = new[] { new { materialId = "news", versionId = "v1", contentHash = new string('a', 64) } }
                }
            }
        });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("invalid_model_explanation", json.RootElement.GetProperty("code").GetString());
        Assert.Equal("Raw model explanation must be bounded.", json.RootElement.GetProperty("message").GetString());
        Assert.Equal(2, json.RootElement.EnumerateObject().Count());
    }

    [Fact]
    public async Task PreviewIncludesReturnedCalculationErrorsWithoutFailingTheBatch()
    {
        using var factory = Factory();
        using var client = Authorized(factory);
        using var response = await client.PostAsJsonAsync("/insight-service/internal/calculations/preview", new[]
        {
            new { expression = new { kind = "divide", left = new { kind = "value", value = "2" }, right = new { kind = "value", value = "1" } }, unit = "RUB", period = "2026" },
            new { expression = new { kind = "divide", left = new { kind = "value", value = "2" }, right = new { kind = "value", value = "0" } }, unit = "RUB", period = "2026" }
        });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var rows = json.RootElement.GetProperty("results");
        Assert.True(rows[0].GetProperty("valid").GetBoolean());
        Assert.Equal("2", rows[0].GetProperty("result").GetString());
        Assert.False(rows[1].GetProperty("valid").GetBoolean());
        Assert.Equal("division_by_zero", rows[1].GetProperty("code").GetString());
        Assert.False(rows[1].TryGetProperty("result", out _));
    }

    [Fact]
    public async Task MvcNullClaimAndPostgresCursorCompareAndSwapRemainObservable()
    {
        using var factory = Factory();
        using var client = Authorized(factory);
        using var empty = await client.PostAsJsonAsync("/insight-service/internal/news-jobs/claim", new
        {
            workerId = "worker"
        });
        Assert.Equal(HttpStatusCode.OK, empty.StatusCode);
        Assert.Equal("null", await empty.Content.ReadAsStringAsync());
        var responses = await Task.WhenAll(Enumerable.Range(0, 2).Select(sequence =>
            client.PostAsJsonAsync("/insight-service/internal/source-cursor", new
            {
                version = 0,
                sequence,
                afterQuestion = (string?)null
            })));
        try
        {
            Assert.Single(responses, response => response.StatusCode == HttpStatusCode.OK);
            var conflict = Assert.Single(responses, response => response.StatusCode == HttpStatusCode.Conflict);
            Assert.Contains("source_cursor_conflict", await conflict.Content.ReadAsStringAsync());
        }
        finally
        {
            foreach (var response in responses)
            {
                response.Dispose();
            }
        }
        using var current = await client.GetAsync("/insight-service/internal/source-cursor");
        using var json = JsonDocument.Parse(await current.Content.ReadAsStringAsync());
        Assert.Equal(1, json.RootElement.GetProperty("version").GetInt64());
    }
    public void Dispose() => database.Dispose();
}
