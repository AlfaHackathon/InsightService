using System.Net;
using System.Text.RegularExpressions;
using AlfaHackaton.Result.Errors;
using InsightService.Contracts.Analyses;

namespace InsightService.Models.Policies;

internal static partial class InsightTitlePolicy
{
    internal const int MaximumLength = 180;

    [GeneratedRegex(@"(?i)(?:\b(?:revision_|question-|news_)[0-9a-f]{16,}\b|\b[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}\b)", RegexOptions.CultureInvariant)]
    private static partial Regex OpaqueIdentity();

    public static Error? Validate(EffectDraft draft)
    {
        if (draft.Title is null)
        {
            return AnalysisPublicationPolicy.IsDiagnostic(draft) ? null : Invalid("TITLE_REQUIRED", "Provide a separate meaningful title for a new published insight.");
        }
        if (string.IsNullOrWhiteSpace(draft.Title) || draft.Title.Length > MaximumLength || draft.Title.Any(c => char.IsControl(c) || c is '\u2028' or '\u2029'))
        {
            return Invalid("TITLE_INVALID", "Title must be a nonblank single line of at most 180 characters.");
        }
        if (AnalysisPublicationPolicy.IsDiagnostic(draft))
        {
            return null; // Internal diagnostics do not need a fabricated public headline.
        }
        var identifiers = new[] { draft.EffectKey, draft.LiteralObjectRef, draft.ExpectedPreviousRevisionId, draft.Impact?.EconomicObjectId }
            .Concat(draft.SourceRefs.SelectMany(reference => new[] { reference.MaterialId, reference.VersionId, reference.ContentHash }));
        if (OpaqueIdentity().IsMatch(draft.Title) || identifiers.Any(id => id is { Length: >= 12 } && draft.Title.Contains(id, StringComparison.Ordinal)))
        {
            return Invalid("TITLE_INVALID", "Use a client-facing title without internal source or object identifiers.");
        }
        if (draft.Calculation is null && CalculationClaimPolicy.ContainsNumericClaim(draft.Title))
        {
            return Invalid("TITLE_NUMERIC_REQUIRES_CALCULATION", "Keep title qualitative unless its numeric result is bound to an explicit calculation.");
        }
        return null;
    }

    private static Error Invalid(string code, string reason) => new($"{code}: {reason}", ErrorSource.Client, ErrorType.CorruptedInfo)
    { HttpStatusCode = HttpStatusCode.UnprocessableEntity };
}
