using System.Net;
using System.Text;
using System.Text.Json;
using InsightService.Contracts.Context;
using InsightService.Services.PublicReads;
using InsightService.Services.PublicReadTransport;
using Microsoft.Extensions.Configuration;
using Xunit;

public sealed class PublicInsightHttpClientTests
{
    [Fact]
    public async Task MembershipUsesOperationalOwnerAndOnlyServerCredential()
    {
        var user = Guid.NewGuid();
        var company = Guid.NewGuid();
        var handler = new Handler(request =>
        {
            Assert.Equal($"http://company.fixture/company-service/internal/companies/by-user/{user:D}", request.RequestUri!.AbsoluteUri);
            Assert.Equal("synthetic-only", Assert.Single(request.Headers.GetValues("X-Internal-Secret")));
            Assert.Null(request.Headers.Authorization);
            return new(HttpStatusCode.OK) { Content = new StringContent($"{{\"id\":\"{company:D}\",\"name\":\"Synthetic\",\"bankProfileId\":\"U_96\"}}", Encoding.UTF8, "application/json") };
        });
        var membership = await Client(handler).Get(user, default);
        Assert.Equal(company, membership!.CompanyId);
        Assert.Equal("U_96", membership.BankProfileId);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task ExactSourceRetainsNullableProvenanceWithoutClaimingCompleteness()
    {
        var handler = new Handler(request =>
        {
            Assert.Equal("http://context.fixture/context-service/internal/material-versions/source/v1", request.RequestUri!.AbsoluteUri);
            return new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new MaterialVersion("source", "v1", "synthetic", "company", "Own", "Original", DateTimeOffset.UtcNow, null, null, [Guid.NewGuid()], [], new string('a', 64))), Encoding.UTF8, "application/json") };
        });
        var source = await Client(handler).Get("source", "v1", default);
        Assert.Equal("Original", source!.Text);
        Assert.Null(source.Provenance);
    }

    [Fact]
    public async Task ExactSourceProvenanceSurvivesDeserializationAtFullTickPrecision()
    {
        var time = DateTimeOffset.Parse("2026-10-25T10:00:00.0000001Z");
        var provenance = new MaterialProvenance("uploaded", "https://example.invalid/source", time, "excerpt", null, "page 2");
        var handler = new Handler(_ => new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new MaterialVersion("source", "v1", "synthetic", "company", "Own", "Original", time, null, null, [Guid.NewGuid()], [], new string('a', 64), provenance)), Encoding.UTF8, "application/json") });
        var source = await Client(handler).Get("source", "v1", default);
        Assert.Equal(provenance, source!.Provenance);
        Assert.Equal(time.UtcTicks, source.KnownAt.UtcTicks);
    }

    [Theory]
    [InlineData(401)]
    [InlineData(403)]
    [InlineData(302)]
    [InlineData(500)]
    public async Task UpstreamFailureIsSanitizedAndNeverEmptySuccess(int code)
    {
        var handler = new Handler(_ => new((HttpStatusCode)code) { Content = new StringContent("PRIVATE RAW BODY") });
        var error = await Assert.ThrowsAsync<PublicInsightReadException>(() => Client(handler).Get(Guid.NewGuid(), default));
        Assert.Equal(503, error.StatusCode);
        Assert.DoesNotContain("PRIVATE", error.Message);
    }

    [Fact]
    public async Task MissingMembershipAndMissingExactSourceRemainExplicitNullForUseCasePolicy()
    {
        var handler = new Handler(_ => new(HttpStatusCode.NotFound));
        Assert.Null(await Client(handler).Get(Guid.NewGuid(), default));
        Assert.Null(await Client(handler).Get("source", "v1", default));
    }

    [Theory]
    [InlineData("file:///private")]
    [InlineData("http://user:password@company.fixture")]
    [InlineData("http://company.fixture?private=true")]
    public async Task UnsafeConfiguredAddressIsRejectedBeforeHttp(string root)
    {
        var handler = new Handler(_ => throw new InvalidOperationException("No HTTP expected"));
        await Assert.ThrowsAsync<PublicInsightReadException>(() => Client(handler, root).Get(Guid.NewGuid(), default));
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task OversizedResponseAndMalformedMembershipAreNotAccepted()
    {
        var huge = new Handler(_ => new(HttpStatusCode.OK) { Content = new StringContent(new string('x', 4 * 1024 * 1024 + 1)) });
        await Assert.ThrowsAsync<PublicInsightReadException>(() => Client(huge).Get(Guid.NewGuid(), default));
        var invalid = new Handler(_ => new(HttpStatusCode.OK) { Content = new StringContent("{\"id\":\"00000000-0000-0000-0000-000000000000\",\"name\":\"invalid\"}", Encoding.UTF8, "application/json") });
        await Assert.ThrowsAsync<PublicInsightReadException>(() => Client(invalid).Get(Guid.NewGuid(), default));
    }

    private static PublicInsightHttpClients Client(Handler handler, string company = "http://company.fixture") => new(new Factory(handler),
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["CompanyService:BaseUrl"] = company, ["ContextService:BaseUrl"] = "http://context.fixture", ["Internal:Secret"] = "synthetic-only" }).Build());
    private sealed class Factory(Handler handler) : IHttpClientFactory { public HttpClient CreateClient(string name) => new(handler, false); }
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        public int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) { Calls++; return Task.FromResult(response(request)); }
    }
}
