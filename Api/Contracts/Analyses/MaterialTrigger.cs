
using System.Text.Json.Serialization;

namespace InsightService.Contracts.Analyses;

public sealed record MaterialTrigger(string MaterialId, string VersionId);
