using System.Text.Json;

namespace InsightService.Contracts.Execution;

public sealed class JobProjectionRequest
{
    public string JobId { get; set; } = string.Empty;
    public bool SegmentCoverageAvailable { get; set; }
    public JsonElement[]? RoleViews { get; set; }
}
