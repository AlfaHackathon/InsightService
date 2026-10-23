using InsightService.Contracts.Projections;
namespace InsightService.Contracts.Execution;

public sealed class RoleReasoningScope
{
    public string Key { get; set; } = string.Empty;
    public string ScopeVersion { get; set; } = string.Empty;
    public VisibleRoleProfile Profile { get; set; } = new();
    public CanonicalFact[] Facts { get; set; } = [];
    public GroupProjection[] Groups { get; set; } = [];
}

