
using System.Text.Json;
namespace InsightService.Contracts.NewsJobs;

public sealed record NewsJobRequest(string JobId, string NewsMaterialId, string NewsVersionId,
    DateTimeOffset AsOf, JsonElement Parameters);
