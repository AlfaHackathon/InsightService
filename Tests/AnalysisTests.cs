using InsightService.Contracts.Analyses;
using InsightService.Contracts.Calculations;
using InsightService.Contracts.Context;
using InsightService.Models;
using InsightService.Models.Persistence;
using InsightService.Repositories;
using InsightService.Services;
using InsightService.Services.Interfaces;
using InsightService;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Xunit;

public sealed class AnalysisTests : IDisposable
{
    private readonly PgTestDatabase database = new();
    private readonly Guid company = Guid.NewGuid();
    private readonly DateTimeOffset now = DateTimeOffset.Parse("2026-10-21T08:00:00Z");
    private const string Hash = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private EffectRepository Repository() => new(database.Context);
    private AnalysisService Service() => new(Repository(), new Sources(company, now));
    private static ExpressionNode Value(string text) => new("value", text);
    private static Calculation Cost(string delta) => new(new("multiply", Left: Value(delta), Right: Value("104")), "RUB", "Synthetic planned acceptance");
    private AnalysisRequest Request(string version = "v1", string? prior = null, string delta = "150") => new(company,
        "U_97", "news", version, now.AddHours(1), [new("lot-1", "contract-1/lot-1", prior,
            "Synthetic expected additional cost", "planned", [new("news", version, Hash), new("contract", "v1", Hash)], Cost(delta))]);

    [Fact]
    public async Task CorrectionIsSameEffectWithTwoRevisionsAndRepeatDoesNotRevertHead()
    {
        var firstRequest = Request();
        var first = await Service().Save(firstRequest);
        Assert.Equal("15600", first.Effects.Single().CurrentRevision.CalculationResult);
        var second = await Service().Save(Request("v2", first.Effects.Single().RevisionId, "60"));
        Assert.Equal(first.Effects.Single().EffectId, second.Effects.Single().EffectId);
        Assert.Equal("6240", second.Effects.Single().CurrentRevision.CalculationResult);
        var replay = await Service().Save(firstRequest);
        Assert.True(replay.Duplicate);
        var stored = (await Service().GetEffect(first.Effects.Single().EffectId));
        Assert.Equal(second.Effects.Single().RevisionId, stored.CurrentRevision.RevisionId);
        Assert.Equal(2, stored.History.Count);
        Assert.Equal("planned", stored.CurrentRevision.Status);
    }

    [Fact]
    public async Task EmptyDraftReceiptSurvivesRestartAndConflictingPayloadIsRejected()
    {
        var empty = Request() with { Drafts = [] };
        var first = await Service().Save(empty);
        Assert.Empty(first.Effects);
        Assert.Equal(first.AnalysisId, (await Service().GetAnalysis(company, "news", "v1")).AnalysisId);
        Assert.True((await Service().Save(empty)).Duplicate);
        Assert.Equal(409, (await Assert.ThrowsAsync<ApiException>(async () => await Service().Save(Request()))).StatusCode);
    }

    [Fact]
    public async Task OmittedPriorEffectsRemainExplicitInCorrectionReceiptAfterRestart()
    {
        var original = await Service().Save(Request() with { ContextComplete = true });
        var correction = Request("v2") with { Drafts = [], ContextComplete = false };
        var saved = await Service().Save(correction);
        Assert.False(saved.ContextComplete);
        Assert.Equal([original.Effects.Single().EffectId], saved.UnrevisedEffectIds);
        var reopened = (await Service().GetAnalysis(company, "news", "v2"));
        Assert.Equal(saved.UnrevisedEffectIds, reopened.UnrevisedEffectIds);
        Assert.Equal("15600", (await Service().GetEffect(original.Effects.Single().EffectId)).CurrentRevision.CalculationResult);
        Assert.Single((await Service().GetEffect(original.Effects.Single().EffectId)).History);
        var replay = await Service().Save(correction);
        Assert.True(replay.Duplicate);
        Assert.Equal(saved.UnrevisedEffectIds, replay.UnrevisedEffectIds);
        Assert.Empty(original.UnrevisedEffectIds!);
    }

    [Fact]
    public async Task LegacyRequestWithoutContextCoverageStillReplaysItsExistingReceipt()
    {
        var request = Request();
        request = request with { Drafts = [request.Drafts[0] with { SourceRefs = [new("contract", "v1", Hash), new("news", "v1", Hash)] }] };
        // The wire/hash shape before optional coverage existed, independent of the current DTO.
        var legacy = new
        {
            request.CompanyId,
            request.BankProfileId,
            request.NewsMaterialId,
            request.NewsVersionId,
            request.AsOf,
            request.Drafts
        };
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(legacy, AnalysisService.Json))));
        await Repository().Save(request, hash, request.Drafts.Select(draft => new PreparedDraft(draft, "15600")).ToArray());
        var replay = await Service().Save(request);
        Assert.True(replay.Duplicate);
        Assert.Null(replay.ContextComplete);
        Assert.Single((await Service().GetEffects(company, "news")).Single().History);
    }

    [Fact]
    public async Task StalePriorAndObjectRebindingDoNotAdvanceHead()
    {
        var first = await Service().Save(Request());
        var prior = first.Effects.Single().RevisionId;
        var wrongObject = Request("v2", prior) with { Drafts = [Request("v2", prior).Drafts[0] with { LiteralObjectRef = "another-object" }] };
        Assert.Equal(409, (await Assert.ThrowsAsync<ApiException>(async () => await Service().Save(wrongObject))).StatusCode);
        await Service().Save(Request("v2", prior, "60"));
        Assert.Equal(409, (await Assert.ThrowsAsync<ApiException>(async () => await Service().Save(Request("v3", prior, "40")))).StatusCode);
        Assert.Equal(2, (await Service().GetEffect(first.Effects.Single().EffectId)).History.Count);
    }

    [Fact]
    public async Task MissingOrWrongSourceAndForeignCompanyCannotCreateEffect()
    {
        var request = Request();
        foreach (var source in new[] { new SourceRef("missing", "v1", Hash), new SourceRef("foreign", "v1", Hash), new SourceRef("future", "v1", Hash), new SourceRef("contract", "v1", new string('0', 64)) })
        {
            var bad = request with { Drafts = [request.Drafts[0] with { SourceRefs = [new("news", "v1", Hash), source] }] };
            Assert.Equal(422, (await Assert.ThrowsAsync<ApiException>(async () => await Service().Save(bad))).StatusCode);
            Assert.Empty((await Service().GetEffects(company, "news")));
        }
    }

    [Fact]
    public async Task ConcurrentSameAnalysisProducesOneReceiptAndRevision()
    {
        var request = Request();
        var service = Service();
        var receipts = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => Task.Run(async () => await service.Save(request))));
        Assert.Single(receipts.Select(x => x.AnalysisId).Distinct());
        Assert.Equal(1, receipts.Count(x => !x.Duplicate));
        Assert.Single((await Service().GetEffects(company, "news")).Single().History);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AnalysisCommittedDuringSourceResolutionChecksReceiptIdentity(bool conflictingPayload)
    {
        var request = Request();
        var paused = new PausingSources(new Sources(company, now));
        var secondService = new AnalysisService(Repository(), paused);
        var pending = secondService.Save(request);
        await paused.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        AnalysisReceipt first;
        var winningRequest = conflictingPayload ? request with
        { Drafts = [request.Drafts[0] with { Conclusion = "Different synthetic conclusion" }] } : request;
        try { first = await Service().Save(winningRequest); }
        finally { paused.Resume.TrySetResult(true); }
        if (conflictingPayload)
        {
            var failure = await Assert.ThrowsAsync<ApiException>(() => pending.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Equal(409, failure.StatusCode);
            Assert.Equal("analysis_payload_conflict", failure.Code);
        }
        else
        {
            var replay = await pending.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(replay.Duplicate);
            Assert.Equal(first.AnalysisId, replay.AnalysisId);
        }
        Assert.Single((await Service().GetEffects(company, "news")).Single().History);
    }

    [Fact]
    public async Task RepositoryRollsBackWholeBatchWhenLaterEffectHasStalePrior()
    {
        var request = Request();
        var invalid = request with { Drafts = [request.Drafts[0], request.Drafts[0] with { EffectKey = "lot-2", ExpectedPreviousRevisionId = "nonexistent" }] };
        var prepared = invalid.Drafts.Select(x => new PreparedDraft(x, "15600")).ToArray();
        Assert.Equal(409, (await Assert.ThrowsAsync<ApiException>(async () => await Repository().Save(invalid, "request-hash", prepared))).StatusCode);
        Assert.Empty((await Repository().GetEffects(company, "news")));
        Assert.Null(await Repository().FindAnalysis(company, "news", "v1"));
    }

    [Fact]
    public async Task DecimalExpressionRejectsRoundingOverflowZeroAndDeepTrees()
    {
        Assert.Equal("15600", DecimalCalculator.Calculate(Cost("150")).Result);
        Assert.Throws<ApiException>(() => DecimalCalculator.Calculate(Cost("0.00000000000000000000000000001")));
        Assert.Throws<ApiException>(() => DecimalCalculator.Calculate(new(new("divide", Left: Value("1"), Right: Value("0")), "RUB", "test")));
        Assert.Throws<ApiException>(() => DecimalCalculator.Calculate(new(new("multiply", Left: Value(decimal.MaxValue.ToString()), Right: Value("2")), "RUB", "test")));
        ExpressionNode deep = Value("1");
        for (var i = 0; i < 17; i++) deep = new("add", Left: deep, Right: Value("1"));
        Assert.Throws<ApiException>(() => DecimalCalculator.Calculate(new(deep, "RUB", "test")));
    }

    public void Dispose() => database.Dispose();

    private sealed class Sources(Guid company, DateTimeOffset now) : ISourceResolver
    {
        public Task<MaterialVersion> Get(string materialId, string versionId, CancellationToken cancellationToken)
        {
            if (materialId == "missing") throw new ApiException(422, "unknown_source", "missing");
            var material = new MaterialVersion(materialId, versionId, "source", materialId == "news" ? "news" : "company",
                "Synthetic", "Synthetic source", materialId == "future" ? now.AddDays(1) : now,
                now, materialId == "news" && versionId != "v1" ? versionId == "v2" ? "v1" : "v2" : null,
                materialId == "news" ? [] : [materialId == "foreign" ? Guid.NewGuid() : company], [], Hash);
            return Task.FromResult(material);
        }
    }

    private sealed class PausingSources(ISourceResolver inner) : ISourceResolver
    {
        public TaskCompletionSource<bool> Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> Resume { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int paused;

        public async Task<MaterialVersion> Get(string materialId, string versionId, CancellationToken cancellationToken)
        {
            if (Interlocked.Exchange(ref paused, 1) == 0)
            {
                Entered.TrySetResult(true);
                await Resume.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
            }
            return await inner.Get(materialId, versionId, cancellationToken);
        }
    }
}
