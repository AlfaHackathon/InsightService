using System.Globalization;
using System.Net;
using AlfaHackaton.HttpClient.Common.Interfaces;
using AlfaHackaton.Result;
using AlfaHackaton.Result.Errors;
using InsightService.Contracts.Analyses;
using InsightService.Contracts.Execution;
using InsightService.Settings;

namespace InsightService.Services;

public sealed class ContextEvidenceClient(IHttpClient<ContextHttpSettings> client, IConfiguration configuration)
{
    public Task<Result<DepthInventory>> InventoryAsync(
        Guid companyId,
        DateTimeOffset asOf,
        CancellationToken ct) =>
        GetAsync<DepthInventory>($"companies/{companyId:D}/evidence?asOf=" + Uri.EscapeDataString(asOf.ToString("O", CultureInfo.InvariantCulture)), ct);
    public Task<Result<SourceEventPage>> ChangesAsync(long after, CancellationToken ct) =>
        GetAsync<SourceEventPage>($"changes?after={after.ToString(CultureInfo.InvariantCulture)}", ct);

    private async Task<Result<T>> GetAsync<T>(string path, CancellationToken ct)
    {
        var secret = configuration["ContextService:InternalSecret"] ?? configuration["Internal:Secret"];
        if (!Uri.TryCreate(configuration["ContextService:BaseUrl"], UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") || string.IsNullOrEmpty(secret))
        {
            return new(Failure("ContextService URL and internal credential must be configured.", HttpStatusCode.ServiceUnavailable));
        }
        try
        {
            var url = uri.ToString().TrimEnd('/') + "/context-service/internal/" + path;
            var response = await client.Get<T>(url, (headers, _) => headers.Add("X-Internal-Secret", secret), ct);
            if (!response.IsSuccessStatusCode || response.Result is null)
            {
                return new(Failure("ContextService rejected evidence inventory resolution.", HttpStatusCode.BadGateway));
            }
            return new(response.Result, null);
        }
        catch (HttpRequestException)
        {
            return new(Failure("ContextService could not be reached.", HttpStatusCode.ServiceUnavailable));
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new(Failure("ContextService timed out.", HttpStatusCode.ServiceUnavailable));
        }
        catch (Newtonsoft.Json.JsonException)
        {
            return new(Failure("ContextService returned an invalid inventory.", HttpStatusCode.BadGateway));
        }
    }
    private static Error Failure(string message, HttpStatusCode status) => new(message, ErrorSource.External, ErrorType.Undefined) { HttpStatusCode = status };
}
