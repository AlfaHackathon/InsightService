using System.Net;
using AlfaHackaton.Result;
using InsightService.Contracts.Analyses;
using InsightService.Contracts.Questions;
using InsightService.Contracts.SourceReactions;
using InsightService.Models.Persistence;
using InsightService.Models.Policies;
using InsightService.Repositories.Interfaces;
using InsightService.Services;
using Xunit;

public sealed class UnpublishedAnalysisTests
{
    [Fact]
    public async Task IndependentUnknownCompletesInternallyWithoutClaimingReplacementOfTheGoodHead()
    {
        var fixture = PreservationFixture.Load();
        var repo = new DiagnosticsRepository(fixture.Existing);
        var input = fixture.Second;
        var prepared = await new AnalysisPreparationService(repo, null!, fixture, null!, null!)
            .PrepareAsync(new() { Request = input, Drafts = input.Drafts }).RequireContentAsync();
        Assert.Single(prepared.Drafts);
        var result = await new AnalysisService(repo, fixture).Save(input with { Drafts = prepared.Drafts }).RequireContentAsync();
        Assert.Equal("no_insight", result.Outcome);
        Assert.Empty(result.Effects);
        var diagnostic = Assert.Single(result.UnpublishedDrafts!);
        Assert.Equal(input.Drafts[0].Conclusion, diagnostic.Conclusion);
        Assert.Equal(input.Drafts[0].Assessment!.Unknowns, diagnostic.Assessment!.Unknowns);
        Assert.Null(diagnostic.Calculation);
        Assert.Equal(fixture.Existing, repo.Head);
        Assert.True((await new AnalysisService(repo, fixture).Save(input with { Drafts = prepared.Drafts }).RequireContentAsync()).Duplicate);
        Assert.Equal(1, repo.ReceiptWrites);
    }

    [Fact]
    public async Task AnInvalidConditionalOrSourceDoesNotBecomeNoInsight()
    {
        var fixture = PreservationFixture.Load();
        var input = fixture.Second;
        var draft = input.Drafts[0];
        var assessment = draft.Assessment!;
        draft = draft with { Assessment = assessment with { Conditions = [.. assessment.Conditions,
            new("Scenario without established exposure", "scenario_delta", "unknown", [], "invalid_scenario", "conditional", [assessment.Unknowns[0].Id!])] } };
        var repo = new DiagnosticsRepository(fixture.Existing);
        var error = await ResultAssert.ErrorAsync(() => new AnalysisService(repo, fixture).Save(input with { Drafts = [draft] }));
        Assert.True(error.HttpStatusCode == HttpStatusCode.UnprocessableEntity, error.Title);
        Assert.Equal(0, repo.ReceiptWrites);
        draft = input.Drafts[0] with { SourceRefs = input.Drafts[0].SourceRefs.Select(r => r with { ContentHash = new string('f', 64) }).ToArray() };
        error = await ResultAssert.ErrorAsync(() => new AnalysisService(repo, fixture).Save(input with { Drafts = [draft] }));
        Assert.Equal(HttpStatusCode.BadRequest, error.HttpStatusCode);
        Assert.Equal(0, repo.ReceiptWrites);
    }

    [Fact]
    public void DiagnosticPartitionDoesNotHideContradictionsOrValidConsequences()
    {
        var draft = PreservationFixture.Load().Second.Drafts[0];
        Assert.True(AnalysisPublicationPolicy.IsDiagnostic(draft));
        Assert.False(AnalysisPublicationPolicy.IsDiagnostic(draft with { Impact = draft.Impact! with { EvidenceStatus = "contradicted" } }));
        Assert.False(AnalysisPublicationPolicy.IsDiagnostic(PreservationFixture.Load().CurrentFirst.Drafts[0]));
        Assert.True(AnalysisPublicationPolicy.IsDiagnostic(draft with { Impact = null }));
        Assert.True(AnalysisPublicationPolicy.IsDiagnostic(draft with { Question = null, Impact = null }));
    }

    [Fact]
    public async Task GroundedQualitativeConsequenceStillPassesWithoutCalculationOrClaim()
    {
        var fixture = PreservationFixture.Load();
        var input = fixture.CurrentFirst;
        var draft = input.Drafts[0] with
        {
            Conclusion = "Условия договора требуют внимания: проверьте применимость объявленного изменения.",
            Calculation = null, CalculationClaim = null,
            Question = new("financing_cost", "event_only", "qualitative", "unknown", null, "qualitative")
        };
        Assert.False(AnalysisPublicationPolicy.IsDiagnostic(draft));
        var repo = new CalculationNarrativeServiceTests.MemoryRepository();
        Assert.Null((await new AnalysisService(repo, fixture).Save(input with { Drafts = [draft] })).Error);
        Assert.Equal(draft.Conclusion, Assert.Single(repo.Saved!.Drafts).Conclusion);
        Assert.Null(repo.Saved.Drafts[0].Calculation);
        Assert.Null(repo.Saved.Drafts[0].CalculationClaim);
    }

    [Fact]
    public void MixedBatchRetainsKnownSubtotalButCannotClaimCompleteGroupCoverage()
    {
        var revision = PublicInsightReadFixture.Revision();
        revision = revision with
        {
            Impact = revision.Impact! with { EvidenceStatus = "supported" },
            Question = revision.Question! with { Basis = "contract_total" }
        };
        var company = PublicInsightReadFixture.Company;
        var receipt = new AnalysisReceipt("mixed", company, null, "news", "v1", revision.AsOf, revision.CreatedAt,
            false, AnalysisService.ReceiptSemantics, [new("effect", revision.RevisionId, null, false, revision)], ContextComplete: true, Outcome: "insights_saved");
        var group = new InsightService.Contracts.Projections.GroupExpansion
        { GroupId = "synthetic-group", CompanyIds = [company], Complete = true, AggregationRule = "sum_compatible_owner_effects" };
        var before = ProjectionPolicy.Group(group, [new() { CompanyId = company, Status = "completed", Receipt = receipt, ContextComplete = true }]);
        Assert.True(before.Complete);
        Assert.Equal(revision.CalculationResult, Assert.Single(before.Totals).FullTotal);
        receipt = receipt with { UnpublishedDrafts = [PreservationFixture.Load().Second.Drafts[0]] };
        var result = ProjectionPolicy.Group(group, [new() { CompanyId = company, Status = "completed", Receipt = receipt, ContextComplete = true }]);
        Assert.False(result.Complete);
        Assert.Null(Assert.Single(result.Totals).FullTotal);
        Assert.Equal(revision.CalculationResult, result.Totals[0].KnownSubtotal);
        Assert.Equal([company], result.Coverage.UnassessedCompanyIds);
        Assert.Empty(result.Coverage.PartialContextCompanyIds);
        Assert.True(receipt.ContextComplete); // Retrieval coverage and assessment completeness differ.
    }

    private sealed class DiagnosticsRepository(EffectResponse head) : IEffectRepository
    {
        public EffectResponse Head => head;
        public int ReceiptWrites;
        private StoredAnalysis? prior;
        public Task<StoredAnalysis?> FindAnalysis(Guid companyId, string materialId, string versionId, MaterialTrigger? trigger = null) => Task.FromResult(prior);
        public Task<IReadOnlyList<EffectResponse>> GetEffects(Guid companyId, string materialId) => Task.FromResult<IReadOnlyList<EffectResponse>>([head]);
        public Task<EffectSnapshot> GetSnapshot(Guid companyId, string materialId, DateTimeOffset? asOf = null) => Task.FromResult(new EffectSnapshot([head], 1));
        public Task<EffectResponse?> GetEffect(string effectId) => Task.FromResult<EffectResponse?>(head);
        public Task<Result<RecipientPage>> Recipients(DateTimeOffset asOf, string? after, string? materialId) => throw new NotSupportedException();
        public Task<Result<ReactionPage>> React(SourceChangeRequest change) => throw new NotSupportedException();
        public Task<Result<AnalysisReceipt>> Save(AnalysisRequest request, string hash, IReadOnlyList<PreparedDraft> drafts)
        {
            Assert.All(drafts, item => Assert.True(AnalysisPublicationPolicy.IsDiagnostic(item.Draft)));
            var receipt = new AnalysisReceipt("internal-receipt", request.CompanyId, request.BankProfileId, request.NewsMaterialId,
                request.NewsVersionId, request.AsOf, request.AsOf, false, AnalysisService.ReceiptSemantics, [],
                request.ContextComplete, [head.EffectId], request.RetrievalTrace, request.Trigger, "no_insight", drafts.Select(x => x.Draft).ToArray());
            prior = new(hash, receipt);
            ReceiptWrites++;
            return Task.FromResult(new Result<AnalysisReceipt>(receipt, null));
        }
    }
}
