using System.Net;
using InsightService.Contracts.Analyses;
using InsightService.Contracts.PublicReads;
using InsightService.Models.Policies;
using InsightService.Services;
using System.Text.Json;
using Xunit;

public sealed class InsightTitleTests
{
    [Fact]
    public async Task NewPublishedPersonalInsightRequiresAnExplicitTitleWithoutInventingOne()
    {
        var fixture = PreservationFixture.Load();
        var request = fixture.CurrentFirst;
        request = request with { Drafts = [request.Drafts[0] with { Title = null }] };
        var repo = new CalculationNarrativeServiceTests.MemoryRepository();
        var result = await new AnalysisService(repo, fixture).Save(request);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, result.Error?.HttpStatusCode);
        Assert.StartsWith("TITLE_REQUIRED:", result.Error?.Title);
        Assert.Null(repo.Saved);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Первая строка\nВторая строка")]
    [InlineData("Первая\u2028Вторая")]
    [InlineData("question-aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    [InlineData("revision_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    public void EmptyMultilineOrOpaqueTitlesAreRejectedWithoutRewriting(string title)
    {
        var draft = CalculationNarrativeTests.Draft("Обоснованный вывод") with { Title = title };
        Assert.StartsWith("TITLE_INVALID:", InsightTitlePolicy.Validate(draft)!.Title);
        Assert.Equal(title, draft.Title);
    }

    [Fact]
    public void LengthIsBoundedAndNoInsightNeedsNoInventedTitle()
    {
        var draft = CalculationNarrativeTests.Draft("Обоснованный вывод") with { Title = new string('Я', 181) };
        Assert.StartsWith("TITLE_INVALID:", InsightTitlePolicy.Validate(draft)!.Title);
        var unknown = PreservationFixture.Load().Second.Drafts[0] with { Title = null };
        Assert.Null(InsightTitlePolicy.Validate(unknown));
        Assert.Null(unknown.Title);
        Assert.True(AnalysisPublicationPolicy.IsDiagnostic(unknown));
    }

    [Theory]
    [InlineData("Расходы могут вырасти", "Расходы: 8000 RUB за 2027 год.")]
    [InlineData("Расходы: 8000 RUB", "Расходы могут вырасти; проверьте условия договора.")]
    [InlineData("Расходы: 8000 RUB", "Расходы: 8000 RUB за 2027 год.")]
    public void OneCanonicalLiteralClaimMayBindTitleBodyOrBoth(string title, string conclusion)
    {
        var draft = CalculationNarrativeTests.Draft(conclusion) with { Title = title, CalculationClaim = new("8000 RUB", "8000", "RUB", "2027") };
        Assert.Null(InsightTitlePolicy.Validate(draft));
        Assert.Null(CalculationClaimPolicy.Validate(draft, "8000"));
        Assert.Equal(title, draft.Title);
        Assert.Equal(conclusion, draft.Conclusion);
    }

    [Theory]
    [InlineData("Рост расходов: 800 RUB", "Расходы могут вырасти.", "800 RUB", "800", "CONCLUSION_CLAIM_VALUE_MISMATCH")]
    [InlineData("Рост расходов: 800 RUB", "Расходы: 8000 RUB за 2027 год.", "8000 RUB", "8000", "CONCLUSION_CLAIM_REQUIRED")]
    [InlineData("Рост расходов: 8000 RUB", "Расходы: 800 RUB за 2027 год.", "8000 RUB", "8000", "CONCLUSION_CLAIM_REQUIRED")]
    public void TitleCannotHideAContradictionToTheCalculationOrBody(string title, string conclusion, string quote, string value, string code)
    {
        var draft = CalculationNarrativeTests.Draft(conclusion) with { Title = title, CalculationClaim = new(quote, value, "RUB", "2027") };
        Assert.StartsWith(code + ":", CalculationClaimPolicy.Validate(draft, "8000")!.Title);
    }

    [Fact]
    public void UnsupportedTitleNumbersNeedCalculationWhileDiagnosticAmountsStayInternal()
    {
        var draft = PreservationFixture.Load().CurrentFirst.Drafts[0] with { Calculation = null, CalculationClaim = null, Title = "Расходы вырастут на 8000 RUB" };
        Assert.StartsWith("TITLE_NUMERIC_REQUIRES_CALCULATION:", InsightTitlePolicy.Validate(draft)!.Title);
        var unknown = PreservationFixture.Load().Second.Drafts[0] with { Title = draft.Title };
        Assert.True(AnalysisPublicationPolicy.IsDiagnostic(unknown));
        Assert.Null(InsightTitlePolicy.Validate(unknown));
    }

    [Theory]
    [InlineData("Рост расходов на ٨٠٠")]
    [InlineData("Рост расходов на 𝟠𝟘𝟘")]
    public void UnsupportedUnicodeNumeralsCannotBypassTheTitleLiteralContract(string title)
    {
        var draft = CalculationNarrativeTests.Draft("Расходы могут вырасти.") with { Title = title };
        var error = CalculationClaimPolicy.Validate(draft, "8000");
        Assert.NotNull(error);
        Assert.StartsWith("CONCLUSION_CLAIM_REQUIRED:", error.Title);
        draft = PreservationFixture.Load().CurrentFirst.Drafts[0] with { Title = title, Calculation = null, CalculationClaim = null };
        Assert.StartsWith("TITLE_NUMERIC_REQUIRES_CALCULATION:", InsightTitlePolicy.Validate(draft)!.Title);
    }

    [Fact]
    public async Task ExplicitTitleAndConclusionArePreservedByPreparationAndSave()
    {
        var fixture = PreservationFixture.Load();
        var request = fixture.CurrentFirst;
        var draft = request.Drafts[0] with { Title = "Изменение условий займа требует проверки" };
        request = request with { Drafts = [draft] };
        var repo = new CalculationNarrativeServiceTests.MemoryRepository();
        var prepared = await new AnalysisPreparationService(repo, null!, fixture, null!, null!).PrepareAsync(new() { Request = request, Drafts = request.Drafts }).RequireContentAsync();
        await new AnalysisService(repo, fixture).Save(request).RequireContentAsync();
        Assert.Equal(draft.Title, Assert.Single(prepared.Drafts).Title);
        Assert.Equal(draft.Title, Assert.Single(repo.Saved!.Drafts).Title);
        Assert.Equal(draft.Conclusion, repo.Saved.Drafts[0].Conclusion);
    }

    [Fact]
    public async Task PublicFeedDetailAndHistoryExposeEachVersionsTitleWithoutBackfill()
    {
        var f = new PublicInsightReadFixture();
        var current = f.Personal!.CurrentRevision with { Title = "Рост расходов по условиям займа" };
        var legacy = current with { RevisionId = "legacy", Title = null, PreviousRevisionId = null };
        f.Personal = f.Personal with { CurrentRevision = current };
        f.Revisions = [current, legacy];
        var service = f.Service();
        var card = Assert.IsType<PublicPersonalInsightCard>(Assert.Single((await service.Feed(PublicInsightReadFixture.User, new(), default)).Items));
        var detail = Assert.IsType<PublicPersonalInsightCard>(await service.Detail(PublicInsightReadFixture.User, "personal", f.Personal.Id, default));
        var history = await service.History(PublicInsightReadFixture.User, f.Personal.Id, new(), default);
        Assert.Equal(current.Title, card.CurrentRevision.Title);
        Assert.Equal(current.Title, detail.CurrentRevision.Title);
        Assert.Equal(current.Title, history.Items[0].Revision.Title);
        Assert.Null(history.Items[1].Revision.Title);
        Assert.Equal(JsonValueKind.Null, JsonSerializer.SerializeToElement(history.Items[1].Revision, AnalysisService.Json).GetProperty("title").ValueKind);
        var json = JsonSerializer.Serialize(legacy, AnalysisService.Json);
        Assert.DoesNotContain("\"title\"", json);
        Assert.Null(JsonSerializer.Deserialize<EffectRevision>(json, AnalysisService.Json)!.Title);
    }

    [Fact]
    public void LegacyDraftJsonOmitsNullTitleAndDoesNotDeriveItFromConclusion()
    {
        var draft = CalculationNarrativeTests.Draft("Исторический вывод") with { Title = null };
        var json = JsonSerializer.Serialize(draft, AnalysisService.Json);
        Assert.DoesNotContain("\"title\"", json);
        Assert.Null(JsonSerializer.Deserialize<EffectDraft>(json, AnalysisService.Json)!.Title);
    }

    [Fact]
    public async Task GeneralInsightRetainsItsExistingSeparateTitle()
    {
        var f = new PublicInsightReadFixture();
        var card = Assert.IsType<PublicGeneralInsightCard>(Assert.Single((await f.Service().Feed(PublicInsightReadFixture.User, new() { Kind = "general" }, default)).Items));
        Assert.Equal("Synthetic general result", card.Insight.Title);
        Assert.Equal("Public source result", card.Insight.Conclusion);
    }
}
