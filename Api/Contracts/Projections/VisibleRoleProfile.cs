namespace InsightService.Contracts.Projections;

public sealed class VisibleRoleProfile
{
    public string PersonId { get; set; } = string.Empty;
    public Guid CompanyId { get; set; }
    public string Role { get; set; } = string.Empty;
    public string[] Responsibilities { get; set; } = [];
}

