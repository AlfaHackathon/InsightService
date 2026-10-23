using System.Text.Json.Serialization;
using InsightService.Contracts.Analyses;

namespace InsightService.Contracts.Execution;

public sealed class JobFlowParameters
{
    public string NewsMaterialId { get; set; } = string.Empty;
    public string NewsVersionId { get; set; } = string.Empty;
    public DateTimeOffset AsOf { get; set; }
    public string[] GroupIds { get; set; } = [];
    public string[] PersonIds { get; set; } = [];
    public Guid[] TargetCompanyIds { get; set; } = [];
    public int CompanyBudget { get; set; } = 12;
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? RetrievalLimit { get; set; }
    public string TextCompleteness { get; set; } = "unknown";
    public bool ProjectionOnly { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public MaterialTrigger? Trigger { get; set; }
}
