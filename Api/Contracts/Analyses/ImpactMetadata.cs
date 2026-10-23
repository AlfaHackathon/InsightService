namespace InsightService.Contracts.Analyses;

public sealed record ImpactMetadata(string Metric, string AmountBasis, string? EconomicObjectId,
    string EvidenceStatus, int Importance, int Urgency, int Novelty);
