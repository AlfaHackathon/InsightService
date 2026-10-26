using System.Text.Json;
using InsightService.Contracts.PublicReads;
using InsightService.Services.PublicReads;
using Xunit;

// Combined owner projection + WebSocket contract. No PgTestDatabase or LLM.
public sealed class InsightWebSocketPublicationIntegrationTests
{
    private static readonly PublicInsightMembership Scope = new(PublicInsightReadFixture.Company, null);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [Theory]
    [InlineData("missing_data")]
    [InlineData("unknown_evidence")]
    [InlineData("legacy_unknown")]
    public async Task DiagnosticsHiddenByPublicProjectionCannotBecomeSocketEvents(string shape)
    {
        var fixture = new PublicInsightReadFixture { NextCursor = null, General = null };
        var reads = fixture.Service();
        var snapshots = new InsightUpdateSnapshot(fixture, fixture, reads);
        var previous = Assert.Single(await snapshots.Read(PublicInsightReadFixture.User, Scope, default));
        var revision = fixture.Personal!.CurrentRevision;
        revision = shape switch
        {
            "missing_data" => revision with { PublicationClass = "missing_data" },
            "unknown_evidence" => revision with { Impact = revision.Impact! with { EvidenceStatus = "unknown" } },
            _ => revision with { Impact = null, Status = "unknown" }
        };
        fixture.Personal = fixture.Personal with { CurrentRevision = revision };

        Assert.Empty((await reads.Feed(PublicInsightReadFixture.User, new(), default)).Items);
        var error = await Assert.ThrowsAsync<PublicInsightReadException>(() => reads.Detail(PublicInsightReadFixture.User, "personal", previous.Id, default));
        Assert.Equal(404, error.StatusCode);
        Assert.Empty(await snapshots.Read(PublicInsightReadFixture.User, Scope, default));
        // Even a candidate captured before the visibility change is rechecked by Detail.
        Assert.Null(await snapshots.Authorize(PublicInsightReadFixture.User, Scope, previous, default));
        Assert.Equal(0, fixture.SourceCalls);
    }

    [Fact]
    public async Task EventRevisionIdentifiesTheSameVersionedTitleAsFeedDetailAndHistory()
    {
        var fixture = new PublicInsightReadFixture { NextCursor = null, General = null };
        var reads = fixture.Service();
        var snapshots = new InsightUpdateSnapshot(fixture, fixture, reads);
        var previous = Assert.Single(await snapshots.Read(PublicInsightReadFixture.User, Scope, default));
        var published = fixture.Personal!.CurrentRevision with
        {
            RevisionId = "published-revision-3", PreviousRevisionId = previous.RevisionId,
            Title = "Пересмотр условий финансирования", Conclusion = "Обоснованный вывод сохранён без переписывания."
        };
        fixture.Personal = fixture.Personal with { CurrentRevision = published };

        var head = Assert.Single(await snapshots.Read(PublicInsightReadFixture.User, Scope, default));
        Assert.NotEqual(previous.Version, head.Version);
        var update = await snapshots.Authorize(PublicInsightReadFixture.User, Scope, head, default);
        Assert.NotNull(update);
        var detail = Assert.IsType<PublicPersonalInsightCard>(await reads.Detail(PublicInsightReadFixture.User, "personal", head.Id, default));
        var feed = Assert.IsType<PublicPersonalInsightCard>(Assert.Single((await reads.Feed(PublicInsightReadFixture.User, new(), default)).Items));
        var history = Assert.Single((await reads.History(PublicInsightReadFixture.User, head.Id, new(), default)).Items);
        Assert.Equal(detail.CurrentRevision.RevisionId, update.RevisionId);
        Assert.Equal(published.Title, detail.CurrentRevision.Title);
        Assert.Equal(published.Title, feed.CurrentRevision.Title);
        Assert.Equal(published.Title, history.Revision.Title);
        Assert.Equal(published.Conclusion, detail.CurrentRevision.Conclusion);
        var wire = JsonSerializer.SerializeToElement(update, Json);
        Assert.False(wire.TryGetProperty("title", out _));
        Assert.False(wire.TryGetProperty("conclusion", out _));
    }

    [Fact]
    public async Task LegacyNullTitleStaysNullAndSourceDeniedCardHasNoEvent()
    {
        var fixture = new PublicInsightReadFixture { NextCursor = null, General = null };
        var reads = fixture.Service();
        var snapshots = new InsightUpdateSnapshot(fixture, fixture, reads);
        var head = Assert.Single(await snapshots.Read(PublicInsightReadFixture.User, Scope, default));
        Assert.NotNull(await snapshots.Authorize(PublicInsightReadFixture.User, Scope, head, default));
        var detail = Assert.IsType<PublicPersonalInsightCard>(await reads.Detail(PublicInsightReadFixture.User, "personal", head.Id, default));
        Assert.Null(detail.CurrentRevision.Title);
        fixture.Materials[("own", "v1")] = PublicInsightReadFixture.Material("own", companies: [PublicInsightReadFixture.Foreign]);
        Assert.Empty((await reads.Feed(PublicInsightReadFixture.User, new(), default)).Items);
        Assert.Null(await snapshots.Authorize(PublicInsightReadFixture.User, Scope, head, default));
    }
}
