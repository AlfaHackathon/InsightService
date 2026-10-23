namespace InsightService.Contracts.Projections;

public sealed class RoleProjectionResponse
{
    public VisibleRoleProfile Profile { get; set; } = null!;
    public CanonicalFact[] Facts { get; set; } = [];
    public GroupProjection[] Groups { get; set; } = [];
}
