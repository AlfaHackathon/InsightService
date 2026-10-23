using InsightService.Contracts.Questions;

namespace InsightService.Contracts.Projections;

public sealed class GroupTotal
{
    public string Metric { get; set; } = string.Empty;
    public string AmountBasis { get; set; } = string.Empty;
    public string Unit { get; set; } = string.Empty;
    public string Period { get; set; } = string.Empty;
    public QuestionIdentity? Question { get; set; }
    public string KnownSubtotal { get; set; } = string.Empty;
    public string? FullTotal { get; set; }
    public TotalCoverage Coverage { get; set; } = null!;
    public string[] EffectIds { get; set; } = [];
    public Guid[] CompanyIds { get; set; } = [];
}
