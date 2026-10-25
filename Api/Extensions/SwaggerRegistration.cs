using Microsoft.OpenApi.Models;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace InsightService.Extensions;

public static class SwaggerRegistration
{
    public static IServiceCollection AddServiceSwagger(this IServiceCollection services)
    {
        services.AddSwaggerGen(options =>
        {
            options.SwaggerDoc("v1", new OpenApiInfo
            {
                Title = "InsightService API",
                Version = "v1",
                Description = "Current-company public insight reads use Bearer through AuthGateway. Internal execution routes require a service credential and are not public frontend APIs. Documentation does not grant access; never place an internal credential in frontend code."
            });
            options.AddSecurityDefinition("InternalSecret", new OpenApiSecurityScheme
            {
                Type = SecuritySchemeType.ApiKey,
                In = ParameterLocation.Header,
                Name = "X-Internal-Secret",
                Description = "Configured service credential; user Bearer tokens do not authorize internal routes."
            });
            options.AddSecurityDefinition("GatewayBearer", new OpenApiSecurityScheme
            {
                Type = SecuritySchemeType.Http,
                Scheme = "bearer",
                BearerFormat = "JWT",
                Description = "External Bearer validated by AuthGateway. InsightService requires trusted gateway identity and private ingress; it does not accept client-selected company scope."
            });
            options.OperationFilter<InternalSwaggerOperationFilter>();
            options.OperationFilter<PublicInsightOperationFilter>();
            options.SchemaFilter<PublicInsightSchemaFilter>();
            options.SchemaFilter<CompanyImportanceSchemaFilter>();
        });
        return services;
    }

    public static WebApplication UseServiceSwagger(this WebApplication app)
    {
        if (!app.Configuration.GetValue<bool>("Swagger:Enabled"))
        {
            return app;
        }
        app.UseSwagger(options => options.RouteTemplate = "insight-service/swagger/{documentName}/swagger.json");
        app.UseSwaggerUI(options =>
        {
            options.RoutePrefix = "insight-service/swagger";
            options.SwaggerEndpoint("v1/swagger.json", "InsightService API v1");
            options.ConfigObject.ValidatorUrl = null;
        });
        return app;
    }
}

public sealed class InternalSwaggerOperationFilter : IOperationFilter
{
    public void Apply(OpenApiOperation operation, OperationFilterContext context)
    {
        var internalRoute = context.ApiDescription.RelativePath?.StartsWith("insight-service/internal", StringComparison.Ordinal) == true;
        operation.Tags = [new OpenApiTag { Name = internalRoute ? "Internal — service credential required" : "Operations" }];
        if (!internalRoute)
        {
            return;
        }
        operation.Security = [new OpenApiSecurityRequirement
        {
            [new OpenApiSecurityScheme
            {
                Reference = new OpenApiReference { Type = ReferenceType.SecurityScheme, Id = "InternalSecret" }
            }] = Array.Empty<string>()
        }];
        operation.Responses.TryAdd("403", new OpenApiResponse { Description = "Missing or invalid configured internal credential." });
        operation.Responses.TryAdd("400", new OpenApiResponse { Description = "Invalid body/parameters. Domain errors may use errorMessage; middleware errors use code/message." });
        operation.Responses.TryAdd("503", new OpenApiResponse { Description = "Storage or upstream unavailable; no successful result is implied." });
    }
}
