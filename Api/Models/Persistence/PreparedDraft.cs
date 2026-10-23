using InsightService.Contracts.Analyses;
using System.Text.Json.Serialization;

namespace InsightService.Models.Persistence;

public sealed record PreparedDraft(EffectDraft Draft, string? CalculationResult);
