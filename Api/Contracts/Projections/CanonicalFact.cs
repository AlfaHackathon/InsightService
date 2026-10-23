using InsightService.Contracts.Analyses;
namespace InsightService.Contracts.Projections;

public sealed class CanonicalFact
{
    public Guid CompanyId { get; set; }
    public string EffectId { get; set; } = string.Empty;
    public EffectRevision Revision { get; set; } = null!;
    public int Priority { get; set; }
    public bool RequiresReassessment { get; set; }
}

