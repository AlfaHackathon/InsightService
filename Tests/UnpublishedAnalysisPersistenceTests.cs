using System.Text.Json;
using InsightService.Repositories;
using InsightService.Services;
using Xunit;

public sealed class UnpublishedAnalysisPersistenceTests
{
    [Fact]
    public async Task UnknownCommitsOnlyDiagnosticReceiptAndLeavesGoodHeadHistoryAndStateVersionUntouched()
    {
        using var database = new PgTestDatabase();
        var fixture = PreservationFixture.Load();
        var repository = new EffectRepository(database.Context);
        var service = new AnalysisService(repository, fixture);
        var saved = Assert.Single((await service.Save(fixture.CurrentFirst).RequireContentAsync()).Effects);
        var before = (await repository.GetEffect(saved.EffectId))!;
        var version = (await repository.GetSnapshot(fixture.First.CompanyId, fixture.First.NewsMaterialId)).StateVersion;
        var input = fixture.Second;
        var prepared = await new AnalysisPreparationService(repository, service, fixture, null!, null!)
            .PrepareAsync(new() { Request = input, Drafts = input.Drafts }).RequireContentAsync();
        var receipt = await service.Save(input with { Drafts = prepared.Drafts }).RequireContentAsync();
        Assert.Equal("no_insight", receipt.Outcome);
        Assert.Empty(receipt.Effects);
        Assert.Single(receipt.UnpublishedDrafts!);
        Assert.Contains(saved.EffectId, receipt.UnrevisedEffectIds!);
        Assert.Equal(JsonSerializer.Serialize(before, AnalysisService.Json), JsonSerializer.Serialize(await repository.GetEffect(saved.EffectId), AnalysisService.Json));
        Assert.Equal(version, (await repository.GetSnapshot(input.CompanyId, input.NewsMaterialId)).StateVersion);
        Assert.True((await service.Save(input with { Drafts = prepared.Drafts }).RequireContentAsync()).Duplicate);
        Assert.Single((await repository.GetEffect(saved.EffectId))!.History);
    }

    [Fact]
    public async Task FirstUnknownProducesNoCanonicalHeadButRetainsExactMissingFactsForReplay()
    {
        using var database = new PgTestDatabase();
        var fixture = PreservationFixture.Load();
        var repository = new EffectRepository(database.Context);
        var service = new AnalysisService(repository, fixture);
        var receipt = await service.Save(fixture.Second).RequireContentAsync();
        Assert.Equal("no_insight", receipt.Outcome);
        Assert.Empty(receipt.Effects);
        Assert.Empty((await repository.GetSnapshot(fixture.Second.CompanyId, fixture.Second.NewsMaterialId)).Effects);
        Assert.Equal(fixture.Second.Drafts[0].Assessment!.Unknowns, Assert.Single(receipt.UnpublishedDrafts!).Assessment!.Unknowns);
        Assert.True((await service.Save(fixture.Second).RequireContentAsync()).Duplicate);
    }
}
