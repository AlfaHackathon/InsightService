using System.Text.Json.Serialization;
using InsightService.Contracts.Calculations;
using InsightService.Contracts.Context;
using InsightService.Contracts.Questions;

namespace InsightService.Contracts.Analyses;

public sealed record EffectDraft(string EffectKey, string LiteralObjectRef, string? ExpectedPreviousRevisionId,
    string Conclusion, string Status, SourceRef[] SourceRefs, Calculation? Calculation = null, ImpactMetadata? Impact = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] QuestionIdentity? Question = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] QuestionAssessment? Assessment = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ModelExplanation = null);
