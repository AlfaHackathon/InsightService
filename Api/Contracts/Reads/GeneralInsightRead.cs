using System.Text.Json;
namespace InsightService.Contracts.Reads;

// One stored general result. No raw job/checkpoint, other companies or role data.
public sealed record GeneralInsightRead(string Id, Guid CompanyId, string JobId, int InsightIndex,
    string NewsMaterialId, string NewsVersionId, string AsOf, string UpdatedAt,
    string TextCompleteness, string CompanyStatus, JsonElement Insight);
