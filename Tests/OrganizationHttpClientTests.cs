using System.Net;
using System.Text;
using System.Text.Json;
using AlfaHackaton.HttpClient.Http;
using InsightService.Contracts.Context;
using InsightService.Models;
using InsightService.Services;
using InsightService.Settings;
using Microsoft.Extensions.Configuration;
using Xunit;

public sealed class OrganizationHttpClientTests
{
    private static IConfiguration Configuration() => new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
    {
        ["ContextService:BaseUrl"] = "https://synthetic.invalid",
        ["ContextService:InternalSecret"] = "synthetic-test-credential"
    }).Build();

    [Fact]
    public async Task RealOrganizationResponseResultPreservesMaterialIdentityTicksAndHeaderScope()
    {
        var at = DateTimeOffset.Parse("2026-10-23T08:01:02.1234567+03:00");
        var material = new MaterialVersion("news", "v1", "source", "news", "title", "source text", at, null, null, [], [], new string('a', 64));
        using var transport = new System.Net.Http.HttpClient(new Handler((request, _) =>
        {
            Assert.Equal("https://synthetic.invalid/context-service/internal/material-versions/news/v1", request.RequestUri!.AbsoluteUri);
            Assert.Equal("synthetic-test-credential", Assert.Single(request.Headers.GetValues("X-Internal-Secret")));
            Assert.Null(request.Headers.Authorization);
            Assert.False(request.Headers.Contains("X-User-Id"));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(material), Encoding.UTF8, "application/json")
            });
        }));
        var settings = new ContextHttpSettings();
        var client = new HttpClient<ContextHttpSettings>(transport, settings);
        var resolver = new ContextSourceResolver(client, Configuration());
        var resolved = await resolver.GetAsync("news", "v1", CancellationToken.None);
        Assert.Equal(material, resolved with
        {
            CompanyIds = material.CompanyIds,
            BankProfileIds = material.BankProfileIds
        });
        Assert.Equal(at.UtcTicks, resolved.KnownAt.UtcTicks);
        Assert.False(settings.AutoRedirect);
        Assert.Equal(TimeSpan.FromSeconds(15), settings.Timeout);
    }

    [Theory]
    [InlineData(404, 422, "unknown_source")]
    [InlineData(403, 502, "context_rejected")]
    [InlineData(302, 502, "context_rejected")]
    [InlineData(503, 502, "context_rejected")]
    public async Task OrganizationClientPreservesUpstreamStatusMapping(int status, int expected, string code)
    {
        using var transport = new System.Net.Http.HttpClient(new Handler((_, _) => Task.FromResult(new HttpResponseMessage((HttpStatusCode)status)
        {
            Content = new StringContent("{\"upstream\":\"failure\"}")
        })));
        var resolver = new ContextSourceResolver(new HttpClient<ContextHttpSettings>(transport, new()), Configuration());
        var error = await Assert.ThrowsAsync<ApiException>(() => resolver.GetAsync("news", "v1", CancellationToken.None));
        Assert.Equal(expected, error.StatusCode);
        Assert.Equal(code, error.Code);
    }

    [Fact]
    public async Task OrganizationClientTimeoutAndInvalidPayloadRemainExplicitFailures()
    {
        using var slow = new System.Net.Http.HttpClient(new Handler(async (_, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }))
        {
            Timeout = TimeSpan.FromMilliseconds(30)
        };
        var resolver = new ContextSourceResolver(new HttpClient<ContextHttpSettings>(slow, new()), Configuration());
        Assert.Equal("context_timeout", (await Assert.ThrowsAsync<ApiException>(() => resolver.GetAsync("news", "v1", CancellationToken.None))).Code);
        using var invalid = new System.Net.Http.HttpClient(new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{")
        })));
        resolver = new(new HttpClient<ContextHttpSettings>(invalid, new()), Configuration());
        Assert.Equal("invalid_context_response", (await Assert.ThrowsAsync<ApiException>(() => resolver.GetAsync("news", "v1", CancellationToken.None))).Code);
    }

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken);
    }
}
