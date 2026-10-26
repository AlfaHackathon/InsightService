namespace InsightService.Contracts.Analyses;

// A bounded literal claim about this draft's result; not a natural-language fact checker.
public sealed record CalculationClaim(string Quote, string Value, string Unit, string Period);
