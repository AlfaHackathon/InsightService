namespace InsightService.Contracts.SourceReactions;

public sealed record SourceChangeRequest(string Category, string EntityId, string VersionId, DateTimeOffset KnownAt,
    Guid[] CompanyIds, string? AfterQuestion = null);
