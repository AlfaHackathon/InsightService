using System.Text.Json.Serialization;

namespace InsightService.Contracts.Questions;

public sealed record MissingFact(string Fact, string? Query, string Scope, string? MaterialId,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Id = null);
