using System.Text.Json.Serialization;
using InsightService.Contracts.Context;
namespace InsightService.Contracts.Analyses;

public sealed class DepthDecisionResponse
{
    public string PolicyVersion { get; set; } = string.Empty;
    public string Level { get; set; } = string.Empty;
    public string Reason { get; set; } = string.Empty;
    public int OwnOriginalCount { get; set; }
    public bool InventoryCompleteForLoadedScope { get; set; }
    public SourceRef[] SourceRefs { get; set; } = [];
    public int[] GeneralInsightIndexes { get; set; } = [];
    public string Applicability { get; set; } = string.Empty;
    public string[] MissingFacts { get; set; } = [];
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public EvidenceRelevance[]? Relevance { get; set; }
}

