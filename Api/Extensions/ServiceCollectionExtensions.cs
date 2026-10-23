using AlfaHackaton.DapperContext;
using InsightService.Repositories;
using InsightService.Repositories.Interfaces;
using InsightService.Services;
using InsightService.Services.Interfaces;
using InsightService.Settings;
using Microsoft.AspNetCore.Mvc;
namespace InsightService.Extensions;

public static class ServiceRegistration
{
    public static void AddInsightServices(this WebApplicationBuilder builder)
    {
        builder.WebHost.ConfigureKestrel(o => o.Limits.MaxRequestBodySize = 1024 * 1024);
        builder.Services.AddControllers(options => options.SuppressImplicitRequiredAttributeForNonNullableReferenceTypes = true)
            .ConfigureApiBehaviorOptions(options => options.InvalidModelStateResponseFactory = _ =>
                new BadRequestObjectResult(new
                {
                    code = "invalid_request",
                    message = "The request body or parameters are invalid."
                }));
        var database = DatabaseSettings.FromConfiguration(builder.Configuration);
        builder.Services.AddDapper(database).AddMigrations(database, typeof(Program).Assembly);
        builder.Services.AddScoped<IEffectRepository, EffectRepository>();
        builder.Services.AddContextSourceResolver();
        builder.Services.AddScoped<IAnalysisService, AnalysisService>();
        builder.Services.AddScoped<ProjectionService>();
        builder.Services.AddScoped<AnalysisPreparationService>();
        builder.Services.AddScoped<ExecutionJobReader>();
        builder.Services.AddScoped<CompanyExecutionService>();
        builder.Services.AddScoped<JobProjectionService>();
        builder.Services.AddScoped<SourceJobPlanningService>();
        builder.Services.AddScoped<AnalysisSessionService>();
        builder.Services.AddScoped<INewsJobRepository, NewsJobRepository>();
    }
}
