using System.Text.Json.Serialization;
namespace InsightService.Contracts.Projections;

public sealed class RoleProjectionRequest
{
    public string PersonId { get; set; } = string.Empty;
    public Guid CompanyId { get; set; }
    public DateTimeOffset AsOf { get; set; }
    [JsonRequired]
    public CompanyProjection[] Companies { get; set; } = [];
    public GroupExpansion[]? Groups { get; set; }
}

