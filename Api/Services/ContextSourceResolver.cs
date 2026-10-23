using AlfaHackaton.HttpClient.Common.Interfaces;
using InsightService.Settings;
using InsightService.Contracts.Context;
using InsightService.Models;
using InsightService.Services.Interfaces;
using System.Net;
namespace InsightService.Services;

public sealed class ContextSourceResolver(IHttpClient<ContextHttpSettings> client, IConfiguration configuration) : ISourceResolver
{
    public async Task<MaterialVersion> Get(string materialId, string versionId, CancellationToken cancellationToken)
    {
        var baseUrl = configuration["ContextService:BaseUrl"];
        var secret = configuration["ContextService:InternalSecret"] ?? configuration["Internal:Secret"];
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") || string.IsNullOrEmpty(secret))
            throw new ApiException(503, "context_not_configured", "ContextService URL and internal credential must be configured.");
        var url = uri.ToString().TrimEnd('/') + "/context-service/internal/material-versions/" + Uri.EscapeDataString(materialId) + "/" + Uri.EscapeDataString(versionId);
        try
        {
            var response = await client.Get<MaterialVersion>(url,
                (headers, _) => headers.Add("X-Internal-Secret", secret), cancellationToken);
            if (response.Status == HttpStatusCode.NotFound)
                throw new ApiException(422, "unknown_source", "A referenced material version does not exist.");
            if (!response.IsSuccessStatusCode)
                throw new ApiException(502, "context_rejected", "ContextService rejected source resolution; no effect was saved.");
            var material = response.Result ?? throw new ApiException(502, "invalid_context_response", "ContextService returned no material.");
            if (material.KnownAt == default || material.CompanyIds is null || material.BankProfileIds is null ||
                material.Kind is not ("news" or "company" or "market") || material.ContentHash is null ||
                material.ContentHash.Length != 64 || !material.ContentHash.All(Uri.IsHexDigit))
                throw new ApiException(502, "invalid_context_response", "ContextService returned an invalid material payload.");
            return material;
        }
        catch (HttpRequestException)
        { throw new ApiException(503, "context_unavailable", "ContextService could not be reached; no effect was saved."); }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { throw new ApiException(503, "context_timeout", "ContextService timed out; no effect was saved."); }
        catch (Newtonsoft.Json.JsonException)
        { throw new ApiException(502, "invalid_context_response", "ContextService returned an invalid material response."); }
    }
}
