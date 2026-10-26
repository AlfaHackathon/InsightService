using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using InsightService.Contracts.Analyses;
using InsightService.Contracts.Context;
using InsightService.Contracts.PublicReads;
using InsightService.Contracts.Reads;
using InsightService.Controllers;
using InsightService.Extensions;
using InsightService.Middleware;
using InsightService.Services.PublicReads;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

// Tests the private service's trusted-header boundary, not JWT validation or live ingress.
public sealed class PublicInsightOwnerMvcTests
{
    private static readonly Guid User = Guid.Parse("11111111-1111-4111-8111-111111111111");
    private static readonly Guid Company = Guid.Parse("22222222-2222-4222-8222-222222222222");
    private sealed class Memberships : IPublicInsightMembershipResolver
    {
        public int Calls;
        public PublicInsightMembership? Value = new(Company, null);
        public Task<PublicInsightMembership?> GetAsync(Guid userId, CancellationToken ct) { Assert.Equal(User, userId); Calls++; return Task.FromResult(Value); }
    }
    private sealed class Sources : IPublicInsightSourceResolver
    {
        public Task<MaterialVersion?> GetAsync(string materialId, string versionId, CancellationToken ct) => throw new InvalidOperationException("No source reads expected for empty feed.");
    }
    private sealed class Store : IPublicInsightReadStore
    {
        public int Calls;
        public InsightReadOptions? Query;
        public Task<InsightReadPage<PersonalInsightRead>> PersonalPage(Guid company, InsightReadOptions options, CancellationToken ct) { Assert.Equal(Company, company); Calls++; Query = options; return Task.FromResult(new InsightReadPage<PersonalInsightRead>(company, [], null)); }
        public Task<PersonalInsightRead?> PersonalDetail(Guid company, string id, CancellationToken ct) { Calls++; return Task.FromResult<PersonalInsightRead?>(null); }
        public Task<InsightReadPage<EffectRevision>?> PersonalHistory(Guid company, string id, int limit, string? cursor, CancellationToken ct) => throw new InvalidOperationException("Missing detail should prevent history read.");
        public Task<InsightReadPage<GeneralInsightRead>> GeneralPage(Guid company, InsightReadOptions options, CancellationToken ct) { Assert.Equal(Company, company); Calls++; Query = options; return Task.FromResult(new InsightReadPage<GeneralInsightRead>(company, [], null)); }
        public Task<GeneralInsightRead?> GeneralDetail(Guid company, string id, CancellationToken ct) { Calls++; return Task.FromResult<GeneralInsightRead?>(null); }
    }
    private static async Task<WebApplication> Start(Memberships memberships, Store store)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["Internal:Secret"] = "synthetic-only", ["Swagger:Enabled"] = "true" });
        builder.Services.AddControllers().AddApplicationPart(typeof(PublicInsightReadsController).Assembly);
        builder.Services.AddPublicInsightReads();
        builder.Services.AddSingleton<IPublicInsightMembershipResolver>(memberships);
        builder.Services.AddSingleton<IPublicInsightSourceResolver, Sources>();
        builder.Services.AddSingleton<IPublicInsightReadStore>(store);
        builder.Services.AddServiceSwagger();
        var app = builder.Build();
        app.UseInsightRequestHandling();
        app.UsePublicInsightReads();
        app.UseServiceSwagger();
        app.MapControllers();
        await app.StartAsync();
        return app;
    }
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("bad-user")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    [InlineData("11111111-1111-4111-8111-111111111111,22222222-2222-4222-8222-222222222222")]
    public async Task TrustedIdentityIsRequiredBeforeValidationAndEveryRead(string? identity)
    {
        var memberships = new Memberships();
        var store = new Store();
        await using var app = await Start(memberships, store);
        using var client = app.GetTestClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", "not-authority-inside-this-private-service");
        client.DefaultRequestHeaders.Add("X-Internal-Secret", "synthetic-only");
        if (identity is not null)
        {
            client.DefaultRequestHeaders.TryAddWithoutValidation("X-User-Id", identity);
        }

        using var response = await client.GetAsync("/insight-service/insights?limit=invalid");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.True(response.Headers.CacheControl!.Private);
        Assert.True(response.Headers.CacheControl.NoStore);
        Assert.Equal(0, memberships.Calls);
        Assert.Equal(0, store.Calls);
    }
    [Fact]
    public async Task OwnedFeedPreservesEnvelopeAndFiltersWithoutCallerCompanyScope()
    {
        var memberships = new Memberships();
        var store = new Store();
        await using var app = await Start(memberships, store);
        using var client = app.GetTestClient();
        client.DefaultRequestHeaders.Add("X-User-Id", User.ToString("D"));
        using var response = await client.GetAsync("/insight-service/insights?limit=3&status=calculated&mechanism=financing_cost&minImportance=2&newsMaterialId=news-1");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(Company.ToString("D"), body.GetProperty("companyId").GetString());
        Assert.Empty(body.GetProperty("items").EnumerateArray());
        Assert.False(body.GetProperty("exhaustive").GetBoolean());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("nextCursor").ValueKind);
        Assert.Equal(3, store.Query!.Limit);
        Assert.Equal(2, store.Query.MinImportance);
        Assert.Equal("financing_cost", store.Query.Mechanism);
        Assert.True(response.Headers.CacheControl!.NoStore);
    }
    [Fact]
    public async Task OwnedFeedHeadReturnsHeadersWithoutResponseBody()
    {
        var store = new Store();
        await using var app = await Start(new(), store);
        using var client = app.GetTestClient();
        client.DefaultRequestHeaders.Add("X-User-Id", User.ToString("D"));
        using var request = new HttpRequestMessage(HttpMethod.Head, "/insight-service/insights");
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Empty(await response.Content.ReadAsByteArrayAsync());
        Assert.True(response.Headers.CacheControl!.NoStore);
        Assert.Equal(1, store.Calls);
    }
    [Theory]
    [InlineData("personal", "risk")]
    [InlineData("personal", "opportunity")]
    [InlineData("personal", "event")]
    [InlineData("general", "risk")]
    [InlineData("general", "opportunity")]
    [InlineData("general", "event")]
    public async Task PublicCategoryQueryIsAcceptedAndPreservedForBothKinds(string kind, string category)
    {
        var store = new Store();
        await using var app = await Start(new(), store);
        using var client = app.GetTestClient();
        client.DefaultRequestHeaders.Add("X-User-Id", User.ToString("D"));
        using var response = await client.GetAsync($"/insight-service/insights?kind={kind}&category={category}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(category, store.Query!.Category);
        Assert.Equal(1, store.Calls);
    }
    [Theory]
    [InlineData("?companyId=other")]
    [InlineData("?personId=other")]
    [InlineData("?asOf=2026-10-25T10:00:00Z")]
    [InlineData("?limit=1&limit=2")]
    [InlineData("?Limit=2")]
    [InlineData("?limit=0")]
    [InlineData("?limit=21")]
    [InlineData("?minImportance=0")]
    [InlineData("?kind=general&minImportance=1")]
    [InlineData("?kind=invalid")]
    [InlineData("?mechanism=invalid")]
    [InlineData("?category=")]
    [InlineData("?category=%20")]
    [InlineData("?category=null")]
    [InlineData("?category=unknown")]
    [InlineData("?category=mixed")]
    [InlineData("?category=Risk")]
    [InlineData("?category=risk&category=event")]
    [InlineData("?Category=risk")]
    [InlineData("?kind=general&category=event&status=forecast")]
    [InlineData("?status=")]
    [InlineData("?mechanism=")]
    [InlineData("?mechanism=%20")]
    [InlineData("?newsMaterialId=")]
    [InlineData("?minImportance=")]
    [InlineData("?cursor=")]
    [InlineData("?cursor=%20")]
    public async Task StrictInputsNeverBecomeEmptySuccess(string query)
    {
        var memberships = new Memberships();
        var store = new Store();
        await using var app = await Start(memberships, store);
        using var client = app.GetTestClient();
        client.DefaultRequestHeaders.Add("X-User-Id", User.ToString("D"));
        using var response = await client.GetAsync("/insight-service/insights" + query);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, store.Calls);
        Assert.True(response.Headers.CacheControl!.NoStore);
    }
    [Fact]
    public async Task LiteralWhitespaceFiltersArePreservedInsteadOfBecomingUnfiltered()
    {
        var store = new Store();
        await using var app = await Start(new(), store);
        using var client = app.GetTestClient();
        client.DefaultRequestHeaders.Add("X-User-Id", User.ToString("D"));
        using var response = await client.GetAsync("/insight-service/insights?status=%20&newsMaterialId=%20");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(" ", store.Query!.Status);
        Assert.Equal(" ", store.Query.NewsMaterialId);
    }
    [Fact]
    public async Task MissingMembershipAndHistoryAreExplicitAndInternalCredentialGuardRemains()
    {
        var memberships = new Memberships { Value = null };
        var store = new Store();
        await using var app = await Start(memberships, store);
        using var client = app.GetTestClient();
        client.DefaultRequestHeaders.Add("X-User-Id", User.ToString("D"));
        using var denied = await client.GetAsync("/insight-service/insights");
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        Assert.Equal(0, store.Calls);
        memberships.Value = new(Company, null);
        using var history = await client.GetAsync("/insight-service/insights/personal/missing/history");
        Assert.Equal(HttpStatusCode.NotFound, history.StatusCode);
        using var internalDenied = await client.GetAsync("/insight-service/internal/read/companies/" + Company + "/personal");
        Assert.Equal(HttpStatusCode.Forbidden, internalDenied.StatusCode);
    }
    [Fact]
    public async Task OpenApiSeparatesPublicBearerFromInternalSecretAndShowsPublicCardUnion()
    {
        await using var app = await Start(new(), new());
        using var client = app.GetTestClient();
        var spec = await client.GetFromJsonAsync<JsonElement>("/insight-service/swagger/v1/swagger.json");
        var operation = spec.GetProperty("paths").GetProperty("/insight-service/insights").GetProperty("get");
        Assert.True(operation.GetProperty("security")[0].TryGetProperty("GatewayBearer", out _));
        Assert.False(operation.GetProperty("security")[0].TryGetProperty("InternalSecret", out _));
        var filter = Assert.Single(operation.GetProperty("parameters").EnumerateArray(), parameter => parameter.GetProperty("name").GetString()!.Equals("category", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(["risk", "opportunity", "event"], filter.GetProperty("schema").GetProperty("enum").EnumerateArray().Select(value => value.GetString()!).ToArray());
        var schemas = spec.GetProperty("components").GetProperty("schemas");
        Assert.Equal(2, schemas.GetProperty("PublicInsightCard").GetProperty("oneOf").GetArrayLength());
        foreach (var pair in new[] { ("PublicPersonalInsightCard", "personal"), ("PublicGeneralInsightCard", "general") })
        {
            var schema = schemas.GetProperty(pair.Item1);
            Assert.Contains(schema.GetProperty("required").EnumerateArray(), value => value.GetString() == "kind");
            Assert.Equal(pair.Item2, Assert.Single(schema.GetProperty("properties").GetProperty("kind").GetProperty("enum").EnumerateArray()).GetString());
        }
        foreach (var type in new[] { "PublicInsightRevision", "PublicGeneralInsight", "EffectRevision" })
        {
            var category = schemas.GetProperty(type).GetProperty("properties").GetProperty("category");
            Assert.True(category.GetProperty("nullable").GetBoolean());
            Assert.Equal(["risk", "opportunity", "event"], category.GetProperty("enum").EnumerateArray().Select(value => value.GetString()!).ToArray());
        }
        Assert.False(spec.GetProperty("paths").TryGetProperty("/agent-service/insights", out _));
        var claim = schemas.GetProperty("CalculationClaim");
        Assert.Contains("never rewritten", claim.GetProperty("description").GetString());
        Assert.Equal(200, claim.GetProperty("properties").GetProperty("quote").GetProperty("maxLength").GetInt32());
        Assert.Equal(4, claim.GetProperty("required").GetArrayLength());
        Assert.True(schemas.GetProperty("EffectRevision").GetProperty("properties").TryGetProperty("calculationClaim", out _));
        Assert.False(schemas.GetProperty("PublicInsightRevision").GetProperty("properties").TryGetProperty("calculationClaim", out _));
    }
}
