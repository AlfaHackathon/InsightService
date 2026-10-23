using InsightService.Contracts.Context;
using System.Text.Json.Serialization;

namespace InsightService.Contracts.Questions;

public sealed record ApplicabilityCondition(string Fact, string Predicate, string Status, SourceRef[] SourceRefs);
