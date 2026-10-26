using InsightService.Repositories;
using InsightService.Services;
using Xunit;

// Native repository gate only; not part of the no-new-database unit allowlist.
public sealed class InsightTitlePersistenceTests
{
    [Fact]
    public async Task TitleIsStoredPerRevisionAndReplayDoesNotReplaceTheCurrentTitle()
    {
        using var database = new PgTestDatabase();
        var fixture = PreservationFixture.Load();
        var repository = new EffectRepository(database.Context);
        var service = new AnalysisService(repository, fixture);
        var initial = fixture.CurrentFirst;
        var first = Assert.Single((await service.Save(initial).RequireContentAsync()).Effects);
        var news = initial.Drafts[0].SourceRefs.Single(r => r.MaterialId == initial.NewsMaterialId);
        fixture.AddVersion(initial.NewsMaterialId, "title-correction", "v1");
        var nextDraft = initial.Drafts[0] with
        {
            Title = "Пересмотр расходов по обновлённым условиям",
            ExpectedPreviousRevisionId = first.RevisionId,
            SourceRefs = [.. initial.Drafts[0].SourceRefs.Where(r => r.MaterialId != news.MaterialId), news with { VersionId = "title-correction" }]
        };
        var next = initial with { NewsVersionId = "title-correction", Drafts = [nextDraft] };
        var second = Assert.Single((await service.Save(next).RequireContentAsync()).Effects);
        var stored = (await repository.GetEffect(first.EffectId))!;
        Assert.Equal(nextDraft.Title, stored.CurrentRevision.Title);
        Assert.Equal(new[] { initial.Drafts[0].Title, nextDraft.Title }, stored.History.Select(r => r.Title).ToArray());
        Assert.True((await service.Save(initial).RequireContentAsync()).Duplicate);
        Assert.Equal(second.RevisionId, (await repository.GetEffect(first.EffectId))!.CurrentRevision.RevisionId);
        Assert.Equal(nextDraft.Title, (await repository.GetEffect(first.EffectId))!.CurrentRevision.Title);
    }
}
