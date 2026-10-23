namespace InsightService.Contracts.Analyses;

public sealed record EffectSnapshot(IReadOnlyList<EffectResponse> Effects, long StateVersion);
