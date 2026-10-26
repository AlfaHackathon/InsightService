using System.Text.Json;

namespace InsightService.Models.Policies;

/// <summary>Category is independent of priority, epistemic status and source provenance. Missing legacy values stay null.</summary>
public static class InsightCategoryPolicy
{
    public static bool IsValid(string? category) => category is null or "risk" or "opportunity" or "event";

    // Jobs store opaque checkpoints. Validate only category in the known general-insight locations,
    // leaving the rest of the checkpoint protocol to its owner. Omission supports existing workers.
    public static bool IsValidCheckpoint(JsonElement checkpoint)
    {
        if (checkpoint.ValueKind != JsonValueKind.Object)
        {
            return false;
        }
        foreach (var name in new[] { "plan", "result" })
        {
            if (checkpoint.TryGetProperty(name, out var plan) && !IsValidPlan(plan))
            {
                return false;
            }
        }
        if (checkpoint.TryGetProperty("segmentBatches", out var batches) && batches.ValueKind == JsonValueKind.Array)
        {
            foreach (var batch in batches.EnumerateArray())
            {
                if (batch.ValueKind == JsonValueKind.Object && batch.TryGetProperty("plan", out var plan) && !IsValidPlan(plan))
                {
                    return false;
                }
            }
        }
        return true;
    }

    private static bool IsValidPlan(JsonElement plan)
    {
        if (plan.ValueKind != JsonValueKind.Object || !plan.TryGetProperty("generalInsights", out var insights) || insights.ValueKind != JsonValueKind.Array)
        {
            return true;
        }
        foreach (var insight in insights.EnumerateArray())
        {
            if (insight.ValueKind == JsonValueKind.Object && insight.TryGetProperty("category", out var category) &&
                (category.ValueKind is not (JsonValueKind.Null or JsonValueKind.String) ||
                 category.ValueKind == JsonValueKind.String && !IsValid(category.GetString())))
            {
                return false;
            }
        }
        return true;
    }
}
