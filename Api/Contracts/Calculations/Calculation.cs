namespace InsightService.Contracts.Calculations;

public sealed record Calculation(ExpressionNode Expression, string Unit, string Period);
