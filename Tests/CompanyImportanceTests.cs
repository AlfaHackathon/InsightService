using System.Text.Json;
using AlfaHackaton.Result;
using InsightService.Common;
using InsightService.Contracts.Analyses;
using InsightService.Contracts.Context;
using InsightService.Contracts.Questions;
using InsightService.Models.Policies;
using InsightService.Repositories;
using InsightService.Services;
using InsightService.Services.Interfaces;
using Xunit;

public sealed class CompanyImportanceTests : IDisposable
{
    private readonly PgTestDatabase database = new();
    private static readonly DateTimeOffset Time = DateTimeOffset.Parse("2026-10-25T00:00:00Z");
    private const string Hash = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private static readonly SourceRef News = new("news", "v1", Hash);
    private static SourceRef Own(Guid company) => new("own-" + company.ToString("N"), "v1", Hash);
    private static AnalysisRequest Request(Guid company, int? score) => new(company, null, "news", "v1", Time,
        [new("importance", "company:" + company.ToString("D"), null, "Synthetic own-company dependency requires review.", "observed", [News, Own(company)],
            Impact: new("qualitative", "qualitative", null, "supported", score ?? 0, 0, 0),
            Question: new QuestionIdentity("liquidity", "event_only", "qualitative", "2026-11", null, "qualitative"),
            Assessment: new([], [], new("established", "documented_exposure", "The own document establishes an explicit company dependency.", [Own(company)], [])),
            CompanyImportance: new(score, score is null ? "Company materiality cannot be established." : "An explicit own-company dependency needs prompt review.", score is null ? [] : [Own(company)]),
            Title: "Важность изменения условий для компании")]);
    private AnalysisService Service() => new(new EffectRepository(database.Context), new Sources());

    [Fact]
    public async Task SameNewsKeepsDifferentCompanyScoresInCanonicalRevisionsAndReads()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var first = (await Service().Save(Request(a, 5))).RequireContent();
        var second = (await Service().Save(Request(b, 2))).RequireContent();
        Assert.Equal("insights_saved", first.Outcome);
        Assert.Empty(first.UnpublishedDrafts!);
        Assert.Equal(5, first.Effects.Single().CurrentRevision.CompanyImportance!.Score);
        Assert.Equal(2, second.Effects.Single().CurrentRevision.CompanyImportance!.Score);
        var reader = new EffectRepository(database.Context);
        Assert.Equal(5, (await reader.GetEffect(first.Effects.Single().EffectId))!.CurrentRevision.CompanyImportance!.Score);
        Assert.Equal(2, (await reader.GetSnapshot(b, "news")).Effects.Single().CurrentRevision.CompanyImportance!.Score);
        Assert.True((await Service().Save(Request(a, 5))).RequireContent().Duplicate);
        Assert.Single((await reader.GetEffect(first.Effects.Single().EffectId))!.History);
    }

    [Fact]
    public async Task NullIsPreservedAndLegacyRevisionDoesNotGainAnAssessment()
    {
        var saved = (await Service().Save(Request(Guid.NewGuid(), null))).RequireContent();
        var revision = saved.Effects.Single().CurrentRevision;
        Assert.Null(revision.CompanyImportance!.Score);
        var old = revision with { CompanyImportance = null };
        var json = JsonSerializer.Serialize(old, AnalysisProtocol.Json);
        Assert.DoesNotContain("companyImportance", json, StringComparison.Ordinal);
        Assert.Null(JsonSerializer.Deserialize<EffectRevision>(json, AnalysisProtocol.Json)!.CompanyImportance);
        var request = Request(Guid.NewGuid(), 4);
        var legacyDraft = request.Drafts.Single() with { CompanyImportance = null };
        Assert.DoesNotContain("companyImportance", JsonSerializer.Serialize(legacyDraft, AnalysisProtocol.Json), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(6)]
    public async Task InvalidScoresNeverCommit(int score)
    {
        var request = Request(Guid.NewGuid(), score);
        Assert.NotNull((await Service().Save(request)).Error);
        Assert.Empty((await new EffectRepository(database.Context).GetSnapshot(request.CompanyId, "news")).Effects);
    }

    [Fact]
    public async Task PublicOnlyOrUnboundEvidenceCannotEstablishCompanyImportance()
    {
        var request = Request(Guid.NewGuid(), 4);
        foreach (var refs in new[] { new[] { News }, new[] { new SourceRef("foreign", "v1", Hash) } })
        {
            var draft = request.Drafts.Single();
            var result = await Service().Save(request with { Drafts = [draft with { CompanyImportance = draft.CompanyImportance! with { EvidenceRefs = refs } }] });
            Assert.NotNull(result.Error);
        }
    }

    [Fact]
    public async Task ExactEvidenceIdentityCannotBeSubstitutedEvenWithTheSameHash()
    {
        var company = Guid.NewGuid();
        var draft = Request(company, 4).Drafts.Single();
        var result = await CompanyImportancePolicy.Validate(draft, company, (_, _) => Task.FromResult(new Result<MaterialVersion>(
            new("substituted", "v2", "synthetic", "company", "Synthetic", "Own facts", Time, Time, null, [company], [], Hash), null)));
        Assert.NotNull(result.Error);
    }

    [Fact]
    public void NewAssessmentCannotContradictItsLegacyCompatibilityComponent()
    {
        var request = Request(Guid.NewGuid(), 5);
        var draft = request.Drafts.Single();
        Assert.Null(CompanyImportancePolicy.ShapeError(draft));
        var impact = new ImpactMetadata("other", "qualitative", null, "unknown", 0, 0, 0);
        Assert.NotNull(CompanyImportancePolicy.ShapeError(draft with { Impact = impact }));
        Assert.Null(CompanyImportancePolicy.ShapeError(draft with { Impact = impact with { Importance = 5 } }));
        Assert.Null(CompanyImportancePolicy.ShapeError(draft with { CompanyImportance = null, Impact = impact }));
        Assert.NotNull(CompanyImportancePolicy.ShapeError(draft with { CompanyImportance = draft.CompanyImportance! with { Rationale = " " } }));
    }

    public void Dispose() => database.Dispose();

    private sealed class Sources : ISourceResolver
    {
        public Task<Result<MaterialVersion>> GetAsync(string materialId, string versionId, CancellationToken cancellationToken)
        {
            var own = materialId.StartsWith("own-", StringComparison.Ordinal);
            var companies = own ? new[] { Guid.ParseExact(materialId[4..], "N") } : [];
            return Task.FromResult(new Result<MaterialVersion>(new(materialId, versionId, "synthetic", own ? "company" : "news",
                "Synthetic", "Synthetic explicit company dependency.", Time, Time, null, companies, [], Hash), null));
        }
    }
}
