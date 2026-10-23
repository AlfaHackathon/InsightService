namespace InsightService.Contracts.Analyses;

public class AnalysisSessionResponse
{
    public AnalysisReceipt? Receipt { get; set; }
    public EffectResponse[] PreviousEffects { get; set; } = [];
    public long? StateVersion { get; set; }
}
