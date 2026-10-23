namespace InsightService.Contracts.Analyses;

public sealed class AnalysisScope
{
    public Guid CompanyId { get; set; }
    public string NewsMaterialId { get; set; } = string.Empty;
    public string NewsVersionId { get; set; } = string.Empty;
    public DateTimeOffset AsOf { get; set; }
    public string? BankProfileId { get; set; }
    public MaterialTrigger? Trigger { get; set; }
    public string[]? Mechanisms { get; set; }
    public bool ProjectionOnly { get; set; }
}

