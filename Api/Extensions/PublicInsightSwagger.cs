using InsightService.Contracts.PublicReads;
using InsightService.Controllers;
using Microsoft.OpenApi.Any;
using Microsoft.OpenApi.Models;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace InsightService.Extensions;

public sealed class PublicInsightOperationFilter : IOperationFilter
{
    public void Apply(OpenApiOperation operation, OperationFilterContext context)
    {
        if (context.MethodInfo.DeclaringType != typeof(PublicInsightReadsController))
        {
            return;
        }

        operation.Tags = [new OpenApiTag { Name = "Public insights — Bearer via AuthGateway" }];
        operation.Description = "Current company and bank scope are resolved server-side on every request, including history. Exact sources are verified before projection. Source-denied feed items may be omitted while the original cursor advances; exhaustive is false. No internal secret is accepted as user authority. Responses are private, no-store.";
        operation.Security = [new OpenApiSecurityRequirement
        {
            [new OpenApiSecurityScheme { Reference = new OpenApiReference { Type = ReferenceType.SecurityScheme, Id = "GatewayBearer" } }] = Array.Empty<string>()
        }];
        foreach (var parameter in operation.Parameters.Where(parameter => parameter.Name == "Category" || parameter.Name == "category"))
        {
            parameter.Schema.Enum = [new OpenApiString("risk"), new OpenApiString("opportunity"), new OpenApiString("event")];
            parameter.Description = "Filter the stored category for personal or general insights. Omit for all, including legacy null. Empty/unknown values and category=null are invalid; category is bound to the cursor.";
        }
        foreach (var item in new Dictionary<string, string>
        {
            ["400"] = "Invalid strict query, kind, identifier or company/filter-bound cursor.",
            ["401"] = "Trusted gateway user context missing or malformed.",
            ["403"] = "Current company membership is required.",
            ["404"] = "Insight/history missing or its sources are outside current permissions.",
            ["502"] = "Saved read/source contract, identity, hash, time or importance evidence mismatch.",
            ["503"] = "Source/dependency/storage unavailable or read budget exceeded; no empty success is implied."
        })
        {
            operation.Responses.TryAdd(item.Key, new OpenApiResponse { Description = item.Value });
        }
    }
}

public sealed class PublicInsightSchemaFilter : ISchemaFilter
{
    public void Apply(OpenApiSchema schema, SchemaFilterContext context)
    {
        if (context.Type == typeof(PublicInsightCard))
        {
            schema.Type = null;
            schema.Properties.Clear();
            schema.Required.Clear();
            schema.OneOf = [context.SchemaGenerator.GenerateSchema(typeof(PublicPersonalInsightCard), context.SchemaRepository),
                context.SchemaGenerator.GenerateSchema(typeof(PublicGeneralInsightCard), context.SchemaRepository)];
        }
        if (context.Type == typeof(PublicPersonalInsightCard) || context.Type == typeof(PublicGeneralInsightCard))
        {
            schema.Required.Add("kind");
            if (schema.Properties.TryGetValue("kind", out var kind))
            {
                kind.Enum = [new OpenApiString(context.Type == typeof(PublicPersonalInsightCard) ? "personal" : "general")];
            }
        }
        if (context.Type == typeof(PublicInsightImportance) && schema.Properties.TryGetValue("score", out var score))
        {
            score.Minimum = 1;
            score.Maximum = 5;
            score.Nullable = true;
        }
        if (context.Type == typeof(PublicInsightRevision) && schema.Properties.TryGetValue("category", out var category))
        {
            category.Nullable = true;
            category.Description = "Stored nullable category from the shared category contract. Absent remains null; never inferred from impact sign.";
        }
        if (context.Type == typeof(PublicInsightRevision) && schema.Properties.TryGetValue("title", out var title))
        {
            title.Nullable = true;
            title.MaxLength = 180;
            title.Description = "Verbatim stored title of this revision. Legacy null has no generated fallback. Personal feed/detail use currentRevision.title; history uses each items[].revision.title. General insights keep insight.title.";
        }
        if (context.Type == typeof(PublicInsightIndicator) && schema.Properties.TryGetValue("value", out var value))
        {
            value.Description = "Verbatim persisted Decimal result as a string. Missing is not zero; no browser-side recomputation.";
        }
        if (context.Type == typeof(PublicInsightSource))
        {
            if (schema.Properties.TryGetValue("url", out var url))
            {
                url.Nullable = true;
                url.Format = "uri";
                url.MaxLength = 4096;
                url.Description = "Exact authorized material provenance HTTP(S) URL, or null. No link is derived from IDs or text. Same value as provenance.sourceUrl.";
            }
            if (schema.Properties.TryGetValue("newsId", out var newsId))
            {
                newsId.Nullable = true;
                newsId.Description = "Nullable parser document identity. Current material provenance does not carry this identity, so null is returned; materialId and private locator are never substituted.";
            }
        }
    }
}
