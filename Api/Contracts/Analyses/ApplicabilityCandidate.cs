using InsightService.Contracts.Questions;

namespace InsightService.Contracts.Analyses;

public sealed class ApplicabilityCandidate
{
    public QuestionAssessment? Assessment { get; set; }
    public string? EvidenceStatus { get; set; }
    public bool HasCalculation { get; set; }
}
