using InsightService.Contracts.Analyses;
using InsightService.Contracts.Projections;
using InsightService.Services;
using Xunit;

public sealed class CalculationNarrativeProjectionTests
{
    [Fact]
    public async Task NoInsightAnalysisDoesNotTurnPreviousGoodHeadsIntoNewActionableFacts()
    {
        var revision = PublicInsightReadFixture.Revision();
        var company = PublicInsightReadFixture.Company;
        var diagnostic = PreservationFixture.Load().Second.Drafts[0];
        var receipt = new AnalysisReceipt("diagnostic", company, null, "news", "v1", revision.AsOf, revision.CreatedAt,
            false, AnalysisService.ReceiptSemantics, [], Outcome: "no_insight", UnpublishedDrafts: [diagnostic]);
        var repo = new CalculationNarrativeServiceTests.MemoryRepository
        {
            Prior = new("synthetic-hash", receipt),
            Snapshot = new([new("effect", "key", company, null, "news", "object", revision, [revision])], 0)
        };
        var result = await new ProjectionService(repo, null!, null!).CanonicalAsync(new()
        {
            Companies = [new() { CompanyId = company, Receipt = receipt }], AsOf = revision.AsOf
        }).RequireContentAsync();
        Assert.Empty(result.Facts);
        var projected = Assert.Single(result.Companies).Receipt!;
        Assert.Empty(projected.Effects);
        Assert.Equal("no_insight", projected.Outcome);
        Assert.Equal(diagnostic, Assert.Single(projected.UnpublishedDrafts!));
        Assert.Single(repo.Snapshot.Effects); // Not deleted, promoted or rewritten.
    }

    [Fact]
    public async Task CanonicalProjectionPreservesPersistedProseInsteadOfRenderingAnotherTemplate()
    {
        var revision = PublicInsightReadFixture.Revision() with { Conclusion = "Расходы могут вырасти; проверьте условия договора.", ModelExplanation = "Private audit" };
        var company = PublicInsightReadFixture.Company;
        var receipt = new AnalysisReceipt("receipt", company, null, "news", "v1", revision.AsOf, revision.CreatedAt,
            false, AnalysisService.ReceiptSemantics, [new("effect", revision.RevisionId, null, false, revision)]);
        var repo = new CalculationNarrativeServiceTests.MemoryRepository
        {
            Prior = new("synthetic-hash", receipt),
            Snapshot = new([new("effect", "key", company, null, "news", "object", revision, [revision])], 0)
        };
        var result = await new ProjectionService(repo, null!, null!).CanonicalAsync(new()
        {
            Companies = [new() { CompanyId = company, Receipt = receipt }],
            AsOf = revision.AsOf
        }).RequireContentAsync();
        var fact = Assert.Single(result.Facts);
        Assert.Equal(revision.Conclusion, fact.Revision.Conclusion);
        Assert.Equal(revision.CalculationResult, fact.Revision.CalculationResult);
        Assert.Null(fact.Revision.ModelExplanation);
        Assert.Equal("Private audit", revision.ModelExplanation);
    }
}
