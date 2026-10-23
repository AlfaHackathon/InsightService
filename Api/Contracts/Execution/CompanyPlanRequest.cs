using InsightService.Contracts.Analyses;

namespace InsightService.Contracts.Execution;

public sealed class CompanyPlanRequest
{
    public string JobId { get; set; } = string.Empty;
    public Guid CompanyId { get; set; }
    public EvidenceRelevance[]? Relevance { get; set; }
}
