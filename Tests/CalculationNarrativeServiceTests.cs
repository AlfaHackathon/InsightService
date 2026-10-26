using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AlfaHackaton.Result;
using InsightService.Contracts.Analyses;
using InsightService.Contracts.Questions;
using InsightService.Contracts.SourceReactions;
using InsightService.Models.Persistence;
using InsightService.Repositories.Interfaces;
using InsightService.Services;
using InsightService.Services.Interfaces;
using Xunit;

public sealed class CalculationNarrativeServiceTests
{
    [Theory]
    [InlineData("correct")]
    [InlineData("wrong_value")]
    [InlineData("missing_claim")]
    [InlineData("wrong_quote")]
    [InlineData("wrong_unit")]
    [InlineData("wrong_period")]
    public async Task PrepareAndSaveEnforceTheSameLiteralClaimWithoutRewriting(string shape)
    {
        var fixture = PreservationFixture.Load();
        var request = fixture.CurrentFirst;
        var draft = request.Drafts[0];
        var claim = draft.CalculationClaim!;
        draft = shape switch
        {
            "wrong_value" => draft with { Conclusion = "Расходы: 800 RUB в 2027 году.", CalculationClaim = claim with { Quote = "800 RUB", Value = "800" } },
            "missing_claim" => draft with { CalculationClaim = null },
            "wrong_quote" => draft with { CalculationClaim = claim with { Quote = "10000 RUB" } },
            "wrong_unit" => draft with { Conclusion = "Расходы: 20000 USD в 2027 году.", CalculationClaim = claim with { Quote = "20000 USD", Unit = "USD" } },
            "wrong_period" => draft with { CalculationClaim = claim with { Period = "2028" } },
            _ => draft
        };
        request = request with { Drafts = [draft] };
        var repo = new MemoryRepository();
        var prepared = await new AnalysisPreparationService(repo, null!, fixture, null!, null!)
            .PrepareAsync(new() { Request = request, Drafts = request.Drafts });
        var saved = await new AnalysisService(repo, fixture).Save(request);
        if (shape == "correct")
        {
            Assert.Null(prepared.Error);
            Assert.Null(saved.Error);
            Assert.Equal(draft.Conclusion, Assert.Single(prepared.Content!.Drafts).Conclusion);
            Assert.Equal(draft.Conclusion, Assert.Single(repo.Saved!.Drafts).Conclusion);
            Assert.Equal(draft.CalculationClaim, Assert.Single(repo.Prepared!).Draft.CalculationClaim);
            Assert.Equal("20000", Assert.Single(repo.Prepared!).CalculationResult);
        }
        else
        {
            Assert.Equal(HttpStatusCode.UnprocessableEntity, saved.Error!.HttpStatusCode);
            Assert.Equal(saved.Error.Title, prepared.Error!.Title);
            Assert.StartsWith("CONCLUSION_", saved.Error.Title);
            Assert.Null(repo.Saved);
        }
        Assert.Equal(draft.Conclusion, request.Drafts[0].Conclusion);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ExactHistoricalHashReplaysWithoutNewClaimPolicyOrSourceAcquisition(bool normalizeQuestions)
    {
        var fixture = PreservationFixture.Load();
        var request = fixture.First;
        var historical = AnalysisRequestNormalizer.Normalize(request, normalizeQuestions, legacyCalculatedConclusion: true).RequireContent().Item1;
        var hash = Hash(historical);
        var receipt = Receipt(historical);
        var repo = new MemoryRepository { Prior = new(hash, receipt) };
        var result = await new AnalysisService(repo, new ForbiddenSources()).Save(request).RequireContentAsync();
        Assert.True(result.Duplicate);
        Assert.Equal(receipt.AnalysisId, result.AnalysisId);
        Assert.Null(repo.Saved);
        // A new field or a changed input must not bypass immutable receipt identity.
        var changed = request with { Drafts = [request.Drafts[0] with { CalculationClaim = new("20000 RUB", "20000", "RUB", "2027") }] };
        var conflict = await ResultAssert.ErrorAsync(() => new AnalysisService(repo, new ForbiddenSources()).Save(changed));
        Assert.Equal(HttpStatusCode.Conflict, conflict.HttpStatusCode);
        changed = request with { Drafts = [request.Drafts[0] with { Conclusion = "Different raw prose" }] };
        Assert.Equal(HttpStatusCode.Conflict, (await ResultAssert.ErrorAsync(() => new AnalysisService(repo, new ForbiddenSources()).Save(changed))).HttpStatusCode);
    }

    [Fact]
    public async Task OldUnclaimedPayloadCannotUseLegacyHashNormalizationToCreateNewWrite()
    {
        var fixture = PreservationFixture.Load();
        var repo = new MemoryRepository();
        var error = await ResultAssert.ErrorAsync(() => new AnalysisService(repo, fixture).Save(fixture.First));
        Assert.StartsWith("CONCLUSION_CLAIM_REQUIRED:", error.Title);
        Assert.Null(repo.Saved);
    }

    [Fact]
    public async Task PreCanonicalQualitativeReceiptUsesTheDistinctFalseModeHashWithoutReadingSources()
    {
        var draft = CalculationNarrativeTests.Draft("Исторический качественный вывод") with
        {
            Calculation = null,
            Question = new("financing_cost", "event_only", "qualitative", "2027", "2026", "annual_run_rate")
        };
        var request = CalculationNarrativeTests.Request(draft);
        var raw = AnalysisRequestNormalizer.Normalize(request, false).RequireContent().Item1;
        var canonical = AnalysisRequestNormalizer.Normalize(request).RequireContent().Item1;
        Assert.NotEqual(Hash(raw), Hash(canonical));
        var repo = new MemoryRepository { Prior = new(Hash(raw), Receipt(raw)) };
        Assert.True((await new AnalysisService(repo, new ForbiddenSources()).Save(request).RequireContentAsync()).Duplicate);
        Assert.Null(repo.Saved);
    }

    private static string Hash(AnalysisRequest request) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(request, AnalysisService.Json))));
    private static AnalysisReceipt Receipt(AnalysisRequest request) => new("synthetic-receipt", request.CompanyId, request.BankProfileId,
        request.NewsMaterialId, request.NewsVersionId, request.AsOf, request.AsOf, false, AnalysisService.ReceiptSemantics, []);
    private sealed class ForbiddenSources : ISourceResolver
    {
        public Task<Result<InsightService.Contracts.Context.MaterialVersion>> GetAsync(string materialId, string versionId, CancellationToken ct) => throw new InvalidOperationException("Replay must not acquire any source.");
    }
    internal sealed class MemoryRepository : IEffectRepository
    {
        public StoredAnalysis? Prior;
        public EffectSnapshot Snapshot = new([], 0);
        public AnalysisRequest? Saved;
        public IReadOnlyList<PreparedDraft>? Prepared;
        public Task<StoredAnalysis?> FindAnalysis(Guid companyId, string materialId, string versionId, MaterialTrigger? trigger = null) => Task.FromResult(Prior);
        public Task<IReadOnlyList<EffectResponse>> GetEffects(Guid companyId, string materialId) => Task.FromResult<IReadOnlyList<EffectResponse>>([]);
        public Task<EffectSnapshot> GetSnapshot(Guid companyId, string materialId, DateTimeOffset? asOf = null) => Task.FromResult(Snapshot);
        public Task<EffectResponse?> GetEffect(string effectId) => throw new NotSupportedException();
        public Task<Result<RecipientPage>> Recipients(DateTimeOffset asOf, string? after, string? materialId) => throw new NotSupportedException();
        public Task<Result<ReactionPage>> React(SourceChangeRequest change) => throw new NotSupportedException();
        public Task<Result<AnalysisReceipt>> Save(AnalysisRequest request, string hash, IReadOnlyList<PreparedDraft> drafts)
        {
            Saved = request;
            Prepared = drafts;
            return Task.FromResult(new Result<AnalysisReceipt>(Receipt(request), null));
        }
    }
}
