using System.Text.Json;
using InsightService.Common;
using InsightService.Contracts.PublicReads;
using InsightService.Models.Policies;
using InsightService.Services.PublicReads;
using Xunit;

public sealed class InsightPlainTextSourceLinkIntegrationTests
{
    [Fact]
    public async Task PlainTextCardKeepsVerifiedAnchorSeparateFromTitleAndConclusionAcrossReads()
    {
        var fixture = new PublicInsightReadFixture();
        var revision = fixture.Personal!.CurrentRevision with
        {
            Title = "Изменение расходов по договору",
            Conclusion = "Расходы могут измениться.\n\nПроверьте условия договора."
        };
        fixture.Personal = fixture.Personal with { CurrentRevision = revision };
        var news = fixture.Materials[("news", "v1")];
        var url = news.Provenance!.SourceUrl!;
        var draft = PublicInsightProjection.Draft(revision);
        Assert.Null(InsightPlainTextPolicy.ValidateDraft(draft));
        Assert.NotNull(InsightPlainTextPolicy.ValidateDraft(draft with { Conclusion = revision.Conclusion + " " + url }));
        Assert.NotNull(InsightPlainTextPolicy.ValidateDraft(draft with { Title = "[Источник](" + url + ")" }));

        var reads = fixture.Service();
        var feed = Assert.IsType<PublicPersonalInsightCard>(Assert.Single((await reads.Feed(PublicInsightReadFixture.User, new(), default)).Items));
        var detail = Assert.IsType<PublicPersonalInsightCard>(await reads.Detail(PublicInsightReadFixture.User, "personal", fixture.Personal.Id, default));
        var history = Assert.Single((await reads.History(PublicInsightReadFixture.User, fixture.Personal.Id, new(), default)).Items);
        foreach (var item in new[] { (feed.CurrentRevision, feed.Sources), (detail.CurrentRevision, detail.Sources), (history.Revision, history.Sources) })
        {
            Assert.Equal(revision.Title, item.Item1.Title);
            Assert.Equal(revision.Conclusion, item.Item1.Conclusion);
            Assert.Equal(revision.SourceRefs.Select(PublicInsightProjection.Reference), item.Item1.SourceRefs);
            Assert.Equal(url, Assert.Single(item.Item2, source => source.Kind == "news").Url);
            Assert.True(InsightPlainTextPolicy.IsPlain(item.Item1.Conclusion));
        }
        // Read-only projection does not sanitize or rewrite the saved narrative.
        Assert.Equal(revision, fixture.Personal.CurrentRevision);
    }

    [Fact]
    public async Task GeneralPlainTextCheckpointAndPublicSourcesRemainCompatible()
    {
        var fixture = new PublicInsightReadFixture();
        var general = fixture.General!;
        var checkpoint = JsonSerializer.SerializeToElement(new { plan = new { generalInsights = new[] { general.Insight } } }, AnalysisProtocol.Json);
        Assert.Null(InsightPlainTextPolicy.ValidateCheckpoint(checkpoint, default));
        var reads = fixture.Service();
        var feed = Assert.IsType<PublicGeneralInsightCard>(Assert.Single((await reads.Feed(PublicInsightReadFixture.User, new() { Kind = "general" }, default)).Items));
        var detail = Assert.IsType<PublicGeneralInsightCard>(await reads.Detail(PublicInsightReadFixture.User, "general", general.Id, default));
        Assert.Equal(feed.Insight.Title, detail.Insight.Title);
        Assert.Equal(feed.Insight.Conclusion, detail.Insight.Conclusion);
        Assert.Equal(feed.Insight.SourceRefs, detail.Insight.SourceRefs);
        Assert.True(InsightPlainTextPolicy.IsPlain(detail.Insight.Title));
        Assert.True(InsightPlainTextPolicy.IsPlain(detail.Insight.Conclusion));
        Assert.Equal(fixture.Materials[("news", "v1")].Provenance!.SourceUrl, Assert.Single(detail.Sources).Url);
        Assert.Empty(detail.Indicators);
    }
}
