using System.Text.Json.Serialization;

namespace InsightService.Contracts.Analyses;

public sealed class ApplicabilityValidation
{
    public Guid CompanyId { get; set; }
    public DateTimeOffset AsOf { get; set; }
    [JsonRequired]
    public ApplicabilityCandidate[] Candidates { get; set; } = [];
}
