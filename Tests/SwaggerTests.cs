using System.Net;
using System.Text.Json;
using InsightService.Controllers;
using InsightService.Extensions;
using InsightService.Middleware;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

public sealed class SwaggerTests
{
    private static async Task<WebApplication> Start(bool enabled)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Production" });
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Swagger:Enabled"] = enabled.ToString(),
            ["Internal:Secret"] = "synthetic-docs-test-only"
        });
        builder.Services.AddControllers().AddApplicationPart(typeof(AnalysesController).Assembly);
        builder.Services.AddServiceSwagger();
        var app = builder.Build();
        app.UseInsightRequestHandling();
        app.UseServiceSwagger();
        app.MapControllers();
        await app.StartAsync();
        return app;
    }

    [Fact]
    public async Task ProductionDocsAreOptInAndServeLocalUiAndGeneratedInternalContracts()
    {
        await using var app = await Start(true);
        using var client = app.GetTestClient();
        using var response = await client.GetAsync("/insight-service/swagger/v1/swagger.json");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = document.RootElement;
        var operation = root.GetProperty("paths").GetProperty("/insight-service/internal/effects").GetProperty("get");
        Assert.True(operation.GetProperty("security")[0].TryGetProperty("InternalSecret", out _));
        var security = root.GetProperty("components").GetProperty("securitySchemes").GetProperty("InternalSecret");
        Assert.Equal("apiKey", security.GetProperty("type").GetString());
        Assert.Equal("X-Internal-Secret", security.GetProperty("name").GetString());
        Assert.DoesNotContain("synthetic-docs-test-only", root.GetRawText(), StringComparison.Ordinal);
        Assert.True(operation.GetProperty("responses").GetProperty("200").TryGetProperty("content", out _));
        using var ui = await client.GetAsync("/insight-service/swagger/index.html");
        Assert.Equal(HttpStatusCode.OK, ui.StatusCode);
        using var asset = await client.GetAsync("/insight-service/swagger/swagger-ui-bundle.js");
        Assert.Equal(HttpStatusCode.OK, asset.StatusCode);
        client.DefaultRequestHeaders.Authorization = new("Bearer", "synthetic-user-token");
        using var denied = await client.SendAsync(new HttpRequestMessage(HttpMethod.Get, "/insight-service/internal/effects"));
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
    }

    [Fact]
    public async Task DisabledDocsDoNotMapSpecOrUi()
    {
        await using var app = await Start(false);
        using var client = app.GetTestClient();
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/insight-service/swagger/v1/swagger.json")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/insight-service/swagger/index.html")).StatusCode);
    }
}
