using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using InsightService.Contracts.PublicReads;
using InsightService.Services.PublicReads;
using Xunit;

public sealed class InsightUpdateTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static InsightUpdateSnapshot Snapshot(PublicInsightReadFixture fixture) => new(fixture, fixture, fixture.Service());
    private static readonly PublicInsightMembership Scope = new(PublicInsightReadFixture.Company, null);
    private static InsightSocketLimits FastLimits => new()
    {
        PollInterval = TimeSpan.FromMilliseconds(15), HeartbeatInterval = TimeSpan.FromMilliseconds(15),
        PongTimeout = TimeSpan.FromMilliseconds(150), SendTimeout = TimeSpan.FromMilliseconds(100),
        ReadTimeout = TimeSpan.FromSeconds(2), QueueCapacity = 2
    };

    [Fact]
    public async Task CommittedHeadsProduceOnlyIdentifiersAfterPublicDetailAuthorization()
    {
        var fixture = new PublicInsightReadFixture { NextCursor = null };
        var snapshots = Snapshot(fixture);
        var heads = await snapshots.Read(PublicInsightReadFixture.User, Scope, default);
        Assert.Equal(2, heads.Count);
        Assert.Equal(["general", "personal"], heads.Select(head => head.Kind).ToArray());
        foreach (var head in heads)
        {
            var update = await snapshots.Authorize(PublicInsightReadFixture.User, Scope, head, default);
            Assert.NotNull(update);
            Assert.Equal("insight.upsert", update.EventType);
            Assert.Equal(head.Id, update.Id);
            Assert.Equal(head.Version, update.Version);
            var wire = JsonSerializer.Serialize(update, Json);
            Assert.DoesNotContain("PRIVATE", wire);
            Assert.DoesNotContain("companyId", wire);
            Assert.DoesNotContain("conclusion", wire);
            Assert.Equal(6, JsonDocument.Parse(wire).RootElement.EnumerateObject().Count());
        }
    }

    [Theory]
    [InlineData("no_insight", "forecast")]
    [InlineData("internal_failed", "forecast")]
    [InlineData("missing_data", "forecast")]
    [InlineData("scenario", "internal_failed")]
    [InlineData("scenario", "failed")]
    [InlineData("scenario", "pending")]
    public async Task NonPublishedPersonalAndFailedGeneralNeverBecomeNewCards(string publication, string status)
    {
        var fixture = new PublicInsightReadFixture { NextCursor = null };
        fixture.Personal = fixture.Personal! with { CurrentRevision = fixture.Personal.CurrentRevision with { PublicationClass = publication, Status = status } };
        fixture.General = fixture.General! with { CompanyStatus = "failed" };
        Assert.Empty(await Snapshot(fixture).Read(PublicInsightReadFixture.User, Scope, default));
    }

    [Fact]
    public async Task LegacyUnknownEvidenceIsDiagnosticEvenBeforeNarrativeReadIntegration()
    {
        var fixture = new PublicInsightReadFixture { NextCursor = null, General = null };
        fixture.Personal = fixture.Personal! with { CurrentRevision = fixture.Personal.CurrentRevision with
        { Impact = fixture.Personal.CurrentRevision.Impact! with { EvidenceStatus = "unknown" } } };
        Assert.Empty(await Snapshot(fixture).Read(PublicInsightReadFixture.User, Scope, default));
        fixture.Personal = fixture.Personal with { CurrentRevision = fixture.Personal.CurrentRevision with { Impact = null, Status = "unknown" } };
        Assert.Empty(await Snapshot(fixture).Read(PublicInsightReadFixture.User, Scope, default));
    }

    [Fact]
    public async Task FullQueueDisconnectsInsteadOfAccumulatingAnUnboundedBacklog()
    {
        var fixture = new PublicInsightReadFixture { NextCursor = null };
        var snapshots = Snapshot(fixture);
        using var socket = new FakeSocket { SlowSend = true };
        var limits = FastLimits with { QueueCapacity = 1, SendTimeout = TimeSpan.FromSeconds(2) };
        await new InsightUpdateSocket(snapshots, limits).Run(socket, PublicInsightReadFixture.User, Scope,
            DateTimeOffset.UtcNow.AddSeconds(5), [], default).WaitAsync(TimeSpan.FromSeconds(3));
        // Ready is in flight; the two newly visible authorized cards exceed one queued slot.
        Assert.Equal("slow_client_refetch", socket.Reason);
        Assert.Equal((WebSocketCloseStatus)1013, socket.OutputStatus);
    }

    [Fact]
    public async Task DeniedSourceAndConcurrentRevisionAreNotAnnounced()
    {
        var fixture = new PublicInsightReadFixture { NextCursor = null };
        var snapshots = Snapshot(fixture);
        var head = Assert.Single(await snapshots.Read(PublicInsightReadFixture.User, Scope, default), item => item.Kind == "personal");
        fixture.Materials[("own", "v1")] = PublicInsightReadFixture.Material("own", companies: [PublicInsightReadFixture.Foreign]);
        Assert.Null(await snapshots.Authorize(PublicInsightReadFixture.User, Scope, head, default));
        fixture.Materials[("own", "v1")] = PublicInsightReadFixture.Material("own");
        fixture.Personal = fixture.Personal! with { CurrentRevision = fixture.Personal.CurrentRevision with { RevisionId = "revision-3" } };
        Assert.Null(await snapshots.Authorize(PublicInsightReadFixture.User, Scope, head, default));
    }

    [Fact]
    public async Task MembershipChangeForeignRowsAndUnboundedPagingFailClosed()
    {
        var fixture = new PublicInsightReadFixture { NextCursor = null };
        var snapshots = Snapshot(fixture);
        fixture.Membership = new(PublicInsightReadFixture.Foreign, null);
        var changed = await Assert.ThrowsAsync<PublicInsightReadException>(() => snapshots.Read(PublicInsightReadFixture.User, Scope, default));
        Assert.Equal(403, changed.StatusCode);
        fixture.Membership = Scope;
        fixture.Personal = fixture.Personal! with { CompanyId = PublicInsightReadFixture.Foreign };
        Assert.Equal(502, (await Assert.ThrowsAsync<PublicInsightReadException>(() => snapshots.Read(PublicInsightReadFixture.User, Scope, default))).StatusCode);
        fixture.Personal = null;
        fixture.NextCursor = "repeating-cursor";
        Assert.Equal("INSIGHT_UPDATE_RESYNC_REQUIRED", (await Assert.ThrowsAsync<PublicInsightReadException>(() => snapshots.Read(PublicInsightReadFixture.User, Scope, default))).Code);
    }

    [Fact]
    public async Task SocketDedupesUpsertAndSendsHeartbeatUntilExpiry()
    {
        var fixture = new PublicInsightReadFixture { NextCursor = null };
        var snapshots = Snapshot(fixture);
        var baseline = await snapshots.Read(PublicInsightReadFixture.User, Scope, default);
        using var socket = new FakeSocket();
        var run = new InsightUpdateSocket(snapshots, FastLimits).Run(socket, PublicInsightReadFixture.User, Scope,
            DateTimeOffset.UtcNow.AddMilliseconds(250), baseline, default);
        Assert.Equal("ready", (await socket.Sent.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2))).GetProperty("eventType").GetString());
        fixture.Personal = fixture.Personal! with { CurrentRevision = fixture.Personal.CurrentRevision with { RevisionId = "revision-3" } };
        await run.WaitAsync(TimeSpan.FromSeconds(3));
        var messages = Drain(socket);
        Assert.Single(messages, message => message.GetProperty("eventType").GetString() == "insight.upsert");
        Assert.Contains(messages, message => message.GetProperty("eventType").GetString() == "heartbeat");
        Assert.Equal("reconnect_and_refetch", socket.Reason);
    }

    [Fact]
    public async Task MissingPongAndSlowSenderAreDisconnectedAndCancellationCompletes()
    {
        var fixture = new PublicInsightReadFixture { NextCursor = null };
        var snapshots = Snapshot(fixture);
        var baseline = await snapshots.Read(PublicInsightReadFixture.User, Scope, default);
        using var silent = new FakeSocket { AutoPong = false };
        await new InsightUpdateSocket(snapshots, FastLimits).Run(silent, PublicInsightReadFixture.User, Scope,
            DateTimeOffset.UtcNow.AddSeconds(2), baseline, default).WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal("pong_timeout", silent.Reason);
        using var slow = new FakeSocket { SlowSend = true };
        await new InsightUpdateSocket(snapshots, FastLimits).Run(slow, PublicInsightReadFixture.User, Scope,
            DateTimeOffset.UtcNow.AddSeconds(2), baseline, default).WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal("unavailable_refetch", slow.Reason);
        using var canceled = new FakeSocket();
        using var stop = new CancellationTokenSource(TimeSpan.FromMilliseconds(30));
        await new InsightUpdateSocket(snapshots, FastLimits).Run(canceled, PublicInsightReadFixture.User, Scope,
            DateTimeOffset.UtcNow.AddSeconds(5), baseline, stop.Token).WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(WebSocketState.CloseSent, canceled.State);
    }

    [Fact]
    public async Task MembershipRevocationStopsAnOpenSocket()
    {
        var fixture = new PublicInsightReadFixture { NextCursor = null };
        var snapshots = Snapshot(fixture);
        using var socket = new FakeSocket();
        var run = new InsightUpdateSocket(snapshots, FastLimits).Run(socket, PublicInsightReadFixture.User, Scope,
            DateTimeOffset.UtcNow.AddSeconds(5), [], default);
        await socket.Sent.Reader.ReadAsync();
        fixture.Membership = null;
        await run.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(WebSocketCloseStatus.PolicyViolation, socket.OutputStatus);
    }

    private static List<JsonElement> Drain(FakeSocket socket)
    {
        var messages = new List<JsonElement>();
        while (socket.Sent.Reader.TryRead(out var message)) { messages.Add(message); }
        return messages;
    }

    private sealed class FakeSocket : WebSocket
    {
        public Channel<JsonElement> Sent { get; } = Channel.CreateUnbounded<JsonElement>();
        private readonly Channel<byte[]> received = Channel.CreateUnbounded<byte[]>();
        public bool AutoPong { get; init; } = true;
        public bool SlowSend { get; init; }
        public string? Reason { get; private set; }
        public WebSocketCloseStatus? OutputStatus { get; private set; }
        private WebSocketState state = WebSocketState.Open;
        public override WebSocketState State => state;
        public override WebSocketCloseStatus? CloseStatus => OutputStatus;
        public override string? CloseStatusDescription => Reason;
        public override string? SubProtocol => null;
        public override void Abort() => state = WebSocketState.Aborted;
        public override void Dispose() { state = WebSocketState.Closed; }
        public override Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken) =>
            CloseOutputAsync(closeStatus, statusDescription, cancellationToken);
        public override Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
        { OutputStatus = closeStatus; Reason = statusDescription; state = WebSocketState.CloseSent; received.Writer.TryWrite([]); return Task.CompletedTask; }
        public override async Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken)
        {
            var bytes = await received.Reader.ReadAsync(cancellationToken);
            if (bytes.Length == 0) { return new(0, WebSocketMessageType.Close, true); }
            bytes.CopyTo(buffer.AsSpan());
            return new(bytes.Length, WebSocketMessageType.Text, true);
        }
        public override async Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken)
        {
            if (SlowSend) { await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken); }
            var message = JsonDocument.Parse(buffer.AsMemory()).RootElement.Clone();
            await Sent.Writer.WriteAsync(message, cancellationToken);
            if (AutoPong && message.GetProperty("eventType").GetString() == "heartbeat")
            { await received.Writer.WriteAsync(Encoding.UTF8.GetBytes("{\"eventType\":\"pong\"}"), cancellationToken); }
        }
    }
}
