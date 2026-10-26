using System.Globalization;
using System.Net;
using System.Net.WebSockets;
using System.Text.Json;
using InsightService.Controllers;
using InsightService.Middleware;
using InsightService.Services.PublicReads;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

// In-process private-service transport tests with fakes; no JWT, database or ingress claim.
public sealed class InsightUpdateTransportTests
{
    private static async Task<WebApplication> Start(PublicInsightReadFixture fixture)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Services.AddControllers().AddApplicationPart(typeof(InsightUpdatesController).Assembly);
        builder.Services.AddSingleton<IPublicInsightMembershipResolver>(fixture);
        builder.Services.AddSingleton<IPublicInsightSourceResolver>(fixture);
        builder.Services.AddSingleton<IPublicInsightReadStore>(fixture);
        builder.Services.AddSingleton<InsightConnectionTickets>();
        builder.Services.AddScoped<PublicInsightReadService>();
        builder.Services.AddScoped<InsightUpdateSnapshot>();
        builder.Services.AddScoped<InsightUpdateSocket>();
        builder.Services.AddSingleton(new InsightSocketLimits
        {
            PollInterval = TimeSpan.FromMilliseconds(20), HeartbeatInterval = TimeSpan.FromMilliseconds(30)
        });
        var app = builder.Build();
        app.UseWebSockets();
        app.UsePublicInsightReads();
        app.MapControllers();
        await app.StartAsync();
        return app;
    }

    [Theory]
    [InlineData(null, null, 401)]
    [InlineData("spoof", "9999999999", 401)]
    [InlineData("11111111-1111-4111-8111-111111111111", null, 401)]
    [InlineData("11111111-1111-4111-8111-111111111111", "9999999999", 401)]
    [InlineData("11111111-1111-4111-8111-111111111111", "1", 401)]
    public async Task TrustedIdentityAndBoundedExpiryAreRequiredBeforeAnyReads(string? user, string? expiry, int status)
    {
        var fixture = new PublicInsightReadFixture { NextCursor = null };
        await using var app = await Start(fixture);
        using var client = app.GetTestClient();
        if (user is not null) { client.DefaultRequestHeaders.Add("X-User-Id", user); }
        if (expiry is not null) { client.DefaultRequestHeaders.Add(InsightUpdatesController.ExpiryHeader, expiry); }
        using var response = await client.PostAsync(InsightUpdatesController.TicketPath, null);
        Assert.Equal((HttpStatusCode)status, response.StatusCode);
        Assert.True(response.Headers.CacheControl!.NoStore);
        Assert.Equal(0, fixture.MembershipCalls);
        Assert.Equal(0, fixture.StoreCalls);
    }

    [Fact]
    public async Task NonUpgradeAndClientCompanyQueryAreRejectedBeforeAnyReads()
    {
        var fixture = new PublicInsightReadFixture { NextCursor = null };
        await using var app = await Start(fixture);
        using var client = app.GetTestClient();
        client.DefaultRequestHeaders.Add("X-User-Id", PublicInsightReadFixture.User.ToString("D"));
        client.DefaultRequestHeaders.Add(InsightUpdatesController.ExpiryHeader, DateTimeOffset.UtcNow.AddMinutes(1).ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture));
        using var response = await client.GetAsync(InsightUpdatesController.Path + "?companyId=other");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, fixture.StoreCalls);
    }

    [Fact]
    public async Task RealWebSocketUpgradeDeliversAuthorizedCommittedRevisionAndCloses()
    {
        var fixture = new PublicInsightReadFixture { NextCursor = null };
        await using var app = await Start(fixture);
        using var http = app.GetTestClient();
        http.DefaultRequestHeaders.Add("X-User-Id", PublicInsightReadFixture.User.ToString("D"));
        http.DefaultRequestHeaders.Add(InsightUpdatesController.ExpiryHeader, DateTimeOffset.UtcNow.AddMinutes(1).ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture));
        using var issued = await http.PostAsync(InsightUpdatesController.TicketPath, null);
        Assert.Equal(HttpStatusCode.OK, issued.StatusCode);
        Assert.True(issued.Headers.CacheControl!.NoStore);
        using var body = JsonDocument.Parse(await issued.Content.ReadAsStringAsync());
        var ticket = body.RootElement.GetProperty("ticket").GetString()!;
        var client = app.GetTestServer().CreateWebSocketClient();
        client.SubProtocols.Add(InsightConnectionTickets.Protocol);
        client.SubProtocols.Add(InsightConnectionTickets.TicketPrefix + ticket);
        client.ConfigureRequest = request =>
        {
            request.Headers["X-User-Id"] = PublicInsightReadFixture.Foreign.ToString("D"); // ignored: ticket owns identity
            request.Headers.Origin = "https://localhost";
        };
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var socket = await client.ConnectAsync(new Uri("ws://localhost" + InsightUpdatesController.Path), timeout.Token);
        Assert.Equal(InsightConnectionTickets.Protocol, socket.SubProtocol);
        Assert.Equal("ready", (await Receive(socket, timeout.Token)).GetProperty("eventType").GetString());
        fixture.Personal = fixture.Personal! with { CurrentRevision = fixture.Personal.CurrentRevision with { RevisionId = "committed-revision-3" } };
        JsonElement message;
        do
        {
            message = await Receive(socket, timeout.Token);
            if (message.GetProperty("eventType").GetString() == "heartbeat")
            {
                await socket.SendAsync("{\"eventType\":\"pong\"}"u8.ToArray().AsMemory(), WebSocketMessageType.Text, true, timeout.Token);
            }
        } while (message.GetProperty("eventType").GetString() != "insight.upsert");
        Assert.Equal("committed-revision-3", message.GetProperty("revisionId").GetString());
        Assert.Equal(fixture.Personal.Id, message.GetProperty("id").GetString());
        Assert.False(message.TryGetProperty("companyId", out _));
        await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "done", timeout.Token);
    }

    [Fact]
    public async Task TicketReplayAndSpoofedIdentityCannotAuthorizeASecondUpgrade()
    {
        var fixture = new PublicInsightReadFixture { NextCursor = null };
        await using var app = await Start(fixture);
        var tickets = app.Services.GetRequiredService<InsightConnectionTickets>();
        var issued = tickets.Issue(PublicInsightReadFixture.User, new(PublicInsightReadFixture.Company, null), DateTimeOffset.UtcNow.AddMinutes(1));
        Assert.NotNull(tickets.Consume(issued.Ticket));
        var client = app.GetTestServer().CreateWebSocketClient();
        client.SubProtocols.Add(InsightConnectionTickets.Protocol);
        client.SubProtocols.Add(InsightConnectionTickets.TicketPrefix + issued.Ticket);
        var status = 0;
        client.ConfigureRequest = request =>
        {
            request.Headers["X-User-Id"] = PublicInsightReadFixture.User.ToString("D");
            request.Headers[InsightUpdatesController.ExpiryHeader] = DateTimeOffset.UtcNow.AddMinutes(1).ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
            request.HttpContext.Response.OnStarting(() => { status = request.HttpContext.Response.StatusCode; return Task.CompletedTask; });
        };
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.ConnectAsync(new Uri("ws://localhost" + InsightUpdatesController.Path), timeout.Token));
        Assert.Equal(401, status);
        Assert.Equal(0, fixture.StoreCalls);
        Assert.Equal(0, fixture.MembershipCalls);
    }

    private static async Task<JsonElement> Receive(WebSocket socket, CancellationToken ct)
    {
        var buffer = new byte[4096];
        var frame = await socket.ReceiveAsync(buffer.AsMemory(), ct);
        Assert.True(frame.EndOfMessage);
        Assert.Equal(WebSocketMessageType.Text, frame.MessageType);
        using var document = JsonDocument.Parse(buffer.AsMemory(0, frame.Count));
        return document.RootElement.Clone();
    }
}
