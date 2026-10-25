using System.Text.Json.Serialization;
using InsightService.Contracts.Calculations;
using InsightService.Contracts.Context;
using InsightService.Contracts.Questions;

namespace InsightService.Contracts.Analyses;

public sealed record EffectRevision(string RevisionId, string? PreviousRevisionId, string NewsVersionId,
    DateTimeOffset AsOf, string Conclusion, string Status, SourceRef[] SourceRefs,
    Calculation? Calculation, string? CalculationResult, bool? CalculationArithmeticOnly,
    DateTimeOffset CreatedAt, ImpactMetadata? Impact = null, string? NewsMaterialId = null, QuestionIdentity? Question = null,
    QuestionAssessment? Assessment = null, MaterialTrigger? Trigger = null, string? ModelExplanation = null,
    string? PublicationClass = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] CompanyImportance? CompanyImportance = null);
