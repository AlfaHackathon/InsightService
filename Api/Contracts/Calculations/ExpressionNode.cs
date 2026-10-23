namespace InsightService.Contracts.Calculations;

public sealed record ExpressionNode(string Kind, string? Value = null, ExpressionNode? Left = null, ExpressionNode? Right = null);
