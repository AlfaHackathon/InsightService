
using System.Text.Json;
namespace InsightService.Contracts.NewsJobs;

public sealed record NewsJob(NewsJobRequest Request, string Status, long Version, string? WorkerId,
    DateTimeOffset? LeaseUntil, JsonElement Checkpoint, string? Error, DateTimeOffset UpdatedAt);
