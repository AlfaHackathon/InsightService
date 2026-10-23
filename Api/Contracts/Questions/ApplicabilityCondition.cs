using InsightService.Contracts.Context;

namespace InsightService.Contracts.Questions;

public sealed record ApplicabilityCondition(string Fact, string Predicate, string Status, SourceRef[] SourceRefs);
