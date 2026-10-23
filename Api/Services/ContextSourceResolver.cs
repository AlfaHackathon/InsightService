using System.Net;
using AlfaHackaton.HttpClient.Common.Interfaces;
using AlfaHackaton.Result;
using AlfaHackaton.Result.Errors;
using InsightService.Contracts.Context;
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
            return new(new Error("ContextService URL and internal credential must be configured.", ErrorSource.External, ErrorType.Undefined) { HttpStatusCode = HttpStatusCode.ServiceUnavailable });
        }

        var url = uri.ToString().TrimEnd('/') + "/context-service/internal/material-versions/" + Uri.EscapeDataString(materialId) + "/" + Uri.EscapeDataString(versionId);
        try
        {
            var response = await client.Get<MaterialVersion>(url, (headers, _) => headers.Add("X-Internal-Secret", secret), cancellationToken);
            if (response.Status == HttpStatusCode.NotFound)
            {
                return new(new Error("A referenced material version does not exist.", ErrorSource.Client, ErrorType.CorruptedInfo) { HttpStatusCode = HttpStatusCode.UnprocessableEntity });
            }

            if (!response.IsSuccessStatusCode)
            {
                return new(new Error("ContextService rejected source resolution; no effect was saved.", ErrorSource.External, ErrorType.Undefined) { HttpStatusCode = HttpStatusCode.BadGateway });
            }

            var value = response.Result;
            if (value is null)
            {
                return new(new Error("ContextService returned no material.", ErrorSource.External, ErrorType.Undefined) { HttpStatusCode = HttpStatusCode.BadGateway });
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
                return new(new Error("ContextService returned an invalid material payload.", ErrorSource.External, ErrorType.Undefined) { HttpStatusCode = HttpStatusCode.BadGateway });
            }

            return new(material, null);
        }
        catch (HttpRequestException)
        {
            return new(new Error("ContextService could not be reached; no effect was saved.", ErrorSource.External, ErrorType.Undefined) { HttpStatusCode = HttpStatusCode.ServiceUnavailable });
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new(new Error("ContextService timed out; no effect was saved.", ErrorSource.External, ErrorType.Undefined) { HttpStatusCode = HttpStatusCode.ServiceUnavailable });
        }
        catch (Newtonsoft.Json.JsonException)
        {
            return new(new Error("ContextService returned an invalid material response.", ErrorSource.External, ErrorType.Undefined) { HttpStatusCode = HttpStatusCode.BadGateway });
        }
    }
}
