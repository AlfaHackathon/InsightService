using InsightService.Contracts.Context;

namespace InsightService.Contracts.Projections;

public sealed class RoleProfile
{
    public string PersonId { get; set; } = string.Empty;
    public Guid CompanyId { get; set; }
    public string Role { get; set; } = string.Empty;
    public string[] Responsibilities { get; set; } = [];
    public Guid[] AllowedCompanyIds { get; set; } = [];
    public string[] AllowedMaterialIds { get; set; } = [];
    public DateTimeOffset KnownAt { get; set; }
    public SourceRef[] SourceRefs { get; set; } = [];
    public string RevisionId { get; set; } = string.Empty;
}
