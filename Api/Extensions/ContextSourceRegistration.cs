using AlfaHackaton.HttpClient;
using InsightService.Settings;
using InsightService.Services;
using InsightService.Services.Interfaces;
namespace InsightService.Extensions;

public static class ContextSourceRegistration
{
    public static IServiceCollection AddContextSourceResolver(this IServiceCollection services)
    {
        services.AddAlfaHttpClient(new ContextHttpSettings())
            .ConfigureHttpClient(client => client.MaxResponseContentBufferSize = 4 * 1024 * 1024);
        services.AddTransient<ISourceResolver, ContextSourceResolver>();
        return services;
    }
}
