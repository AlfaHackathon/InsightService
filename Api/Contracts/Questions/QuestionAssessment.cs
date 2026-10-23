
using System.Text.Json.Serialization;

namespace InsightService.Contracts.Questions;

public sealed record QuestionAssessment(ApplicabilityCondition[] Conditions, MissingFact[] Unknowns,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] CompanyApplicability? Applicability = null);
