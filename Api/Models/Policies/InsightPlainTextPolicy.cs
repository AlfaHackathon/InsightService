using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using AlfaHackaton.Result.Errors;
using InsightService.Contracts.Analyses;

namespace InsightService.Models.Policies;

internal static partial class InsightPlainTextPolicy
{
    [GeneratedRegex(@"\*\*|__|`|^[ \t]*(?:~{3,}|#{1,6}(?:[ \t]|\r?$))|!?\[[^\]\r\n]*\]\([^\)\r\n]*\)|!?\[[^\]\r\n]*\]\[[^\]\r\n]*\]|https?://", RegexOptions.Multiline | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Markup();

    [GeneratedRegex(@"(?:^|[ \t(])(?:\*[^*\s](?:[^*\r\n]*[^*\s])?\*|_[^_\s](?:[^_\r\n]*[^_\s])?_)(?=$|[ \t.,;:!?)]|\r?\n)", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex Emphasis();

    internal static bool IsPlain(string text) => !Markup().IsMatch(text) && !Emphasis().IsMatch(text);

    internal static Error? ValidateDraft(EffectDraft draft) =>
        draft.Title is { } title && !IsPlain(title) ? Invalid("title") :
        !IsPlain(draft.Conclusion) ? Invalid("conclusion") : null;

    internal static Error? ValidateCheckpoint(JsonElement checkpoint, JsonElement previous)
    {
        // Carry exact existing entries unchanged. This preserves legacy checkpoint
        // progress/replay without turning a status update into a history rewrite.
        // Newly generated or changed general insights must satisfy this contract.
        var existing = GeneralEntries(previous).Select(item => JsonSerializer.Serialize(item)).ToHashSet(StringComparer.Ordinal);
        foreach (var item in GeneralEntries(checkpoint))
        {
            if (existing.Contains(JsonSerializer.Serialize(item)))
            {
                continue;
            }
            if (item.ValueKind != JsonValueKind.Object)
            {
                return Invalid("generalInsights");
            }
            foreach (var name in new[] { "title", "conclusion" })
            {
                if (item.TryGetProperty(name, out var field) &&
                    (field.ValueKind != JsonValueKind.String || !IsPlain(field.GetString()!)))
                {
                    return Invalid("generalInsights." + name);
                }
            }
        }
        return null;
    }

    private static IEnumerable<JsonElement> GeneralEntries(JsonElement checkpoint)
    {
        if (checkpoint.ValueKind != JsonValueKind.Object)
        {
            yield break;
        }
        foreach (var name in new[] { "plan", "result" })
        {
            if (checkpoint.TryGetProperty(name, out var plan))
            {
                foreach (var item in PlanEntries(plan))
                {
                    yield return item;
                }
            }
        }
        if (checkpoint.TryGetProperty("segmentBatches", out var batches) && batches.ValueKind == JsonValueKind.Array)
        {
            foreach (var batch in batches.EnumerateArray())
            {
                if (batch.ValueKind == JsonValueKind.Object && batch.TryGetProperty("plan", out var plan))
                {
                    foreach (var item in PlanEntries(plan))
                    {
                        yield return item;
                    }
                }
            }
        }
    }

    private static JsonElement.ArrayEnumerator PlanEntries(JsonElement plan) =>
        plan.ValueKind == JsonValueKind.Object && plan.TryGetProperty("generalInsights", out var insights) && insights.ValueKind == JsonValueKind.Array
            ? insights.EnumerateArray() : default;

    private static Error Invalid(string field) => new($"INSIGHT_PLAIN_TEXT_REQUIRED: Use plain text in {field}; preserve paragraphs and exact numbers, and put source URLs in source metadata.", ErrorSource.Client, ErrorType.CorruptedInfo)
    { HttpStatusCode = HttpStatusCode.UnprocessableEntity };
}
