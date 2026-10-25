using System.Net;
using AlfaHackaton.Result;
using AlfaHackaton.Result.Errors;
using InsightService.Contracts.Analyses;
using InsightService.Contracts.Context;

namespace InsightService.Models.Policies;

internal static class FinancialSemanticsPolicy
{
    public static async Task<Result> Validate(EffectDraft draft, Guid company,
        Func<string, string, Task<Result<MaterialVersion>>> material)
    {
        // Keep historical writes and qualitative missing-data questions compatible.
        if (draft.Question is not { } question || draft.Calculation is null)
        {
            return new();
        }

        var objectId = draft.Impact?.EconomicObjectId;
        if (string.IsNullOrWhiteSpace(objectId) || objectId.Length > 500 || objectId.Any(char.IsControl))
        {
            return Invalid("A monetary question requires a documented economic object identifier.");
        }

        if (draft.SourceRefs is null || draft.SourceRefs.Length > 64 ||
            draft.Assessment is not { Conditions: { Length: <= 16 }, Applicability.SourceRefs: { Length: <= 16 } })
        {
            return Invalid("A monetary question requires bounded documentary exposure and conditions.");
        }

        var objectBound = false;
        var budgetBound = false;
        foreach (var reference in draft.Assessment?.Applicability?.SourceRefs ?? [])
        {
            if (reference is null || !draft.SourceRefs.Contains(reference))
            {
                continue;
            }
            var resolved = await material(reference.MaterialId, reference.VersionId);
            if (resolved.Error is { } error)
            {
                return new(error);
            }
            var source = resolved.Content!;
            if (source.MaterialId != reference.MaterialId || source.VersionId != reference.VersionId ||
                source.ContentHash != reference.ContentHash || source.Kind != "company" ||
                !source.CompanyIds.Contains(company) || source.SourceId == "provided-bank-csv")
            {
                continue;
            }

            objectBound |= ContainsIdentifier(source.Text, objectId);
            if (question.Frame == "budget_delta" && question.BaselinePeriod is { } baseline &&
                baseline.StartsWith("budget:", StringComparison.Ordinal))
            {
                // A model-created label or an ordinary contractual rate is not budget evidence.
                // Require an observed, exact excerpt identifying this budget and this object.
                budgetBound |= draft.Assessment!.Conditions.Any(c =>
                    c is { Status: "supported", Assertion: "observed", Predicate: "budget_baseline", SourceRefs: not null, DependsOn.Length: 0 } &&
                    c.SourceRefs.Contains(reference) && c.Fact is { Length: > 0 and <= 1000 } &&
                    source.Text.Contains(c.Fact, StringComparison.Ordinal) &&
                    ContainsIdentifier(c.Fact, baseline) && ContainsIdentifier(c.Fact, objectId));
            }
        }

        if (!objectBound)
        {
            return Invalid("The economic object identifier must occur in an exact cited own exposure document.");
        }
        return question.Frame == "budget_delta" && !budgetBound
            ? Invalid("A budget delta requires an observed budget_baseline excerpt binding the exact budget and economic object in an own document.")
            : new();
    }

    private static bool ContainsIdentifier(string text, string identifier)
    {
        var start = 0;
        while (start < text.Length)
        {
            var index = text.IndexOf(identifier, start, StringComparison.Ordinal);
            if (index < 0)
            {
                return false;
            }
            var end = index + identifier.Length;
            if ((index == 0 || !IdentifierCharacter(text[index - 1])) &&
                (end == text.Length || !IdentifierCharacter(text[end])))
            {
                return true;
            }
            start = index + 1;
        }
        return false;
    }

    private static bool IdentifierCharacter(char value) => char.IsLetterOrDigit(value) || value is '_' or '-' or '/';

    private static Result Invalid(string message) => new(new Error(message, ErrorSource.Client, ErrorType.CorruptedInfo)
    { HttpStatusCode = HttpStatusCode.UnprocessableEntity });
}
