using System.Text.Json.Nodes;
using InsightService.Contracts.Analyses;
using InsightService.Contracts.Context;

namespace InsightService.Contracts.Execution;

public sealed class CompanyPlanResponse
{
    public JsonObject AnalysisInput { get; set; } = new();
    public DepthDecisionResponse Depth { get; set; } = new();
    public long WatchAfterSequence { get; set; }
    public bool RequiresWatchRegistration { get; set; }
    public MaterialVersion[] RelevanceSources { get; set; } = [];
    public JsonObject RelevancePlan { get; set; } = new();
    public AnalysisReceipt? Receipt { get; set; }
    public string[] SegmentKeys { get; set; } = [];
}
