using System.Text.Json;
using System.Text.Json.Serialization;

namespace InsightService.Contracts.Analyses;

public sealed class DepthRequest
{
    public Guid CompanyId { get; set; }
    [JsonRequired]
    public AnalysisScope Request { get; set; } = new();
    public JsonElement Plan { get; set; }
    public DepthInventory Inventory { get; set; } = new();
    public bool HasProfile { get; set; }
    public bool NewsCorrection { get; set; }
    public EvidenceRelevance[]? Relevance { get; set; }
}
