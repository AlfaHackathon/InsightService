using InsightService.Contracts.Projections;

namespace InsightService.Contracts.Execution;

public sealed class CompanyGroupPage
{
    public GroupExpansion[] Groups { get; set; } = [];
}
