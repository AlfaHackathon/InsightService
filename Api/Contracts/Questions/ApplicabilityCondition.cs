using System.Text.Json.Serialization;
using InsightService.Contracts.Context;

namespace InsightService.Contracts.Questions;

public sealed record ApplicabilityCondition(string Fact, string Predicate, string Status, SourceRef[] SourceRefs,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Id = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Assertion = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string[]? DependsOn = null);
