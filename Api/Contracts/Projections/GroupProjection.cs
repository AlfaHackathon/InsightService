using InsightService.Contracts.Context;
namespace InsightService.Contracts.Projections;

public sealed class GroupProjection
{
    public string GroupId { get; set; } = string.Empty;
    public Guid[] CompanyIds { get; set; } = [];
    public bool Complete { get; set; }
    public GroupCoverage Coverage { get; set; } = null!;
    public GroupTotal[] Totals { get; set; } = [];
    public SourceRef[] SourceRefs { get; set; } = [];
}

