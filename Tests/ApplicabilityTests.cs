using InsightService.Contracts.Analyses;
using InsightService.Contracts.Calculations;
using InsightService.Contracts.Context;
using InsightService.Models;
using InsightService.Repositories;
using InsightService.Services;
using InsightService.Services.Interfaces;
using InsightService;
using Xunit;

public sealed class ApplicabilityTests : IDisposable
{
    private readonly PgTestDatabase database = new();
    private readonly Guid company = Guid.NewGuid();
    private static readonly DateTimeOffset At = DateTimeOffset.Parse("2026-10-23T08:00:00Z");
    private const string Hash = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private AnalysisService Service() => new(new EffectRepository(database.Context), new Sources(company));
    private AnalysisRequest Request() => new(company, "U_1", "news", "1", At,
        [new("q", "company:" + company, null, "The company impact remains conditional", "conditional",
            [new("news", "1", Hash), new("profile", "1", Hash)], Impact: new("risk", "qualitative", null, "conditional", 1, 1, 1),
            Question: new("financing_cost", "event_only", "qualitative", "unknown", null, "qualitative"),
            Assessment: new([], [new("Actual exposure is unknown", null, "company", "profile")]))]);

    [Fact]
    public async Task ConditionalWithoutEstablishedCompanyExposureCannotBeSaved()
    {
        var error = await Assert.ThrowsAsync<ApiException>(async () => await Service().Save(Request()));
        Assert.Equal("applicability_not_established", error.Code);
        Assert.Empty((await Service().GetEffects(company, "news")));
    }

    [Fact]
    public async Task ProfileSignalStillCannotBePromotedByCallingItEstablished()
    {
        var request = Request(); var draft = request.Drafts[0];
        request = request with
        {
            Drafts = [draft with { Assessment = draft.Assessment! with
        { Applicability = new("established", "documented_exposure", "Claimed profile exposure",
            [new("profile", "1", Hash)], ["Hypothetical future rate"]) } }]
        };
        Assert.Equal("applicability_not_established", (await Assert.ThrowsAsync<ApiException>(async () => await Service().Save(request))).Code);
    }

    [Fact]
    public async Task DocumentedExposureWithExplicitScenarioIsSavedAsScenarioNotConfirmedConsequence()
    {
        var request = Request(); var draft = request.Drafts[0];
        request = request with
        {
            Drafts = [draft with { SourceRefs = [new("news", "1", Hash), new("contract", "1", Hash)],
            Assessment = new([], [], new("established", "documented_exposure", "Own document establishes exposure",
                [new("contract", "1", Hash)], ["Future public index follows this scenario"])) }]
        };
        var saved = Assert.Single((await Service().Save(request)).Effects);
        Assert.Equal("scenario", saved.CurrentRevision.PublicationClass);
        Assert.Equal("conditional", saved.CurrentRevision.Impact!.EvidenceStatus);
        Assert.Null(saved.CurrentRevision.CalculationResult);
    }

    [Fact]
    public async Task UnknownIsPersistedAsMissingDataAndNotAsZero()
    {
        var request = Request(); var draft = request.Drafts[0];
        request = request with { Drafts = [draft with { Status = "unknown", Impact = draft.Impact! with { EvidenceStatus = "unknown" } }] };
        var saved = Assert.Single((await Service().Save(request)).Effects);
        Assert.Equal("missing_data", saved.CurrentRevision.PublicationClass);
        Assert.Null(saved.CurrentRevision.CalculationResult);
    }

    [Fact]
    public async Task MissingImpactCannotBypassUnknownCalculationGuard()
    {
        var request = Request();
        request = request with { Drafts = [request.Drafts[0] with { Impact = null, Question = new("financing_cost", "absolute", "RUB", "2026", null, "actual_cashflow"), Calculation = new(new("value", "0"), "RUB", "2026") }] };
        Assert.Equal("unknown_calculation", (await Assert.ThrowsAsync<ApiException>(async () => await Service().Save(request))).Code);
    }

    private sealed class Sources(Guid company) : ISourceResolver
    {
        public Task<MaterialVersion> Get(string materialId, string versionId, CancellationToken ct) =>
            Task.FromResult(new MaterialVersion(materialId, versionId,
                materialId == "profile" ? "provided-bank-csv" : "fixture", materialId == "news" ? "news" : "company",
                materialId, "Synthetic source fixture", At, At, null, materialId == "news" ? [] : [company], [], Hash));
    }
    public void Dispose() => database.Dispose();
}
