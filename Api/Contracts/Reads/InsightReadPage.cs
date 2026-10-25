namespace InsightService.Contracts.Reads;

public sealed record InsightReadPage<T>(Guid CompanyId, IReadOnlyList<T> Items, string? NextCursor);
