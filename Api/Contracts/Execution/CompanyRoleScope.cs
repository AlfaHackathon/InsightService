using InsightService.Contracts.Projections;

namespace InsightService.Contracts.Execution;

public sealed class CompanyRoleScope
{
    public RoleProfile[] Roles { get; set; } = [];
    public string[] MissingPersonIds { get; set; } = [];
    public Guid[] CompanyIds { get; set; } = [];
}
