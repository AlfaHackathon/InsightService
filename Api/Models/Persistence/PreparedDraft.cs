using InsightService.Contracts.Analyses;

namespace InsightService.Models.Persistence;

public sealed record PreparedDraft(EffectDraft Draft, string? CalculationResult);
