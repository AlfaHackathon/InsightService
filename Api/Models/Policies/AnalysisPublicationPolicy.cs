using InsightService.Contracts.Analyses;

namespace InsightService.Models.Policies;

internal static class AnalysisPublicationPolicy
{
    // Validated missing-data outcomes are processed diagnostics, not client insights.
    // Grounded contradictions remain separate review revisions. Historical receipts
    // replay before this new-write boundary and are never rewritten.
    internal static bool IsDiagnostic(EffectDraft draft) => draft.Calculation is null &&
        (draft.Impact?.EvidenceStatus == "unknown" || draft.Impact is null &&
            (draft.Status == "unknown" || draft.Question is not null));
}
