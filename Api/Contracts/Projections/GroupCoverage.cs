namespace InsightService.Contracts.Projections;

public sealed class GroupCoverage
{
    public int ExpectedCompanies { get; set; }
    public int AnalyzedCompanies { get; set; }
    public Guid[] MissingCompanyIds { get; set; } = [];
    public string[] MissingGroups { get; set; } = [];
    public string[][] Cycles { get; set; } = [];
    public string[] UnknownEffects { get; set; } = [];
    public string[] DuplicateObjects { get; set; } = [];
    public string[] UnrevisedEffectIds { get; set; } = [];
    public Guid[] EmptyCompanyIds { get; set; } = [];
    public Guid[] PartialContextCompanyIds { get; set; } = [];
    public bool AggregationRuleConfirmed { get; set; }
}
