using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using InsightService.Contracts.Analyses;
using InsightService.Models.Policies;
using InsightService.Services;
using Xunit;

public sealed class InsightPlainTextTests
{
    [Fact]
    public async Task MarkdownInNewConclusionIsRejectedWithoutStrippingOrWriting()
    {
        var fixture = PreservationFixture.Load();
        var input = fixture.CurrentFirst;
        var draft = input.Drafts[0] with { Conclusion = "**Расходы** могут вырасти на 20000 RUB в 2027 году. Проверьте условия договора." };
        input = input with { Drafts = [draft] };
        var repo = new CalculationNarrativeServiceTests.MemoryRepository();
        var result = await new AnalysisService(repo, fixture).Save(input);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, result.Error?.HttpStatusCode);
        Assert.StartsWith("INSIGHT_PLAIN_TEXT_REQUIRED:", result.Error?.Title);
        Assert.Equal(draft.Conclusion, input.Drafts[0].Conclusion);
        Assert.Null(repo.Saved);
    }

    [Theory]
    [InlineData("**вывод**")]
    [InlineData("__вывод__")]
    [InlineData("`8000 RUB`")]
    [InlineData("```\n8000 RUB\n```")]
    [InlineData("~~~\nпример\n~~~")]
    [InlineData("# Заголовок")]
    [InlineData("###\r\nТекст")]
    [InlineData("Абзац\n  ## Подзаголовок")]
    [InlineData("[источник](/relative)")]
    [InlineData("[источник][reference]")]
    [InlineData("![image](relative)")]
    [InlineData("https://example.test/source")]
    [InlineData("<HTTP://example.test/source>")]
    [InlineData("*важно*")]
    [InlineData("_важно_")]
    [InlineData("*x*")]
    public void FormattingMarkersAndInlineSourceUrlsAreRejected(string text) => Assert.False(InsightPlainTextPolicy.IsPlain(text));

    [Theory]
    [InlineData("Первый абзац.\n\nВторой абзац.")]
    [InlineData("2 * 3 - 1 = 5")]
    [InlineData("a*b*c")]
    [InlineData("Изменение: -8000 RUB за 2027 год.")]
    [InlineData("Число 8\u202f000.00 RUB остаётся точным.")]
    public void PlainParagraphsArithmeticAndLiteralAmountsRemainUntouched(string text)
    {
        Assert.True(InsightPlainTextPolicy.IsPlain(text));
    }

    [Fact]
    public async Task PreparationAndSaveGiveTheSameBoundedRepairWithoutEchoingDraftArguments()
    {
        var fixture = PreservationFixture.Load();
        var request = fixture.CurrentFirst;
        var draft = request.Drafts[0] with { Title = "**Изменение расходов**" };
        request = request with { Drafts = [draft] };
        var repo = new CalculationNarrativeServiceTests.MemoryRepository();
        var prepared = await new AnalysisPreparationService(repo, null!, fixture, null!, null!).PrepareAsync(new() { Request = request, Drafts = request.Drafts });
        var saved = await new AnalysisService(repo, fixture).Save(request);
        Assert.Equal(prepared.Error!.Title, saved.Error!.Title);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, saved.Error.HttpStatusCode);
        Assert.DoesNotContain(draft.Title, saved.Error.Title);
        Assert.True(Encoding.UTF8.GetByteCount(saved.Error.Title) < 512);
        Assert.Null(repo.Saved);
    }

    [Fact]
    public async Task ParagraphsClaimQuoteAndRawSourceEvidenceAreNotStripped()
    {
        var fixture = PreservationFixture.Load();
        var request = fixture.CurrentFirst;
        var draft = request.Drafts[0] with { Conclusion = "Расходы могут вырасти на 20000 RUB в 2027 году.\n\nФактическая оплата не подтверждена." };
        // Source facts and quotations are evidence, not generated card prose.
        var sourceQuote = draft with { Assessment = draft.Assessment! with { Conditions = [draft.Assessment.Conditions[0] with { Fact = "**Exact source excerpt** https://example.test/source" }] } };
        Assert.Null(InsightPlainTextPolicy.ValidateDraft(sourceQuote));
        var repo = new CalculationNarrativeServiceTests.MemoryRepository();
        await new AnalysisService(repo, fixture).Save(request with { Drafts = [draft] }).RequireContentAsync();
        Assert.Equal(draft.Conclusion, Assert.Single(repo.Saved!.Drafts).Conclusion);
        Assert.Equal(draft.CalculationClaim, repo.Saved.Drafts[0].CalculationClaim);
    }

    [Theory]
    [InlineData("plan")]
    [InlineData("result")]
    [InlineData("segmentBatches")]
    public void BackendRejectsNewGeneralMarkupAtEveryKnownCheckpointLocation(string location)
    {
        var checkpoint = Checkpoint(location, "**Общий заголовок**", "Общий вывод");
        var error = InsightPlainTextPolicy.ValidateCheckpoint(checkpoint, JsonSerializer.SerializeToElement(new { }));
        Assert.StartsWith("INSIGHT_PLAIN_TEXT_REQUIRED:", error!.Title);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, error.HttpStatusCode);
        Assert.Null(InsightPlainTextPolicy.ValidateCheckpoint(Checkpoint(location, "Общий заголовок", "Абзац.\n\nЕщё абзац."), default));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("[]")]
    [InlineData("{\"generalInsights\":null}")]
    [InlineData("{\"generalInsights\":{}}")]
    [InlineData("{\"generalInsights\":[]}")]
    public void MissingOrNonArrayGeneralEntriesRemainEmpty(string json)
    {
        var plan = JsonSerializer.Deserialize<JsonElement>(json);
        var checkpoint = JsonSerializer.SerializeToElement(new { plan, result = plan, segmentBatches = new[] { new { plan } } });
        Assert.Null(InsightPlainTextPolicy.ValidateCheckpoint(checkpoint, default));
    }

    [Fact]
    public void ExactLegacyGeneralEntriesCanBeCarriedWithoutRewritingButChangedOnesCannot()
    {
        var old = Checkpoint("plan", "**Исторический заголовок**", "Исторический вывод");
        Assert.Null(InsightPlainTextPolicy.ValidateCheckpoint(old, old));
        var carriedToResult = Checkpoint("result", "**Исторический заголовок**", "Исторический вывод");
        Assert.Null(InsightPlainTextPolicy.ValidateCheckpoint(carriedToResult, old));
        Assert.NotNull(InsightPlainTextPolicy.ValidateCheckpoint(Checkpoint("plan", "**Другой заголовок**", "Исторический вывод"), old));
        Assert.NotNull(InsightPlainTextPolicy.ValidateCheckpoint(Checkpoint("plan", "**Исторический заголовок**", "Исторический вывод", "v2"), old));
    }

    [Fact]
    public async Task ExactHistoricalReceiptReplayBypassesOnlyNewAdmissionNotItsIdentityHash()
    {
        var fixture = PreservationFixture.Load();
        var request = fixture.CurrentFirst;
        request = request with { Drafts = [request.Drafts[0] with { Title = "**Legacy title**" }] };
        var normalized = AnalysisRequestNormalizer.Normalize(request).RequireContent().Item1;
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(normalized, AnalysisService.Json))));
        var repo = new CalculationNarrativeServiceTests.MemoryRepository
        {
            Prior = new(hash, new("old-receipt", request.CompanyId, request.BankProfileId, request.NewsMaterialId, request.NewsVersionId,
                request.AsOf, request.AsOf, false, AnalysisService.ReceiptSemantics, []))
        };
        Assert.True((await new AnalysisService(repo, null!).Save(request).RequireContentAsync()).Duplicate);
        Assert.Null(repo.Saved);
        var changed = request with { Drafts = [request.Drafts[0] with { Conclusion = "Different historical payload" }] };
        Assert.Equal(HttpStatusCode.Conflict, (await new AnalysisService(repo, null!).Save(changed)).Error!.HttpStatusCode);
    }

    private static JsonElement Checkpoint(string location, string title, string conclusion, string version = "v1")
    {
        var plan = new { generalInsights = new[] { new { title, conclusion, sourceRefs = new[] { new { materialId = "news", versionId = version, contentHash = new string('a', 64) } } } } };
        return location == "segmentBatches" ? JsonSerializer.SerializeToElement(new { segmentBatches = new[] { new { plan } } }) :
            JsonSerializer.SerializeToElement(new Dictionary<string, object> { [location] = plan });
    }
}
