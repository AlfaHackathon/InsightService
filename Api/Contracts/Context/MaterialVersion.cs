
using System.Text.Json.Serialization;

namespace InsightService.Contracts.Context;

public sealed record MaterialVersion(string MaterialId, string VersionId, string SourceId, string Kind,
    string Title, string Text, DateTimeOffset KnownAt, DateTimeOffset? PublishedAt, string? CorrectsVersionId,
    Guid[] CompanyIds, string[] BankProfileIds, string ContentHash);
