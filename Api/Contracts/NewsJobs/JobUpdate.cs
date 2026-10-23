
using System.Text.Json;
namespace InsightService.Contracts.NewsJobs;

public sealed record JobUpdate(string WorkerId, long Version, string Status, JsonElement Checkpoint, string? Error = null);
