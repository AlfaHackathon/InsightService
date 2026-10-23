using InsightService.Contracts.Context;
namespace InsightService.Contracts.Analyses;

public sealed class EvidenceRelevance
{
    public SourceRef SourceRef { get; set; } = null!;
    public string Status { get; set; } = string.Empty;
    public string Reason { get; set; } = string.Empty;
    public string Quote { get; set; } = string.Empty;
}

