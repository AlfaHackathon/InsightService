using System.Text.Json;
using InsightService.Contracts.Projections;

namespace InsightService.Contracts.Execution;

public sealed class JobExecutionCheckpoint
{
    public JsonElement Plan { get; set; }
    public JsonElement[] SignalInventories { get; set; } = [];
    public JsonElement[] SegmentBatches { get; set; } = [];
    public Guid[] SelectedCompanyIds { get; set; } = [];
    public Guid[] PendingCompanyIds { get; set; } = [];
    public string? RecipientCursor { get; set; }
    public GroupExpansion[] Groups { get; set; } = [];
    public CompanyProjection[] Companies { get; set; } = [];
    public JsonElement[] RoleViews { get; set; } = [];
}
