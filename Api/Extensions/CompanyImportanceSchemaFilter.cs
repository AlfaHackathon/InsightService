using InsightService.Contracts.Analyses;
using Microsoft.OpenApi.Any;
using Microsoft.OpenApi.Models;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace InsightService.Extensions;

public sealed class CompanyImportanceSchemaFilter : ISchemaFilter
{
    public void Apply(OpenApiSchema schema, SchemaFilterContext context)
    {
        if (context.Type != typeof(CompanyImportance))
        {
            return;
        }
        schema.Description = "Per-company decision importance: 1 low, 2 moderate, 3 needs attention, 4 high, 5 critical; null means not assessed. Not confidence, financial magnitude or risk/opportunity sign. Requires rationale and exact own-company evidence for an assessed score. Historical legacy importance is not converted into this assessment.";
        schema.Example = new OpenApiObject
        {
            ["score"] = new OpenApiInteger(4),
            ["rationale"] = new OpenApiString("Synthetic own-company dependency needs prompt review."),
            ["evidenceRefs"] = new OpenApiArray
            {
                new OpenApiObject
                {
                    ["materialId"] = new OpenApiString("synthetic-own"),
                    ["versionId"] = new OpenApiString("v1"),
                    ["contentHash"] = new OpenApiString(new string('a', 64))
                }
            }
        };
    }
}
