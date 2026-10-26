using System.Text.Json.Serialization;

namespace InsightService.Contracts.Context;

public sealed record MaterialVersion(string MaterialId, string VersionId, string SourceId, string Kind,
    string Title, string Text, DateTimeOffset KnownAt, DateTimeOffset? PublishedAt, string? CorrectsVersionId,
    Guid[] CompanyIds, string[] BankProfileIds, string ContentHash,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] MaterialProvenance? Provenance = null);

public sealed record MaterialProvenance(string Acquisition, string? SourceUrl, DateTimeOffset RetrievedAt,
    string TextCompleteness, string? OriginVersionId = null, string? Locator = null);
