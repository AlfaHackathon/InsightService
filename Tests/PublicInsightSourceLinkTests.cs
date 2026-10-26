using System.Text.Json;
using InsightService.Common;
using InsightService.Contracts.Context;
using InsightService.Contracts.PublicReads;
using InsightService.Services.PublicReads;
using Xunit;

public sealed class PublicInsightSourceLinkTests
{
    internal static MaterialVersion News() => JsonSerializer.Deserialize<MaterialVersion>(
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "source-link-material.json")), AnalysisProtocol.Json)!;

    private static PublicInsightReadFixture Fixture(MaterialVersion news)
    {
        var f = new PublicInsightReadFixture();
        f.Materials[(news.MaterialId, news.VersionId)] = news;
        var revision = f.Personal!.CurrentRevision;
        f.Personal = f.Personal with
        {
            NewsMaterialId = news.MaterialId,
            CurrentRevision = revision with
            {
                NewsMaterialId = news.MaterialId,
                NewsVersionId = news.VersionId,
                SourceRefs = [PublicInsightReadFixture.Ref(news), revision.SourceRefs[1]]
            }
        };
        f.General = PublicInsightReadFixture.GeneralItem([PublicInsightReadFixture.Ref(news)]) with
        { NewsMaterialId = news.MaterialId, NewsVersionId = news.VersionId };
        return f;
    }

    [Fact]
    public async Task ExactAdmittedMaterialUrlIsStructuredAcrossPersonalGeneralDetailAndHistory()
    {
        var news = News();
        var f = Fixture(news);
        var reads = f.Service();
        var personal = Assert.IsType<PublicPersonalInsightCard>(Assert.Single((await reads.Feed(PublicInsightReadFixture.User, new(), default)).Items));
        var detail = Assert.IsType<PublicPersonalInsightCard>(await reads.Detail(PublicInsightReadFixture.User, "personal", f.Personal!.Id, default));
        var general = Assert.IsType<PublicGeneralInsightCard>(Assert.Single((await reads.Feed(PublicInsightReadFixture.User, new() { Kind = "general" }, default)).Items));
        var generalDetail = Assert.IsType<PublicGeneralInsightCard>(await reads.Detail(PublicInsightReadFixture.User, "general", f.General!.Id, default));
        var history = Assert.Single((await reads.History(PublicInsightReadFixture.User, f.Personal.Id, new(), default)).Items);
        foreach (var sources in new[] { personal.Sources, detail.Sources, general.Sources, generalDetail.Sources, history.Sources })
        {
            var source = Assert.Single(sources, source => source.Kind == "news");
            Assert.Equal(news.Title, source.Title);
            Assert.Equal(news.Provenance!.SourceUrl, source.Url);
            Assert.Equal(source.Url, source.Provenance!.SourceUrl); // Existing consumers stay compatible.
            Assert.Equal(news.MaterialId, source.MaterialId);
            Assert.Equal(news.VersionId, source.VersionId);
            Assert.Equal(news.ContentHash, source.ContentHash);
            Assert.Equal("lead", source.Provenance.TextCompleteness);
            Assert.Null(source.NewsId); // No parser Mongo ID is present in the material contract.
            var wire = JsonSerializer.SerializeToElement(source, AnalysisProtocol.Json);
            Assert.Equal(source.Url, wire.GetProperty("url").GetString());
            Assert.Equal(JsonValueKind.Null, wire.GetProperty("newsId").ValueKind);
            Assert.False(wire.TryGetProperty("text", out _));
            Assert.DoesNotContain("publisher-123", wire.GetRawText()); // Internal locator stays private.
        }
        Assert.Equal(f.Personal.CurrentRevision.Conclusion, detail.CurrentRevision.Conclusion);
        Assert.DoesNotContain("](", detail.CurrentRevision.Conclusion);
        Assert.DoesNotContain(news.Provenance!.SourceUrl!, detail.CurrentRevision.Conclusion);
    }

    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData("data:text/html,unsafe")]
    [InlineData("file:///private")]
    [InlineData("ftp://publisher.example.test/news")]
    [InlineData("https://user:password@publisher.example.test/news")]
    [InlineData(" https://publisher.example.test/news")]
    [InlineData("https://publisher.example.test/ne\nws")]
    [InlineData("https://publisher.example.test/a\\b")]
    public async Task UnsafeProvenanceNeverBecomesAnAnchorInAnyPublicRead(string url)
    {
        var news = News();
        var f = Fixture(news with { Provenance = news.Provenance! with { SourceUrl = url } });
        var reads = f.Service();
        var personal = Assert.IsType<PublicPersonalInsightCard>(Assert.Single((await reads.Feed(PublicInsightReadFixture.User, new(), default)).Items));
        var detail = Assert.IsType<PublicPersonalInsightCard>(await reads.Detail(PublicInsightReadFixture.User, "personal", f.Personal!.Id, default));
        var general = Assert.IsType<PublicGeneralInsightCard>(Assert.Single((await reads.Feed(PublicInsightReadFixture.User, new() { Kind = "general" }, default)).Items));
        var history = Assert.Single((await reads.History(PublicInsightReadFixture.User, f.Personal.Id, new(), default)).Items);
        foreach (var sources in new[] { personal.Sources, detail.Sources, general.Sources, history.Sources })
        {
            var source = Assert.Single(sources, source => source.Kind == "news");
            Assert.Null(source.Url);
            Assert.Null(source.Provenance!.SourceUrl);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task MissingProvenanceOrUrlStaysNullWithoutBuildingLinksFromIdsOrTitles(bool noProvenance)
    {
        var news = News();
        news = news with { Title = "https://unverified.example.test/title", Provenance = noProvenance ? null : news.Provenance! with { SourceUrl = null } };
        var f = Fixture(news);
        var card = Assert.IsType<PublicPersonalInsightCard>(Assert.Single((await f.Service().Feed(PublicInsightReadFixture.User, new(), default)).Items));
        var source = Assert.Single(card.Sources, source => source.Kind == "news");
        Assert.Null(source.Url);
        Assert.Null(source.NewsId);
        Assert.Equal(news.Title, source.Title);
    }

    [Fact]
    public async Task HistoryKeepsExactVersionLinksAndDuplicateCitationsShareOneSourceEntry()
    {
        var news = News();
        var older = news with { VersionId = "older-v1", Provenance = news.Provenance! with { SourceUrl = "http://publisher.example.test/older" } };
        var f = Fixture(news);
        f.Materials[(older.MaterialId, older.VersionId)] = older;
        var revision = f.Personal!.CurrentRevision;
        f.Personal = f.Personal with { CurrentRevision = revision with { SourceRefs = [.. revision.SourceRefs, revision.SourceRefs[0]] } };
        f.Revisions = [f.Personal.CurrentRevision, revision with
        {
            RevisionId = "older-revision", NewsVersionId = older.VersionId,
            SourceRefs = [PublicInsightReadFixture.Ref(older), revision.SourceRefs[1]]
        }];
        var reads = f.Service();
        var history = await reads.History(PublicInsightReadFixture.User, f.Personal.Id, new(), default);
        Assert.Equal([news.Provenance!.SourceUrl, older.Provenance!.SourceUrl], history.Items.Select(item => item.Sources.Single(source => source.Kind == "news").Url));
        Assert.All(history.Items, item => Assert.Equal(2, item.Sources.Length));
        Assert.Equal(3, f.SourceCalls); // Two news versions + one own-company material.
        Assert.Equal("opaque-next", history.NextCursor);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LinkMetadataDoesNotBypassCompanyOrBankAcl(bool bank)
    {
        var news = News();
        news = bank ? news with { BankProfileIds = ["U_97"] } : news with { CompanyIds = [PublicInsightReadFixture.Foreign] };
        var f = Fixture(news);
        var reads = f.Service();
        foreach (var kind in new[] { "personal", "general" })
        {
            var page = await reads.Feed(PublicInsightReadFixture.User, new() { Kind = kind }, default);
            Assert.Empty(page.Items);
            Assert.Equal("opaque-next", page.NextCursor);
            var id = kind == "personal" ? f.Personal!.Id : f.General!.Id;
            Assert.Equal(404, (await Assert.ThrowsAsync<PublicInsightReadException>(() => reads.Detail(PublicInsightReadFixture.User, kind, id, default))).StatusCode);
        }
        Assert.Equal(404, (await Assert.ThrowsAsync<PublicInsightReadException>(() => reads.History(PublicInsightReadFixture.User, f.Personal!.Id, new(), default))).StatusCode);
    }
}
