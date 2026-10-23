using System.Text.Json.Nodes;
using InsightService.Contracts.Projections;
namespace InsightService.Contracts.Execution;

public sealed class JobProjectionResponse
{
    public CompanyProjection[] CanonicalCompanies { get; set; } = [];
    public RoleReasoningScope[] RoleScopes { get; set; } = [];
    public JsonObject[] RoleViews { get; set; } = [];
    public JsonObject Result { get; set; } = new();
}

