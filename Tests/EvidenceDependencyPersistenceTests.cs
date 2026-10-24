using System.Net;
using AlfaHackaton.Result;
using InsightService.Contracts.Analyses;
using InsightService.Contracts.Context;
using InsightService.Contracts.Questions;
using InsightService.Repositories;
using InsightService.Services;
using InsightService.Services.Interfaces;
using Xunit;

public sealed class EvidenceDependencyPersistenceTests : IDisposable
{
    private readonly PgTestDatabase database = new();
    private readonly Guid company = Guid.NewGuid();
    private static readonly DateTimeOffset At = DateTimeOffset.Parse("2026-10-23T08:00:00Z");
    private const string Hash = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private AnalysisService Service() => new(new EffectRepository(database.Context), new Sources(company));

    [Fact]
    public async Task UnknownPrerequisiteCannotBeSavedAsSupportedDerivedCondition()
    {
        var request = Request();
        request = request with { Drafts = [request.Drafts[0] with
        {
            Assessment = new([new("Public index 10 to 12 and contract index plus 2", "Loan rate 12 to 14", "supported",
                request.Drafts[0].SourceRefs, "rate", "derived", ["index_identity"])],
                [new("News index identity versus contract index", null, "company", "contract", "index_identity")])
        }] };
        var error = await ResultAssert.ErrorAsync(async () => await Service().Save(request));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, error.HttpStatusCode);
        Assert.Empty((await Service().GetEffects(company, "news")).RequireContent());
    }

    [Fact]
    public async Task GraphSurvivesReceiptHistoryAndReplayWhileChangedGraphConflicts()
    {
        var firstRequest = Request();
        var first = (await Service().Save(firstRequest)).RequireContent();
        var firstRevision = Assert.Single(first.Effects);
        var secondRequest = Request("2", firstRevision.RevisionId);
        var draft = secondRequest.Drafts[0];
        secondRequest = secondRequest with { Drafts = [draft with
        {
            Assessment = draft.Assessment! with
            {
                Conditions = [.. draft.Assessment!.Conditions,
                    new("Index identity still unresolved", "Resulting rate is unresolved", "unknown", [], "rate", "derived", ["index_identity"]) ]
            }
        }] };
        var second = (await Service().Save(secondRequest)).RequireContent();
        var current = (await Service().GetEffect(firstRevision.EffectId)).RequireContent();
        Assert.Equal(2, current.History.Count);
        Assert.Equal("observed", current.History[0].Assessment!.Conditions[0].Assertion);
        Assert.Equal(["index_identity"], Assert.IsType<string[]>(current.History[1].Assessment!.Conditions[1].DependsOn));
        Assert.Equal("index_identity", current.History[1].Assessment!.Unknowns[0].Id);

        var replay = (await Service().Save(firstRequest)).RequireContent();
        Assert.True(replay.Duplicate);
        Assert.Equal(first.AnalysisId, replay.AnalysisId);
        Assert.Single(Assert.Single(replay.Effects).CurrentRevision.Assessment!.Conditions);
        Assert.Equal(Assert.Single(second.Effects).RevisionId, (await Service().GetEffect(firstRevision.EffectId)).RequireContent().CurrentRevision.RevisionId);
        Assert.True((await Service().Save(secondRequest)).RequireContent().Duplicate);

        var changed = secondRequest with { Drafts = [secondRequest.Drafts[0] with
        {
            Assessment = secondRequest.Drafts[0].Assessment! with
            {
                Conditions = [.. secondRequest.Drafts[0].Assessment!.Conditions.Select(c => c.Id == "rate" ? c with { DependsOn = ["principal"] } : c)]
            }
        }] };
        Assert.Equal(HttpStatusCode.Conflict, (await ResultAssert.ErrorAsync(async () => await Service().Save(changed))).HttpStatusCode);
        Assert.Equal(2, (await Service().GetEffect(firstRevision.EffectId)).RequireContent().History.Count);
    }

    [Fact]
    public async Task LegacyReceiptReplaysWithoutSynthesizedMetadataAndAddingGraphConflicts()
    {
        var graph = Request();
        var draft = graph.Drafts[0];
        var legacy = graph with { Drafts = [draft with { Assessment = draft.Assessment! with
        {
            Conditions = draft.Assessment!.Conditions.Select(c => c with { Id = null, Assertion = null, DependsOn = null }).ToArray(),
            Unknowns = draft.Assessment.Unknowns.Select(u => u with { Id = null }).ToArray()
        }}] };
        var first = (await Service().Save(legacy)).RequireContent();
        var replay = (await Service().Save(legacy)).RequireContent();
        Assert.True(replay.Duplicate);
        Assert.Equal(first.AnalysisId, replay.AnalysisId);
        Assert.Null(Assert.Single(replay.Effects).CurrentRevision.Assessment!.Conditions[0].Id);
        Assert.Equal(HttpStatusCode.Conflict, (await ResultAssert.ErrorAsync(async () => await Service().Save(graph))).HttpStatusCode);
        Assert.Single((await Service().GetEffect(Assert.Single(first.Effects).EffectId)).RequireContent().History);
    }

    private AnalysisRequest Request(string version = "1", string? previous = null) => new(company, "U_1", "news", version, At,
        [new("q", "company:" + company, previous, "Synthetic financing assessment", "unknown",
            [new("news", version, Hash), new("contract", "1", Hash)],
            Impact: new("risk", "qualitative", null, "unknown", 1, 1, 1),
            Question: new("financing_cost", "event_only", "qualitative", "unknown", null, "qualitative"),
            Assessment: new([new("Own loan principal RUB 1000000", "Principal is documented", "supported", [new("contract", "1", Hash)], "principal", "observed", [])],
                [new("News index identity versus contract index", null, "company", "contract", "index_identity")]))]);

    private sealed class Sources(Guid company) : ISourceResolver
    {
        public Task<Result<MaterialVersion>> GetAsync(string materialId, string versionId, CancellationToken ct) => Task.FromResult(
            new Result<MaterialVersion>(new(materialId, versionId, "fixture", materialId == "news" ? "news" : "company", materialId,
                "Synthetic source fixture", At, At, materialId == "news" && versionId == "2" ? "1" : null,
                materialId == "news" ? [] : [company], [], Hash), null));
    }

    public void Dispose() => database.Dispose();
}
