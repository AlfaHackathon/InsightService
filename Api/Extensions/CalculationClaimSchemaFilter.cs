using InsightService.Contracts.Analyses;
using Microsoft.OpenApi.Any;
using Microsoft.OpenApi.Models;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace InsightService.Extensions;

public sealed class CalculationClaimSchemaFilter : ISchemaFilter
{
    public void Apply(OpenApiSchema schema, SchemaFilterContext context)
    {
        if (context.Type == typeof(AnalysisReceipt))
        {
            schema.Properties["outcome"].Description = "Internal processing outcome: no_insight means no new revision/card; insights_saved means one or more grounded revisions. Legacy receipts omit this field. Completion is not proof that all missing facts were resolved.";
            schema.Properties["outcome"].Enum = [new OpenApiString("no_insight"), new OpenApiString("insights_saved")];
            schema.Properties["unpublishedDrafts"].Description = "Internal validated missing-data diagnostics, never canonical revisions or frontend cards. Exact evidence and missing facts retained for replay/audit. Does not mutate earlier heads/history or turn technical failures into successful analysis.";
            return;
        }
        if (context.Type != typeof(CalculationClaim))
        {
            return;
        }
        schema.Description = "Optional for legacy records and qualitative conclusions. New calculated writes with a numeric result in conclusion require one exact decimal-and-unit quote, canonical decimal value equal to the independently calculated result, matching unit and period. Supported quote: signed invariant decimal, optional space grouping, ISO literal unit or RUB/₽/руб. No scales, comma decimals or worded amounts. Other numbers in calculated conclusion are limited to its declared period. This validates literal consistency, not arbitrary prose or real-world financial truth. Rejected claims return repairable 422 CONCLUSION_*; conclusion is never rewritten. Internal evidence/audit field, not a frontend text template.";
        schema.Required.UnionWith(["quote", "value", "unit", "period"]);
        foreach (var (name, limit) in new[] { ("quote", 200), ("value", 100), ("unit", 64), ("period", 1000) })
        {
            if (schema.Properties.TryGetValue(name, out var property))
            {
                property.MinLength = 1;
                property.MaxLength = limit;
            }
        }
        schema.Example = new OpenApiObject
        {
            ["quote"] = new OpenApiString("8 000 RUB"),
            ["value"] = new OpenApiString("8000"),
            ["unit"] = new OpenApiString("RUB"),
            ["period"] = new OpenApiString("2027")
        };
    }
}
