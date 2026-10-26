using InsightService.Middleware;
using InsightService.Services.PublicReads;
using InsightService.Services.PublicReadTransport;
using Microsoft.AspNetCore.Mvc;

namespace InsightService.Extensions;

public static class PublicInsightReadRegistration
{
    public static IServiceCollection AddPublicInsightReads(this IServiceCollection services)
    {
        services.AddHttpClient(nameof(PublicInsightHttpClients), client => client.Timeout = TimeSpan.FromSeconds(15))
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false });
        services.AddScoped<PublicInsightHttpClients>();
        services.AddScoped<IPublicInsightMembershipResolver>(provider => provider.GetRequiredService<PublicInsightHttpClients>());
        services.AddScoped<IPublicInsightSourceResolver>(provider => provider.GetRequiredService<PublicInsightHttpClients>());
        services.AddScoped<IPublicInsightReadStore, LocalPublicInsightReadStore>();
        services.AddScoped<PublicInsightReadService>();
        services.Configure<ApiBehaviorOptions>(options =>
        {
            var previous = options.InvalidModelStateResponseFactory;
            options.InvalidModelStateResponseFactory = context => PublicInsightReadMiddleware.IsPublicInsightRead(context.HttpContext.Request.Path)
                ? new BadRequestObjectResult(new { code = "INVALID_REQUEST", message = "Invalid insight read request." }) : previous(context);
        });
        return services;
    }
}
