namespace InsightService.Contracts.Projections;

public sealed class CanonicalProjection
{
    public CompanyProjection[] Companies { get; set; } = [];
    public CanonicalFact[] Facts { get; set; } = [];
}
