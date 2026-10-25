using System.Text.Json;
using AlfaHackaton.Result;
using InsightService.Contracts.Analyses;
using InsightService.Contracts.Context;
using InsightService.Contracts.Questions;
using InsightService.Contracts.SourceReactions;
using InsightService.Models.Persistence;
using InsightService.Repositories;
using InsightService.Repositories.Interfaces;
using InsightService.Services;
using InsightService.Services.Interfaces;
using Xunit;

// Replays the two captured final tool results, without a model or external service.
public sealed class ReassessmentScopeTests
{
    private readonly PreservationFixture fixture = PreservationFixture.Load();

    [Fact]
    public async Task UnboundUnknownOnIndependentNewsDoesNotPrepareReplacementForIdentifiedConsequence()
    {
        var repository = new ExistingQuestion(fixture.Existing);
        var result = await Prepare(repository, fixture.Second);
        Assert.Empty(result.Drafts);
        Assert.Equal("20000", (await repository.GetEffect(fixture.Existing.EffectId))!.CurrentRevision.CalculationResult);
    }

    [Fact]
    public async Task DirectWriteCannotBypassReassessmentScopeCheck()
    {
        var repository = new ExistingQuestion(fixture.Existing);
        var error = await ResultAssert.ErrorAsync(() => new AnalysisService(repository, fixture).Save(fixture.Second));
        Assert.Equal(422, (int)error.HttpStatusCode!);
        Assert.Contains("reassessment scope", error.Title);
        Assert.False(repository.SaveCalled);
    }

    [Fact]
    public async Task OmittedImpactRetainsUnknownSemanticsInPreparationAndDirectWrite()
    {
        var repository = new ExistingQuestion(fixture.Existing);
        var request = fixture.Second with { Drafts = [fixture.Second.Drafts[0] with { Impact = null }] };
        Assert.Empty((await Prepare(repository, request)).Drafts);
        Assert.Equal(422, (int)(await ResultAssert.ErrorAsync(() => new AnalysisService(repository, fixture).Save(request))).HttpStatusCode!);
        Assert.False(repository.SaveCalled);
    }

    [Theory]
    [InlineData("older", null)]
    [InlineData("sibling", "older")]
    public async Task OlderOrSiblingDependencyCannotAuthorizeUnboundReplacement(string version, string? corrects)
    {
        var source = fixture.First.Drafts[0].SourceRefs.First(r => r.MaterialId != fixture.First.NewsMaterialId);
        fixture.AddVersion(source.MaterialId, "older");
        fixture.AddVersion(source.MaterialId, version, corrects);
        var request = fixture.Second with
        {
            Trigger = new(source.MaterialId, version),
            Drafts = [fixture.Second.Drafts[0] with { SourceRefs = [.. fixture.Second.Drafts[0].SourceRefs, source with { VersionId = version }] }]
        };
        var repository = new ExistingQuestion(fixture.Existing);
        Assert.Empty((await Prepare(repository, request)).Drafts);
        Assert.Equal(422, (int)(await ResultAssert.ErrorAsync(() => new AnalysisService(repository, fixture).Save(request))).HttpStatusCode!);
        Assert.False(repository.SaveCalled);
    }

    [Fact]
    public async Task NewQualitativeUnknownScopeDoesNotInheritMonetaryQuestion()
    {
        var unknown = fixture.Second.Drafts[0] with
        {
            Question = new("financing_cost", "event_only", "qualitative", "unknown", null, "qualitative"),
            ExpectedPreviousRevisionId = null
        };
        var prepared = await Prepare(new ExistingQuestion(fixture.Existing), fixture.Second with { Drafts = [unknown] });
        Assert.Null(Assert.Single(prepared.Drafts).ExpectedPreviousRevisionId);
    }

    [Theory]
    [InlineData("same_object")]
    [InlineData("news_correction")]
    [InlineData("source_trigger")]
    public async Task LinkedUnknownCanStillReassessTheExistingQuestion(string relation)
    {
        var request = fixture.Second;
        var draft = request.Drafts[0];
        var prior = fixture.Existing.CurrentRevision;
        var dependency = fixture.First.Drafts[0].SourceRefs.First(r => r.MaterialId != fixture.First.NewsMaterialId);
        fixture.AddVersion(dependency.MaterialId, "corrected", "v1");
        fixture.AddVersion(fixture.First.NewsMaterialId, "correction", "v1");
        var oldNews = fixture.First.Drafts[0].SourceRefs.Single(r => r.MaterialId == fixture.First.NewsMaterialId);
        request = relation switch
        {
            "same_object" => request with { Drafts = [draft with { Impact = draft.Impact! with { EconomicObjectId = prior.Impact!.EconomicObjectId } }] },
            "news_correction" => request with { NewsMaterialId = fixture.First.NewsMaterialId, NewsVersionId = "correction", Drafts = [draft with { SourceRefs = [.. draft.SourceRefs, oldNews with { VersionId = "correction" }] }] },
            "source_trigger" => request with { Trigger = new(dependency.MaterialId, "corrected"), Drafts = [draft with { SourceRefs = [.. draft.SourceRefs, dependency with { VersionId = "corrected" }] }] },
            _ => throw new InvalidOperationException()
        };
        var prepared = await Prepare(new ExistingQuestion(fixture.Existing), request);
        Assert.Equal(prior.RevisionId, Assert.Single(prepared.Drafts).ExpectedPreviousRevisionId);
    }

    [Fact]
    public async Task UnrelatedTriggerAndOldSharedSourcesDoNotEstablishReassessmentScope()
    {
        var request = fixture.Second with { Trigger = new("unrelated-material", "v2") };
        Assert.Empty((await Prepare(new ExistingQuestion(fixture.Existing), request)).Drafts);
    }

    [Fact]
    public async Task UnchangedDependencyTriggerDoesNotEstablishReassessmentScope()
    {
        var source = fixture.Existing.CurrentRevision.SourceRefs.Last();
        var request = fixture.Second with { Trigger = new(source.MaterialId, source.VersionId) };
        Assert.Empty((await Prepare(new ExistingQuestion(fixture.Existing), request)).Drafts);
    }

    [Fact]
    public async Task DifferentObjectAndCompanyExposureDoNotEstablishSameObjectScope()
    {
        var draft = fixture.Second.Drafts[0];
        var request = fixture.Second with
        {
            Drafts = [draft with
        {
            Impact = draft.Impact! with { EconomicObjectId = "another-owned-test-obligation" },
            Assessment = draft.Assessment! with { Applicability = fixture.Existing.CurrentRevision.Assessment!.Applicability }
        }]
        };
        Assert.Empty((await Prepare(new ExistingQuestion(fixture.Existing), request)).Drafts);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnchangedOwnNewsWithUnrelatedTriggerCannotReplaceHead(bool legacyMetadata)
    {
        var existing = fixture.Existing;
        if (legacyMetadata)
        {
            var revision = existing.CurrentRevision with { NewsMaterialId = null };
            existing = existing with { CurrentRevision = revision, History = [revision] };
        }
        var news = fixture.First.Drafts[0].SourceRefs.Single(r => r.MaterialId == fixture.First.NewsMaterialId);
        var request = fixture.First with
        {
            AsOf = fixture.Second.AsOf,
            Trigger = new(fixture.Second.NewsMaterialId, fixture.Second.NewsVersionId),
            Drafts = [fixture.Second.Drafts[0] with { SourceRefs = [.. fixture.Second.Drafts[0].SourceRefs, news] }]
        };
        var repository = new ExistingQuestion(existing);
        Assert.Empty((await Prepare(repository, request)).Drafts);
        Assert.Equal(422, (int)(await ResultAssert.ErrorAsync(() => new AnalysisService(repository, fixture).Save(request))).HttpStatusCode!);
        Assert.False(repository.SaveCalled);
    }

    [Fact]
    public async Task LegacyNullableNewsMetadataStillRequiresActualCorrection()
    {
        var revision = fixture.Existing.CurrentRevision with { NewsMaterialId = null };
        var existing = fixture.Existing with { CurrentRevision = revision, History = [revision] };
        fixture.AddVersion(fixture.First.NewsMaterialId, "uncorrected");
        var news = fixture.First.Drafts[0].SourceRefs.Single(r => r.MaterialId == fixture.First.NewsMaterialId);
        var request = fixture.Second with
        {
            NewsMaterialId = fixture.First.NewsMaterialId,
            NewsVersionId = "uncorrected",
            Drafts = [fixture.Second.Drafts[0] with { SourceRefs = [.. fixture.Second.Drafts[0].SourceRefs, news with { VersionId = "uncorrected" }] }]
        };
        var repository = new ExistingQuestion(existing);
        Assert.Empty((await Prepare(repository, request)).Drafts);
        Assert.Equal(422, (int)(await ResultAssert.ErrorAsync(() => new AnalysisService(repository, fixture).Save(request))).HttpStatusCode!);
        Assert.False(repository.SaveCalled);
    }

    private async Task<DraftPreparationResponse> Prepare(IEffectRepository repository, AnalysisRequest request) =>
        await new AnalysisPreparationService(repository, null!, fixture, null!, null!).PrepareAsync(new() { Request = request, Drafts = request.Drafts }).RequireContentAsync();

    private sealed class ExistingQuestion(EffectResponse effect) : IEffectRepository
    {
        public bool SaveCalled { get; private set; }
        public Task<StoredAnalysis?> FindAnalysis(Guid companyId, string materialId, string versionId, MaterialTrigger? trigger = null) => Task.FromResult<StoredAnalysis?>(null);
        public Task<IReadOnlyList<EffectResponse>> GetEffects(Guid companyId, string materialId) => Task.FromResult<IReadOnlyList<EffectResponse>>([effect]);
        public Task<EffectSnapshot> GetSnapshot(Guid companyId, string materialId, DateTimeOffset? asOf = null) => Task.FromResult(new EffectSnapshot([effect], 1));
        public Task<EffectResponse?> GetEffect(string effectId) => Task.FromResult<EffectResponse?>(effect);
        public Task<Result<RecipientPage>> Recipients(DateTimeOffset asOf, string? after, string? materialId) => throw new NotSupportedException();
        public Task<Result<ReactionPage>> React(SourceChangeRequest change) => throw new NotSupportedException();
        public Task<Result<AnalysisReceipt>> Save(AnalysisRequest request, string hash, IReadOnlyList<PreparedDraft> drafts)
        {
            SaveCalled = true;
            throw new InvalidOperationException("The unrelated replacement reached persistence.");
        }
    }
}

public sealed class ReassessmentScopePersistenceTests
{
    [Theory]
    [InlineData("same_object")]
    [InlineData("news_correction")]
    [InlineData("dependency_correction")]
    [InlineData("bound_contradiction")]
    public async Task LinkedReassessmentCanWithdrawSupportedConsequence(string relation)
    {
        using var database = new PgTestDatabase();
        var fixture = PreservationFixture.Load();
        var repository = new EffectRepository(database.Context);
        var service = new AnalysisService(repository, fixture);
        var first = (await service.Save(fixture.First).RequireContentAsync()).Effects.Single();
        var draft = fixture.Second.Drafts[0] with { ExpectedPreviousRevisionId = first.RevisionId };
        var request = fixture.Second;
        if (relation is "same_object" or "bound_contradiction")
        {
            draft = draft with
            {
                Impact = draft.Impact! with
                {
                    EconomicObjectId = fixture.First.Drafts[0].Impact!.EconomicObjectId,
                    EvidenceStatus = relation == "bound_contradiction" ? "contradicted" : "unknown"
                }
            };
            if (relation == "bound_contradiction")
            {
                var original = fixture.First.Drafts[0];
                var condition = original.Assessment!.Conditions.Single(c => c.Id == "loan_terms");
                var dependency = condition.SourceRefs.Single();
                fixture.AddVersion(dependency.MaterialId, "withdrawn-link", "v1",
                    "SYNTHETIC CONTROL CORRECTION: the prior named loan-to-index linkage was erroneous and is withdrawn; the prior interest consequence is contradicted.");
                var corrected = fixture.Reference(dependency.MaterialId, "withdrawn-link");
                draft = draft with
                {
                    Conclusion = "The corrected own contract contradicts the prior loan-to-index linkage; the former consequence requires review.",
                    SourceRefs = [.. draft.SourceRefs, .. original.SourceRefs, corrected],
                    Assessment = new([
                        condition with { Status = "contradicted", SourceRefs = [corrected], Fact = "The own correction withdraws the prior named loan-to-index linkage." },
                        original.Assessment.Conditions.Single(c => c.Id == "index_change"),
                        original.Assessment.Conditions.Single(c => c.Id == "incremental_interest") with { Status = "contradicted", SourceRefs = [corrected] }
                    ], [], new("established", "documented_exposure", "The corrected own contract retains the obligation but withdraws its former linkage.", [corrected], []))
                };
            }
        }
        else if (relation == "news_correction")
        {
            fixture.AddVersion(fixture.First.NewsMaterialId, "corrected", "v1");
            request = request with { NewsMaterialId = fixture.First.NewsMaterialId, NewsVersionId = "corrected" };
            var source = fixture.First.Drafts[0].SourceRefs.Single(r => r.MaterialId == fixture.First.NewsMaterialId);
            draft = draft with { SourceRefs = [.. draft.SourceRefs, source with { VersionId = "corrected" }] };
        }
        else
        {
            var dependency = fixture.First.Drafts[0].SourceRefs.First(r => r.MaterialId != fixture.First.NewsMaterialId);
            fixture.AddVersion(dependency.MaterialId, "corrected", "v1");
            request = request with { Trigger = new(dependency.MaterialId, "corrected") };
            draft = draft with { SourceRefs = [.. draft.SourceRefs, dependency with { VersionId = "corrected" }] };
        }
        request = request with { Drafts = [draft] };
        var preparation = new AnalysisPreparationService(repository, service, fixture, null!, null!);
        var prepared = await preparation.PrepareAsync(new() { Request = request, Drafts = request.Drafts }).RequireContentAsync();
        Assert.Equal(first.RevisionId, Assert.Single(prepared.Drafts).ExpectedPreviousRevisionId);
        var revision = (await service.Save(request with { Drafts = prepared.Drafts }).RequireContentAsync()).Effects.Single();
        Assert.Equal(first.EffectId, revision.EffectId);
        Assert.Equal(first.RevisionId, revision.PreviousRevisionId);
        Assert.Equal(relation == "bound_contradiction" ? "review" : "missing_data", revision.CurrentRevision.PublicationClass);
        Assert.Null(revision.CurrentRevision.CalculationResult);
        var state = (await repository.GetEffect(first.EffectId))!;
        Assert.Equal(revision.RevisionId, state.CurrentRevision.RevisionId);
        Assert.Equal(2, state.History.Count);
        Assert.Equal("20000", state.History[0].CalculationResult);
        Assert.True((await service.Save(fixture.First).RequireContentAsync()).Duplicate);
        Assert.Equal(revision.RevisionId, (await repository.GetEffect(first.EffectId))!.CurrentRevision.RevisionId);
    }

    [Theory]
    [InlineData("omitted_impact")]
    [InlineData("older_dependency")]
    [InlineData("sibling_dependency")]
    [InlineData("same_news_unrelated_trigger")]
    public async Task UnrelatedReplacementCannotAdvancePersistedHead(string bypass)
    {
        using var database = new PgTestDatabase();
        var fixture = PreservationFixture.Load();
        var repository = new EffectRepository(database.Context);
        var service = new AnalysisService(repository, fixture);
        var first = (await service.Save(fixture.First).RequireContentAsync()).Effects.Single();
        var draft = fixture.Second.Drafts[0] with { ExpectedPreviousRevisionId = first.RevisionId };
        var request = fixture.Second;
        if (bypass == "omitted_impact")
        {
            draft = draft with { Impact = null };
        }
        else if (bypass == "same_news_unrelated_trigger")
        {
            var news = fixture.First.Drafts[0].SourceRefs.Single(r => r.MaterialId == fixture.First.NewsMaterialId);
            request = request with { NewsMaterialId = fixture.First.NewsMaterialId, NewsVersionId = fixture.First.NewsVersionId, Trigger = new(fixture.Second.NewsMaterialId, fixture.Second.NewsVersionId) };
            draft = draft with { SourceRefs = [.. draft.SourceRefs, news] };
        }
        else
        {
            var dependency = fixture.First.Drafts[0].SourceRefs.First(r => r.MaterialId != fixture.First.NewsMaterialId);
            fixture.AddVersion(dependency.MaterialId, "older");
            fixture.AddVersion(dependency.MaterialId, "sibling", "older");
            var version = bypass == "older_dependency" ? "older" : "sibling";
            request = request with { Trigger = new(dependency.MaterialId, version) };
            draft = draft with { SourceRefs = [.. draft.SourceRefs, dependency with { VersionId = version }] };
        }
        request = request with { Drafts = [draft] };
        Assert.Equal(422, (int)(await ResultAssert.ErrorAsync(() => service.Save(request))).HttpStatusCode!);
        var state = (await repository.GetEffect(first.EffectId))!;
        Assert.Equal(first.RevisionId, state.CurrentRevision.RevisionId);
        Assert.Equal("20000", state.CurrentRevision.CalculationResult);
        Assert.Single(state.History);
        Assert.Null(await repository.FindAnalysis(request.CompanyId, request.NewsMaterialId, request.NewsVersionId, request.Trigger));
    }

    [Fact]
    public async Task OneBatchCanPreserveConsequenceAndSaveIndependentUnknownScope()
    {
        using var database = new PgTestDatabase();
        var fixture = PreservationFixture.Load();
        var repository = new EffectRepository(database.Context);
        var service = new AnalysisService(repository, fixture);
        var first = (await service.Save(fixture.First).RequireContentAsync()).Effects.Single();
        var independent = fixture.Second.Drafts[0] with
        {
            Question = new("financing_cost", "event_only", "qualitative", "unknown", null, "qualitative"),
            ExpectedPreviousRevisionId = null
        };
        var request = fixture.Second with { Drafts = [fixture.Second.Drafts[0] with { ExpectedPreviousRevisionId = first.RevisionId }, independent] };
        var prepared = await new AnalysisPreparationService(repository, service, fixture, null!, null!).PrepareAsync(new() { Request = request, Drafts = request.Drafts }).RequireContentAsync();
        Assert.Null(Assert.Single(prepared.Drafts).ExpectedPreviousRevisionId);
        var receipt = await service.Save(request with { Drafts = prepared.Drafts }).RequireContentAsync();
        var unknown = Assert.Single(receipt.Effects);
        Assert.NotEqual(first.EffectId, unknown.EffectId);
        Assert.Equal("missing_data", unknown.CurrentRevision.PublicationClass);
        Assert.Null(unknown.CurrentRevision.CalculationResult);
        Assert.Equal([first.EffectId], receipt.UnrevisedEffectIds);
        Assert.Equal(first.RevisionId, (await repository.GetEffect(first.EffectId))!.CurrentRevision.RevisionId);
        Assert.Single((await repository.GetEffect(first.EffectId))!.History);
    }

    [Fact]
    public async Task ExactTwoRunReplayKeepsSupportedHeadAndCanSaveIndependentUnknown()
    {
        using var database = new PgTestDatabase();
        var fixture = PreservationFixture.Load();
        var repository = new EffectRepository(database.Context);
        var service = new AnalysisService(repository, fixture);
        var first = (await service.Save(fixture.First).RequireContentAsync()).Effects.Single();
        var before = JsonSerializer.Serialize((await repository.GetEffect(first.EffectId))!, AnalysisService.Json);
        var request = fixture.Second with { Drafts = [fixture.Second.Drafts[0] with { ExpectedPreviousRevisionId = first.RevisionId }] };
        var preparation = new AnalysisPreparationService(repository, service, fixture, null!, null!);
        var prepared = await preparation.PrepareAsync(new() { Request = request, Drafts = request.Drafts }).RequireContentAsync();
        Assert.Empty(prepared.Drafts);
        var second = await service.Save(request with { Drafts = prepared.Drafts }).RequireContentAsync();
        Assert.Empty(second.Effects);
        Assert.Equal([first.EffectId], second.UnrevisedEffectIds);
        Assert.Equal(before, JsonSerializer.Serialize((await repository.GetEffect(first.EffectId))!, AnalysisService.Json));
        Assert.True((await service.Save(fixture.First).RequireContentAsync()).Duplicate);

        var unknown = fixture.Second.Drafts[0] with
        {
            Question = new("financing_cost", "event_only", "qualitative", "unknown", null, "qualitative"),
            ExpectedPreviousRevisionId = null
        };
        // A separate analysis uses another fixture-owned news version for the independent unknown.
        fixture.AddVersion(fixture.Second.NewsMaterialId, "independent-unknown");
        var news = unknown.SourceRefs.Single(r => r.MaterialId == request.NewsMaterialId);
        var independent = request with { NewsVersionId = "independent-unknown", Drafts = [unknown with { SourceRefs = [.. unknown.SourceRefs, news with { VersionId = "independent-unknown" }] }] };
        var newScope = await preparation.PrepareAsync(new() { Request = independent, Drafts = independent.Drafts }).RequireContentAsync();
        Assert.Null(Assert.Single(newScope.Drafts).ExpectedPreviousRevisionId);
        var savedUnknown = (await service.Save(independent with { Drafts = newScope.Drafts }).RequireContentAsync()).Effects.Single();
        Assert.NotEqual(first.EffectId, savedUnknown.EffectId);
        Assert.Equal("missing_data", savedUnknown.CurrentRevision.PublicationClass);
        Assert.Null(savedUnknown.CurrentRevision.CalculationResult);
        Assert.Equal(first.RevisionId, (await repository.GetEffect(first.EffectId))!.CurrentRevision.RevisionId);
    }
}

internal sealed class PreservationFixture : ISourceResolver
{
    private readonly Dictionary<(string, string), MaterialVersion> materials;
    public AnalysisRequest First { get; }
    public AnalysisRequest Second { get; }
    public EffectResponse Existing { get; }
    private PreservationFixture(JsonElement json)
    {
        var cases = json.GetProperty("cases").EnumerateArray().ToArray();
        First = cases[0].GetProperty("request").Deserialize<AnalysisRequest>(AnalysisService.Json)!;
        Second = cases[1].GetProperty("request").Deserialize<AnalysisRequest>(AnalysisService.Json)!;
        materials = cases.SelectMany(c => c.GetProperty("materials").Deserialize<MaterialVersion[]>(AnalysisService.Json)!).DistinctBy(m => (m.MaterialId, m.VersionId)).ToDictionary(m => (m.MaterialId, m.VersionId));
        var saved = cases[0].GetProperty("receipt").Deserialize<AnalysisReceipt>(AnalysisService.Json)!.Effects.Single();
        var key = AnalysisService.QuestionKey(First.Drafts[0].LiteralObjectRef, saved.CurrentRevision.Question!).RequireContent();
        Existing = new(saved.EffectId, key, First.CompanyId, First.BankProfileId, First.NewsMaterialId, First.Drafts[0].LiteralObjectRef, saved.CurrentRevision, [saved.CurrentRevision]);
    }
    public static PreservationFixture Load() => new(JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "preservation-two-runs.json"))).RootElement);
    public void AddVersion(string materialId, string versionId, string? correctsVersionId = null, string? text = null)
    {
        var original = materials[(materialId, "v1")];
        materials[(materialId, versionId)] = original with
        {
            VersionId = versionId,
            CorrectsVersionId = correctsVersionId,
            Text = text ?? original.Text,
            ContentHash = text is null ? original.ContentHash : Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text)))
        };
    }
    public SourceRef Reference(string materialId, string versionId) => new(materialId, versionId, materials[(materialId, versionId)].ContentHash);
    public Task<Result<MaterialVersion>> GetAsync(string materialId, string versionId, CancellationToken cancellationToken) => Task.FromResult(new Result<MaterialVersion>(materials[(materialId, versionId)], null));
}
