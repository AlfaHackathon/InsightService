using InsightService.Contracts.Analyses;
using InsightService.Contracts.Context;
using InsightService.Contracts.PublicReads;
using InsightService.Contracts.Reads;
using InsightService.Repositories;

namespace InsightService.Services.PublicReads;

public interface IPublicInsightMembershipResolver
{
    Task<PublicInsightMembership?> GetAsync(Guid userId, CancellationToken ct);
}

public interface IPublicInsightSourceResolver
{
    Task<MaterialVersion?> GetAsync(string materialId, string versionId, CancellationToken ct);
}

public interface IPublicInsightReadStore
{
    Task<InsightReadPage<PersonalInsightRead>> PersonalPage(Guid company, InsightReadOptions options, CancellationToken ct);
    Task<PersonalInsightRead?> PersonalDetail(Guid company, string id, CancellationToken ct);
    Task<InsightReadPage<EffectRevision>?> PersonalHistory(Guid company, string id, int limit, string? cursor, CancellationToken ct);
    Task<InsightReadPage<GeneralInsightRead>> GeneralPage(Guid company, InsightReadOptions options, CancellationToken ct);
    Task<GeneralInsightRead?> GeneralDetail(Guid company, string id, CancellationToken ct);
}

// Local owner read seam: preserve existing IDs, filters, cursors and SQL; no HTTP call to this service.
public sealed class LocalPublicInsightReadStore(InsightReadRepository repository) : IPublicInsightReadStore
{
    public Task<InsightReadPage<PersonalInsightRead>> PersonalPage(Guid company, InsightReadOptions options, CancellationToken ct) =>
        repository.PersonalPage(company, options).WaitAsync(ct);
    public Task<PersonalInsightRead?> PersonalDetail(Guid company, string id, CancellationToken ct) =>
        repository.PersonalDetail(company, id).WaitAsync(ct);
    public Task<InsightReadPage<EffectRevision>?> PersonalHistory(Guid company, string id, int limit, string? cursor, CancellationToken ct) =>
        repository.PersonalHistory(company, id, limit, cursor).WaitAsync(ct);
    public Task<InsightReadPage<GeneralInsightRead>> GeneralPage(Guid company, InsightReadOptions options, CancellationToken ct) =>
        repository.GeneralPage(company, options).WaitAsync(ct);
    public Task<GeneralInsightRead?> GeneralDetail(Guid company, string id, CancellationToken ct) =>
        repository.GeneralDetail(company, id).WaitAsync(ct);
}
