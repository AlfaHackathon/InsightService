using System.Net;
using AlfaHackaton.HttpClient.Common.Interfaces;
using AlfaHackaton.Result;
using InsightService.Contracts.Context;
using InsightService.Models;
using InsightService.Services.Interfaces;
using InsightService.Settings;

namespace InsightService.Services;

public sealed class ContextSourceResolver(IHttpClient<ContextHttpSettings> client, IConfiguration configuration) : ISourceResolver
{
    public async Task<Result<MaterialVersion>> GetAsync(string materialId, string versionId, CancellationToken cancellationToken)
    {
        var baseUrl = configuration["ContextService:BaseUrl"];
        var secret = configuration["ContextService:InternalSecret"] ?? configuration["Internal:Secret"];
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https") ||
            string.IsNullOrEmpty(secret))
        {
            return new(new ApiError(503, "context_not_configured", "ContextService URL and internal credential must be configured."));
        }

        var url = uri.ToString().TrimEnd('/') + "/context-service/internal/material-versions/" + Uri.EscapeDataString(materialId) + "/" + Uri.EscapeDataString(versionId);
        try
        {
            var response = await client.Get<MaterialVersion>(url, (headers, _) => headers.Add("X-Internal-Secret", secret), cancellationToken);
            if (response.Status == HttpStatusCode.NotFound)
            {
                return new(new ApiError(422, "unknown_source", "A referenced material version does not exist."));
            }

            if (!response.IsSuccessStatusCode)
            {
                return new(new ApiError(502, "context_rejected", "ContextService rejected source resolution; no effect was saved."));
            }

            var value = response.Result;
            if (value is null)
            {
                return new(new ApiError(502, "invalid_context_response", "ContextService returned no material."));
            }

            var material = value;
            if (material.KnownAt == default ||
                material.CompanyIds is null ||
                material.BankProfileIds is null ||
                material.Kind is not ("news" or "company" or "market") ||
                material.ContentHash is null ||
                material.ContentHash.Length != 64 ||
                !material.ContentHash.All(Uri.IsHexDigit))
            {
                return new(new ApiError(502, "invalid_context_response", "ContextService returned an invalid material payload."));
            }

            return new(material, null);
        }
        catch (HttpRequestException)
        {
            return new(new ApiError(503, "context_unavailable", "ContextService could not be reached; no effect was saved."));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new(new ApiError(503, "context_timeout", "ContextService timed out; no effect was saved."));
        }
        catch (Newtonsoft.Json.JsonException)
        {
            return new(new ApiError(502, "invalid_context_response", "ContextService returned an invalid material response."));
        }
    }
}
