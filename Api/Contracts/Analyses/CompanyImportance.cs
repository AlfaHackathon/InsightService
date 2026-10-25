using System.ComponentModel.DataAnnotations;
using InsightService.Contracts.Context;

namespace InsightService.Contracts.Analyses;

// Per-company decision importance; null score means not assessed, never low importance.
// Independent of confidence, monetary magnitude/sign and legacy ranking components.
public sealed record CompanyImportance(
    [property: Range(1, 5)] int? Score,
    [property: Required, MinLength(1), MaxLength(2000)] string Rationale,
    [property: Required, MaxLength(16)] SourceRef[] EvidenceRefs);
