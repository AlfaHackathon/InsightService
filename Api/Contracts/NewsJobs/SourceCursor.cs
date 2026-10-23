
using System.Text.Json;
namespace InsightService.Contracts.NewsJobs;

public sealed record SourceCursor(long Version, long Sequence, string? AfterQuestion);
