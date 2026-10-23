namespace InsightService.Contracts.Projections;

public sealed class TotalCoverage
{
    public bool Complete { get; set; }
    public Guid[] MissingCompanyIds { get; set; } = [];
    public string[] UnresolvedEffectIds { get; set; } = [];
    public string[] DuplicateObjects { get; set; } = [];
}
