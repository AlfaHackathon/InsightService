using InsightService.Contracts.Context;
namespace InsightService.Contracts.Projections;

public sealed class GroupExpansion
{
    public string GroupId { get; set; } = string.Empty;
    public Guid[] CompanyIds { get; set; } = [];
    public string[] MissingGroups { get; set; } = [];
    public string[][] Cycles { get; set; } = [];
    public bool Complete { get; set; }
    public SourceRef[] SourceRefs { get; set; } = [];
    public string? AggregationRule { get; set; }
}

