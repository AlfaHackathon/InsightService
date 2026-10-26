using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using AlfaHackaton.Controllers.Controllers;
using InsightService.Contracts.Analyses;
using InsightService.Contracts.Calculations;
using InsightService.Controllers;
using InsightService.Models.Policies;
using InsightService.Repositories;
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
    public async Task CompanyImportanceSurvivesInternalDetailSnapshotAndCanonicalProjection()
    {
        var company = Guid.NewGuid();
        var time = DateTimeOffset.Parse("2026-10-25T00:00:00Z");
        var source = new InsightService.Contracts.Context.SourceRef("synthetic-own", "v1", new string('a', 64));
        var rating = new CompanyImportance(4, "Own operational dependency needs prompt review.", [source]);
        var draft = new EffectDraft("importance", "company:" + company.ToString("D"), null, "Synthetic consequence", "unknown", [source],
            Question: new("liquidity", "event_only", "qualitative", "2026-11", null, "qualitative"), CompanyImportance: rating, Category: "risk");
        var saved = (await new EffectRepository(database.Context).Save(new(company, null, "news", "v1", time, [draft]), "synthetic-importance", [new(draft, null)])).RequireContent();
        using var factory = Factory();
        using var client = Authorized(factory);
        var effect = saved.Effects.Single();
        var detail = (await client.GetFromJsonAsync<JsonElement>($"/insight-service/internal/effects/{effect.EffectId}"));
        Assert.Equal(4, detail.GetProperty("currentRevision").GetProperty("companyImportance").GetProperty("score").GetInt32());
        Assert.Equal("risk", detail.GetProperty("currentRevision").GetProperty("category").GetString());
        Assert.Equal(rating.Rationale, detail.GetProperty("history")[0].GetProperty("companyImportance").GetProperty("rationale").GetString());
        var snapshot = await client.GetFromJsonAsync<JsonElement>($"/insight-service/internal/effects?companyId={company:D}&newsMaterialId=news");
        Assert.Equal(4, snapshot.GetProperty("effects")[0].GetProperty("currentRevision").GetProperty("companyImportance").GetProperty("score").GetInt32());
        using var projected = await client.PostAsJsonAsync("/insight-service/internal/projections/canonical-facts", new { asOf = time, companies = new[] { new { companyId = company, status = "completed", receipt = saved } } });
        Assert.Equal(HttpStatusCode.OK, projected.StatusCode);
        var facts = (await projected.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("facts");
        Assert.Equal(4, facts[0].GetProperty("revision").GetProperty("companyImportance").GetProperty("score").GetInt32());
        Assert.Equal("risk", facts[0].GetProperty("revision").GetProperty("category").GetString());
        using var invalid = await client.PostAsJsonAsync("/insight-service/internal/analyses", new AnalysisRequest(company, null, "news", "v2", time,
            [draft with { Category = "unknown", CompanyImportance = null }]));
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
    }

    [Fact]
    public async Task DomainToolsUsePersistedFactsNormalizeQuestionsAndSeparateSourceCursors()
    {
        var company = Guid.NewGuid();
        var asOf = DateTimeOffset.Parse("2026-10-24T08:00:00Z");
        var calculation = new Calculation(new ExpressionNode("value", "9007199254740993.11"), "RUB", "2026");
        var draft = new EffectDraft("debt", "contract", null, "Canonical", "forecast", [], calculation,
            new("cost", "annual_run_rate", "contract", "supported", 5, 5, 5));
        var request = new AnalysisRequest(company, null, "news", "v1", asOf, [draft], ContextComplete: true);
        var saved = (await new EffectRepository(database.Context).Save(request, "synthetic-contract", [new(draft, "9007199254740993.11")])).RequireContent();
        using var factory = Factory();
        using var client = Authorized(factory);
        var forged = saved with
        {
            Effects = [saved.Effects[0] with { CurrentRevision = saved.Effects[0].CurrentRevision with { CalculationResult = "0" } }]
        };
        using var projection = await client.PostAsJsonAsync("/insight-service/internal/projections/canonical-facts", new
        {
            asOf,
            companies = new[] { new { companyId = company, status = "completed", receipt = forged } }
        });
        Assert.Equal(HttpStatusCode.OK, projection.StatusCode);
        var facts = (await projection.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("facts");
        Assert.Equal("9007199254740993.11", facts[0].GetProperty("revision").GetProperty("calculationResult").GetString());
        Assert.Equal(100, facts[0].GetProperty("priority").GetInt32());
        var total = ProjectionPolicy.Group(new() { GroupId = "group", CompanyIds = [company], Complete = true, AggregationRule = "sum_compatible_owner_effects" },
            [new() { CompanyId = company, Status = "completed", Receipt = saved, ContextComplete = true }]).Totals.Single();
        Assert.Equal("9007199254740993.11", total.FullTotal);

        using var prepare = await client.PostAsJsonAsync("/insight-service/internal/analyses/prepare", new
        {
            request,
            drafts = new[] { draft with { Calculation = null, Impact = null, Question = new("liquidity", "event_only", "qualitative", "2026", "2025", "unknown") } }
        });
        Assert.Equal(HttpStatusCode.OK, prepare.StatusCode);
        var prepared = (await prepare.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("drafts")[0];
        Assert.StartsWith("question-", prepared.GetProperty("effectKey").GetString());
        Assert.Equal("unknown", prepared.GetProperty("question").GetProperty("targetPeriod").GetString());
        Assert.Equal("qualitative", prepared.GetProperty("question").GetProperty("basis").GetString());
        using var advanced = await client.PostAsJsonAsync("/insight-service/internal/source-cursor?source=company", new
        {
            version = 0,
            sequence = 17,
            afterQuestion = (string?)null
        });
        Assert.Equal(HttpStatusCode.OK, advanced.StatusCode);
        var contextCursor = await client.GetFromJsonAsync<JsonElement>("/insight-service/internal/source-cursor");
        var companyCursor = await client.GetFromJsonAsync<JsonElement>("/insight-service/internal/source-cursor?source=company");
        Assert.Equal(0, contextCursor.GetProperty("sequence").GetInt64());
        Assert.Equal(17, companyCursor.GetProperty("sequence").GetInt64());
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
        Assert.Equal("No completed analysis receipt exists for this news version.", body.RootElement.GetProperty("errorMessage").GetString());
        Assert.False(body.RootElement.TryGetProperty("code", out _));
        using var anonymous = factory.CreateClient();
        using var forbidden = await anonymous.GetAsync("/insight-service/internal/source-cursor");
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
        Assert.Contains("internal_access_denied", await forbidden.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task ReturnedValidationErrorUsesPackageEnvelopeAndStatus()
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
        Assert.Equal("Raw model explanation must be bounded.", json.RootElement.GetProperty("errorMessage").GetString());
        Assert.Single(json.RootElement.EnumerateObject());
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
        Assert.Equal("The expression divides by zero.", rows[1].GetProperty("errorMessage").GetString());
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
            Assert.Equal("Another consumer advanced the source cursor.", (await conflict.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("errorMessage").GetString());
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
