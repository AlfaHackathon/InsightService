using System.Text.Json.Serialization;
namespace InsightService.Contracts.Analyses;

public sealed class DraftPreparation
{
    [JsonRequired]
    public AnalysisRequest Request { get; set; } = null!;
    [JsonRequired]
    public EffectDraft[] Drafts { get; set; } = [];
    public long? ExpectedStateVersion { get; set; }
}

