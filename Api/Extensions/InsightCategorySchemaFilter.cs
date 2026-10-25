using InsightService.Contracts.Analyses;
using Microsoft.OpenApi.Any;
using Microsoft.OpenApi.Models;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace InsightService.Extensions;

public sealed class InsightCategorySchemaFilter : ISchemaFilter
{
    public void Apply(OpenApiSchema schema, SchemaFilterContext context)
    {
        if ((context.Type == typeof(EffectDraft) || context.Type == typeof(EffectRevision)) && schema.Properties.TryGetValue("category", out var category))
        {
            category.Nullable = true;
            category.Enum = new List<IOpenApiAny> { new OpenApiString("risk"), new OpenApiString("opportunity"), new OpenApiString("event") };
            category.Description = "Risk (Риск): supported adverse effect for scope. Opportunity (Возможность): supported beneficial effect. Event (Событие): established significant event without a directional effect. Null: unclassified, uncertain, mixed without a supported predominant effect, or legacy. Independent of kind, importance, epistemic status and provenance; no historical backfill.";
        }
    }
}
