using InsightService.Contracts.Analyses;

namespace InsightService.Contracts.Projections;

public sealed class CompanyProjection
{
    public Guid CompanyId { get; set; }
    public string Status { get; set; } = string.Empty;
    public AnalysisReceipt? Receipt { get; set; }
    public string? Error { get; set; }
    public bool? ContextComplete { get; set; }
    public string[]? StaleEffectIds { get; set; }
}
