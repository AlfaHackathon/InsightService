using System.Net;
using AlfaHackaton.Result;
using InsightService.Contracts.Analyses;
using InsightService.Contracts.Questions;
using InsightService.Contracts.SourceReactions;
using InsightService.Models.Persistence;
using InsightService.Repositories.Interfaces;
using InsightService.Services;
using Xunit;

// Existing synthetic sources and in-memory repository only; no PostgreSQL or model.
public sealed class CalculationQuestionContractTests
{
    private readonly PreservationFixture fixture = PreservationFixture.Load();
    private AnalysisRequest Request()
    {
        var request = fixture.First;
        var draft = request.Drafts[0];
        return request with
        {
            Drafts = [draft with
            {
                Question = draft.Question! with { Frame = "event_only", Unit = "RUB", TargetPeriod = "2027", BaselinePeriod = null },
                Impact = draft.Impact! with { AmountBasis = "contract_total" }
            }]
        };
    }

    [Theory]
    [InlineData("period")]
    [InlineData("unit")]
    [InlineData("empty_conclusion")]
    public async Task PreparationRejectsTheSameInvalidFullDraftAsSaveWithoutWritingOrRewritingIt(string defect)
    {
        var request = Request();
        var draft = request.Drafts[0];
        draft = defect switch
        {
            "period" => draft with { Calculation = draft.Calculation! with { Period = "2027-01-01/2028-01-01" } },
            "unit" => draft with { Calculation = draft.Calculation! with { Unit = "USD" } },
            _ => draft with { Conclusion = "" }
        };
        request = request with { Drafts = [draft] };
        var before = System.Text.Json.JsonSerializer.Serialize(request, AnalysisService.Json);
        var repository = new EmptyRepository();
        var preparation = new AnalysisPreparationService(repository, null!, fixture, null!, null!);
        var prepared = await ResultAssert.ErrorAsync(() => preparation.PrepareAsync(new() { Request = request, Drafts = request.Drafts }));
        var saved = await ResultAssert.ErrorAsync(() => new AnalysisService(repository, fixture).Save(request));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, prepared.HttpStatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, saved.HttpStatusCode);
        Assert.Equal(saved.Title, prepared.Title);
        Assert.Equal(before, System.Text.Json.JsonSerializer.Serialize(request, AnalysisService.Json));
        Assert.False(repository.SaveCalled);
    }

    [Fact]
    public async Task ExactPeriodAndNullBaselinePrepareWithoutChangingTheModelCalculationOrConclusion()
    {
        var request = Request();
        var repository = new EmptyRepository();
        var prepared = await new AnalysisPreparationService(repository, null!, fixture, null!, null!)
            .PrepareAsync(new() { Request = request, Drafts = request.Drafts }).RequireContentAsync();
        var result = Assert.Single(prepared.Drafts);
        Assert.Equal("2027", result.Calculation!.Period);
        Assert.Equal(request.Drafts[0].Calculation, result.Calculation);
        Assert.Equal(request.Drafts[0].Question, result.Question);
        Assert.Null(result.Question!.BaselinePeriod);
        Assert.Equal(request.Drafts[0].Conclusion, result.Conclusion);
        Assert.Null(result.ModelExplanation);
        Assert.False(repository.SaveCalled);
    }

    [Fact]
    public async Task QualitativeUnknownRemainsWithoutCalculationAndKeepsItsMissingFacts()
    {
        var draft = fixture.Second.Drafts[0];
        draft = draft with
        {
            Calculation = null,
            Question = draft.Question! with { Frame = "event_only", Unit = "qualitative", TargetPeriod = "unknown", BaselinePeriod = null, Basis = "qualitative" }
        };
        var request = fixture.Second with { Drafts = [draft] };
        var result = await new AnalysisPreparationService(new EmptyRepository(), null!, fixture, null!, null!)
            .PrepareAsync(new() { Request = request, Drafts = request.Drafts }).RequireContentAsync();
        var prepared = Assert.Single(result.Drafts);
        Assert.Null(prepared.Calculation);
        Assert.Equal("unknown", prepared.Question!.TargetPeriod);
        Assert.Null(prepared.Question.BaselinePeriod);
        Assert.Equal(draft.Assessment!.Unknowns, prepared.Assessment!.Unknowns);
    }

    private sealed class EmptyRepository : IEffectRepository
    {
        public bool SaveCalled { get; private set; }
        public Task<StoredAnalysis?> FindAnalysis(Guid companyId, string materialId, string versionId, MaterialTrigger? trigger = null) => Task.FromResult<StoredAnalysis?>(null);
        public Task<IReadOnlyList<EffectResponse>> GetEffects(Guid companyId, string materialId) => Task.FromResult<IReadOnlyList<EffectResponse>>([]);
        public Task<EffectSnapshot> GetSnapshot(Guid companyId, string materialId, DateTimeOffset? asOf = null) => Task.FromResult(new EffectSnapshot([], 0));
        public Task<EffectResponse?> GetEffect(string effectId) => Task.FromResult<EffectResponse?>(null);
        public Task<Result<RecipientPage>> Recipients(DateTimeOffset asOf, string? after, string? materialId) => throw new NotSupportedException();
        public Task<Result<ReactionPage>> React(SourceChangeRequest change) => throw new NotSupportedException();
        public Task<Result<AnalysisReceipt>> Save(AnalysisRequest request, string hash, IReadOnlyList<PreparedDraft> drafts)
        {
            SaveCalled = true;
            throw new InvalidOperationException("Draft preparation must not write.");
        }
    }
}
