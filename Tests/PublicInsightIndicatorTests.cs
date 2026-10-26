using InsightService.Contracts.Analyses;
using InsightService.Contracts.Context;
using InsightService.Contracts.PublicReads;
using InsightService.Contracts.Questions;
using Xunit;

public sealed class PublicInsightIndicatorTests
{
    private static async Task<PublicPersonalInsightCard> Card(PublicInsightReadFixture fixture) =>
        Assert.IsType<PublicPersonalInsightCard>(Assert.Single((await fixture.Service().Feed(PublicInsightReadFixture.User, new(), default)).Items));

    [Theory]
    [InlineData("stale", "stale_sources")]
    [InlineData("no-calculation", "no_calculation")]
    [InlineData("null-result", "missing_result")]
    [InlineData("nan-result", "missing_result")]
    [InlineData("arabic-result", "missing_result")]
    [InlineData("no-question", "insufficient_evidence")]
    [InlineData("no-assessment", "insufficient_evidence")]
    [InlineData("unknown-period", "unknown_period")]
    [InlineData("invalid-period", "unknown_period")]
    [InlineData("arabic-period", "unknown_period")]
    [InlineData("unitless-calculation", "insufficient_evidence")]
    [InlineData("qualitative-unit", "insufficient_evidence")]
    [InlineData("bom-unknown-unit", "insufficient_evidence")]
    [InlineData("different-period", "insufficient_evidence")]
    [InlineData("profile-only", "insufficient_evidence")]
    [InlineData("no-documentary-source", "insufficient_evidence")]
    [InlineData("unknown-status", "insufficient_evidence")]
    [InlineData("no-object", "insufficient_evidence")]
    [InlineData("object-prefix", "insufficient_evidence")]
    [InlineData("object-unicode-number", "insufficient_evidence")]
    [InlineData("object-unicode-letter-number", "insufficient_evidence")]
    [InlineData("unsupported-derived", "insufficient_evidence")]
    [InlineData("cyclic-dependency", "insufficient_evidence")]
    [InlineData("supported-unknown", "insufficient_evidence")]
    [InlineData("no-scenario-assumption", "insufficient_evidence")]
    [InlineData("wrong-publication", "insufficient_evidence")]
    [InlineData("absolute-delta", "insufficient_evidence")]
    [InlineData("event-budget", "insufficient_evidence")]
    [InlineData("budget-unproven", "unknown_baseline")]
    [InlineData("budget-prefix", "unknown_baseline")]
    [InlineData("budget-not-exact-excerpt", "unknown_baseline")]
    public async Task EveryFinancialGuardKeepsHistoricalCardReadableAndSuppressesOnlyIndicator(string cause, string expected)
    {
        var f = new PublicInsightReadFixture();
        var r = f.Personal!.CurrentRevision;
        var own = r.SourceRefs[1];
        var source = f.Materials[("own", "v1")];
        switch (cause)
        {
            case "stale":
                f.Personal = f.Personal with { RequiresReassessment = true };
                break;
            case "no-calculation":
                r = r with { Calculation = null };
                break;
            case "null-result":
                r = r with { CalculationResult = null };
                break;
            case "nan-result":
                r = r with { CalculationResult = "NaN" };
                break;
            case "arabic-result":
                r = r with { CalculationResult = "١.٢" };
                break;
            case "no-question":
                r = r with { Question = null };
                break;
            case "no-assessment":
                r = r with { Assessment = null };
                break;
            case "unknown-period":
                r = r with { Question = r.Question! with { TargetPeriod = "unknown" } };
                break;
            case "invalid-period":
                r = r with { Question = r.Question! with { TargetPeriod = "2026-13" } };
                break;
            case "arabic-period":
                r = r with { Question = r.Question! with { TargetPeriod = "٢٠٢٦-١١" } };
                break;
            case "unitless-calculation":
                r = r with { Calculation = r.Calculation! with { Unit = "" } };
                break;
            case "qualitative-unit":
                r = r with { Question = r.Question! with { Unit = "qualitative" }, Calculation = r.Calculation! with { Unit = "qualitative" } };
                break;
            case "bom-unknown-unit":
                r = r with { Question = r.Question! with { Unit = "\uFEFFunknown" }, Calculation = r.Calculation! with { Unit = "\uFEFFunknown" } };
                break;
            case "different-period":
                r = r with { Calculation = r.Calculation! with { Period = "2026-12" } };
                break;
            case "profile-only":
                r = r with { Assessment = r.Assessment! with { Applicability = r.Assessment.Applicability! with { Status = "unknown", Basis = "profile_signal" } } };
                break;
            case "no-documentary-source":
                f.Materials[("own", "v1")] = source with { SourceId = "provided-bank-csv" };
                break;
            case "unknown-status":
                r = r with { Impact = r.Impact! with { EvidenceStatus = "unknown" } };
                break;
            case "no-object":
                r = r with { Impact = r.Impact! with { EconomicObjectId = null } };
                break;
            case "object-prefix":
                f.Materials[("own", "v1")] = source with { Text = "Only loan L-1234 exists" };
                break;
            case "object-unicode-number":
                f.Materials[("own", "v1")] = source with { Text = "Only loan L-123² exists" };
                break;
            case "object-unicode-letter-number":
                f.Materials[("own", "v1")] = source with { Text = "Only loan L-123Ⅳ exists" };
                break;
            case "unsupported-derived":
                r = r with
                {
                    Assessment = r.Assessment! with
                    {
                        Conditions = [new("Unsupported derived claim", "exposure", "supported", [own], "derived", "derived", ["missing"])],
                        Unknowns = [new("Unknown required premise", null, "company", null, "missing")]
                    }
                };
                break;
            case "cyclic-dependency":
                r = r with { Assessment = r.Assessment! with { Conditions = [new("Cyclic claim", "exposure", "supported", [own], "cycle", "derived", ["cycle"])] } };
                break;
            case "supported-unknown":
                r = r with
                {
                    Impact = r.Impact! with { EvidenceStatus = "supported" },
                    PublicationClass = "consequence",
                    Assessment = r.Assessment! with { Unknowns = [new("Unknown premise", null, "company", null)], Applicability = r.Assessment.Applicability! with { Assumptions = [] } }
                };
                break;
            case "no-scenario-assumption":
                r = r with { Assessment = r.Assessment! with { Applicability = r.Assessment.Applicability! with { Assumptions = [] } } };
                break;
            case "wrong-publication":
                r = r with { PublicationClass = "consequence" };
                break;
            case "absolute-delta":
                r = r with { Question = r.Question! with { Frame = "absolute" } };
                break;
            case "event-budget":
                r = r with { Question = r.Question! with { BaselinePeriod = "budget:2027" } };
                break;
            default:
                r = r with { Question = r.Question! with { Frame = "budget_delta", BaselinePeriod = "budget:2027" } };
                if (cause == "budget-prefix")
                {
                    const string excerpt = "budget:20270 object L-123: amount 100 RUB";
                    f.Materials[("own", "v1")] = source with { Text = source.Text + "; " + excerpt };
                    r = r with { Assessment = r.Assessment! with { Conditions = [new(excerpt, "budget_baseline", "supported", [own], "budget", "observed", [])] } };
                }
                if (cause == "budget-not-exact-excerpt")
                {
                    r = r with { Assessment = r.Assessment! with { Conditions = [new("budget:2027 object L-123: amount 100 RUB", "budget_baseline", "supported", [own], "budget", "observed", [])] } };
                }

                break;
        }
        f.Personal = f.Personal! with { CurrentRevision = r };
        if (cause == "unknown-status")
        {
            Assert.Empty((await f.Service().Feed(PublicInsightReadFixture.User, new(), default)).Items);
            return; // Missing-data heads are diagnostics, not cards with an absent indicator.
        }
        var card = await Card(f);
        Assert.Empty(card.Indicators);
        Assert.Equal(expected, card.IndicatorAvailability.Reason);
        Assert.Equal("revision-2", card.CurrentRevision.RevisionId);
        Assert.Equal(2, card.Sources.Length);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-0.0000")]
    [InlineData("12345678901234567890.12340000")]
    [InlineData("-1.2300e+40")]
    public async Task ProvenResultRemainsTheOriginalStringWithNoNumericConversionOrZeroFallback(string result)
    {
        var f = new PublicInsightReadFixture();
        f.Personal = f.Personal! with { CurrentRevision = f.Personal.CurrentRevision with { CalculationResult = result } };
        Assert.Equal(result, Assert.Single((await Card(f)).Indicators).Value);
    }

    [Fact]
    public async Task ObservedExactBudgetExcerptProducesBudgetDeltaAndPreservesBaseline()
    {
        var f = new PublicInsightReadFixture();
        var r = f.Personal!.CurrentRevision;
        const string excerpt = "budget:2027 object L-123: amount 100 RUB";
        f.Materials[("own", "v1")] = f.Materials[("own", "v1")] with { Text = excerpt };
        f.Personal = f.Personal with
        {
            CurrentRevision = r with
            {
                Question = r.Question! with { Frame = "budget_delta", BaselinePeriod = "budget:2027" },
                Assessment = r.Assessment! with { Conditions = [new(excerpt, "budget_baseline", "supported", [r.SourceRefs[1]], "budget", "observed", [])] }
            }
        };
        var indicator = Assert.Single((await Card(f)).Indicators);
        Assert.Equal("budget:2027", indicator.BaselinePeriod);
        Assert.Equal("available", (await Card(f)).IndicatorAvailability.Reason);
    }
}
