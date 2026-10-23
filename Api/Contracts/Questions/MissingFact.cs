namespace InsightService.Contracts.Questions;

public sealed record MissingFact(string Fact, string? Query, string Scope, string? MaterialId);
