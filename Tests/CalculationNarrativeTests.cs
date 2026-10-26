using InsightService.Contracts.Analyses;
using InsightService.Contracts.Calculations;
using InsightService.Contracts.Questions;
using InsightService.Models.Policies;
using System.Net;
using System.Text.Json;
using InsightService.Services;
using Xunit;

public sealed class CalculationNarrativeTests
{
    [Fact]
    public void NormalizationPreservesTheAgentsVerbatimCalculatedConclusion()
    {
        var draft = Draft("  Расходы могут вырасти; проверьте условия договора.  ");
        var result = AnalysisRequestNormalizer.Normalize(Request(draft));
        Assert.Null(result.Error);
        Assert.Equal(draft.Conclusion, result.Content.Item1.Drafts[0].Conclusion);
    }

    [Theory]
    [InlineData("800 RUB", "800", "RUB", "2027", "CONCLUSION_CLAIM_VALUE_MISMATCH")]
    [InlineData("8000 USD", "8000", "USD", "2027", "CONCLUSION_CLAIM_UNIT_MISMATCH")]
    [InlineData("8000 RUB", "8000", "RUB", "2028", "CONCLUSION_CLAIM_PERIOD_MISMATCH")]
    [InlineData("8000 RUB", "8000.0", "RUB", "2027", "CONCLUSION_CLAIM_VALUE_MISMATCH")]
    [InlineData("8,000 RUB", "8000", "RUB", "2027", "CONCLUSION_CLAIM_INVALID_QUOTE")]
    [InlineData("восемь тысяч рублей", "8000", "RUB", "2027", "CONCLUSION_CLAIM_INVALID_QUOTE")]
    public void ContradictoryOrUnsupportedLiteralIsRepairableNotRewritten(string quote, string value, string unit, string period, string code)
    {
        var draft = Draft($"Изменение расходов: {quote} за 2027 год.") with { CalculationClaim = new(quote, value, unit, period) };
        var error = CalculationClaimPolicy.Validate(draft, "8000");
        Assert.StartsWith(code + ":", error!.Title);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, error.HttpStatusCode);
        Assert.Contains(quote, draft.Conclusion);
    }

    [Theory]
    [InlineData("8000 RUB")]
    [InlineData("+8 000.00 RUB")]
    [InlineData("8\u00a0000 ₽")]
    [InlineData("8\u202f000 руб.")]
    public void ExactLiteralAndCanonicalDecimalAreAcceptedWithoutAnyTextTransformation(string quote)
    {
        var text = $"  Компания может столкнуться с ростом расходов на {quote} в 2027 году. Проверьте условия договора.  ";
        var draft = Draft(text) with { CalculationClaim = new(quote, "8000", "RUB", "2027") };
        Assert.Null(CalculationClaimPolicy.Validate(draft, "8000"));
        var normalized = AnalysisRequestNormalizer.Normalize(Request(draft)).RequireContent().Item1.Drafts[0];
        Assert.Equal(text, normalized.Conclusion);
        Assert.Equal(draft.CalculationClaim, normalized.CalculationClaim);
    }

    [Theory]
    [InlineData("Расходы вырастут на 800 RUB.")]
    [InlineData("Расходы вырастут на восемь тысяч рублей.")]
    [InlineData("Расходы вырастут на 8000.")]
    public void NewlyCalculatedNumericConclusionRequiresAnExplicitClaim(string text)
    {
        var error = CalculationClaimPolicy.Validate(Draft(text), "8000");
        Assert.StartsWith("CONCLUSION_CLAIM_REQUIRED:", error!.Title);
    }

    [Theory]
    [InlineData("Сумма: 8000 RUB и ещё 8000 RUB.", "8000 RUB")]
    [InlineData("Сумма: 18000 RUB.", "8000 RUB")]
    [InlineData("Сумма: 𝟙8000 RUB.", "8000 RUB")]
    [InlineData("Сумма: 8000 RUB𝟙.", "8000 RUB")]
    [InlineData("Сумма: 8000 RUBль.", "8000 RUB")]
    [InlineData("Сумма неизвестна.", "8000 RUB")]
    public void QuoteMustBeOneWholeExactOccurrence(string text, string quote)
    {
        var draft = Draft(text) with { CalculationClaim = new(quote, "8000", "RUB", "2027") };
        Assert.StartsWith("CONCLUSION_CLAIM_INVALID_QUOTE:", CalculationClaimPolicy.Validate(draft, "8000")!.Title);
    }

    [Theory]
    [InlineData("Сумма: 8000 RUB млн за 2027 год.")]
    [InlineData("Сумма: 8000 RUB тысяч за 2027 год.")]
    [InlineData("Сумма: 8000 RUB million за 2027 год.")]
    public void ScaleWordsAreOutsideTheSupportedLiteralProfile(string text)
    {
        var draft = Draft(text) with { CalculationClaim = new("8000 RUB", "8000", "RUB", "2027") };
        Assert.StartsWith("CONCLUSION_CLAIM_REQUIRED:", CalculationClaimPolicy.Validate(draft, "8000")!.Title);
    }

    [Fact]
    public void AdditionalUnboundNumberCannotHideBehindCorrectResultClaim()
    {
        var draft = Draft("Расходы: 8000 RUB за 2027 год; рост 20%.") with { CalculationClaim = new("8000 RUB", "8000", "RUB", "2027") };
        Assert.StartsWith("CONCLUSION_UNBOUND_NUMBER:", CalculationClaimPolicy.Validate(draft, "8000")!.Title);
    }

    [Fact]
    public void QualitativeUnknownAndCalculatedQualitativeNarrativesNeedNoInventedClaim()
    {
        var draft = Draft("Экспозиция требует проверки; данных о бюджете недостаточно.");
        Assert.Null(CalculationClaimPolicy.Validate(draft, "8000"));
        Assert.Null(CalculationClaimPolicy.Validate(draft with { Calculation = null, Status = "unknown" }, null));
        Assert.StartsWith("CONCLUSION_CLAIM_WITHOUT_CALCULATION:", CalculationClaimPolicy.Validate(draft with
        {
            Calculation = null,
            CalculationClaim = new("8000 RUB", "8000", "RUB", "2027")
        }, null)!.Title);
    }

    [Fact]
    public void LegacySerializationAndReadbackDoNotInventClaimsOrRewriteProse()
    {
        var draft = Draft("Legacy raw narrative");
        var json = JsonSerializer.Serialize(draft, AnalysisService.Json);
        Assert.DoesNotContain("calculationClaim", json);
        Assert.Null(JsonSerializer.Deserialize<EffectDraft>(json, AnalysisService.Json)!.CalculationClaim);
        var revision = PublicInsightReadFixture.Revision() with { Conclusion = "Исторический точный текст 800 RUB" };
        var restored = JsonSerializer.Deserialize<EffectRevision>(JsonSerializer.Serialize(revision, AnalysisService.Json), AnalysisService.Json)!;
        Assert.Equal(revision.Conclusion, restored.Conclusion);
        Assert.Null(restored.CalculationClaim);
    }

    internal static EffectDraft Draft(string text) => new("fixture", "FixtureLoan", null, text, "forecast",
        [new("news", "v1", new string('a', 64))],
        new(new("multiply", Left: new("value", "100"), Right: new("value", "80")), "RUB", "2027"),
        Question: new("financing_cost", "event_only", "RUB", "2027", null, "contract_total"));
    internal static AnalysisRequest Request(EffectDraft draft) => new(Guid.Parse("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa"), null, "news", "v1", DateTimeOffset.Parse("2026-10-26T00:00:00Z"), [draft]);
}
