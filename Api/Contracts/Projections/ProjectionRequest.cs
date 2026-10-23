using System.Text.Json.Serialization;
namespace InsightService.Contracts.Projections;

public sealed class ProjectionRequest
{
    [JsonRequired]
    public CompanyProjection[] Companies { get; set; } = [];
    public GroupExpansion[]? Groups { get; set; }
    public DateTimeOffset? AsOf { get; set; }
}

