using System.Net;
using AlfaHackaton.HttpClient.Common.Interfaces;
using AlfaHackaton.Result;
using AlfaHackaton.Result.Errors;
using InsightService.Contracts.Execution;
using InsightService.Contracts.Projections;
using InsightService.Settings;

namespace InsightService.Services;

public sealed class CompanyKnowledgeClient(IHttpClient<CompanyHttpSettings> client, IConfiguration configuration)
{
    public Task<Result<SourceEventPage>> ChangesAsync(long after, CancellationToken ct) =>
        SendAsync<SourceEventPage>($"changes?after={after.ToString(System.Globalization.CultureInfo.InvariantCulture)}", ct);
    public Task<Result<CompanyRoleScope>> ScopeAsync(
        DateTimeOffset asOf,
        string[] personIds,
        Guid[] companyIds,
        CancellationToken ct) => SendAsync<CompanyRoleScope>("role-scope", ct, new { asOf, personIds, companyIds });
    public Task<Result<CompanyGroupPage>> GroupsAsync(DateTimeOffset asOf, CancellationToken ct) =>
        SendAsync<CompanyGroupPage>($"groups?asOf={Uri.EscapeDataString(asOf.ToString("O", System.Globalization.CultureInfo.InvariantCulture))}", ct);
    public Task<Result<CompanyIdentity>> ProfileAsync(
        Guid companyId,
        DateTimeOffset asOf,
        CancellationToken ct) => SendAsync<CompanyIdentity>($"companies/{companyId:D}?asOf={Uri.EscapeDataString(asOf.ToString("O", System.Globalization.CultureInfo.InvariantCulture))}", ct);
    public Task<Result<RoleProfile>> RoleAsync(
        string personId,
        Guid companyId,
        DateTimeOffset asOf,
        CancellationToken ct) => SendAsync<RoleProfile>($"role-scope?personId={Uri.EscapeDataString(personId)}&companyId={companyId:D}&asOf={Uri.EscapeDataString(asOf.ToString("O", System.Globalization.CultureInfo.InvariantCulture))}", ct);

    public Task<Result<GroupExpansion>> GroupAsync(
        string groupId,
        DateTimeOffset asOf,
        CancellationToken ct) => SendAsync<GroupExpansion>($"groups/{Uri.EscapeDataString(groupId)}?asOf={Uri.EscapeDataString(asOf.ToString("O", System.Globalization.CultureInfo.InvariantCulture))}", ct);

    private async Task<Result<T>> SendAsync<T>(
        string path,
        CancellationToken ct,
        object? body = null)
    {
        var secret = configuration["CompanyService:InternalSecret"] ?? configuration["Internal:Secret"];
        if (!Uri.TryCreate(configuration["CompanyService:BaseUrl"], UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https") || string.IsNullOrEmpty(secret))
        {
            return new(new Error("CompanyService URL and internal credential must be configured.", ErrorSource.External, ErrorType.Undefined) { HttpStatusCode = HttpStatusCode.ServiceUnavailable });
        }
        try
        {
            var url = uri.ToString().TrimEnd('/') + "/company-service/internal/knowledge/" + path;
            var response = body is null
                ? await client.Get<T>(url, (headers, _) => headers.Add("X-Internal-Secret", secret), ct)
                : await client.Post<object, T>(url, body, (headers, _) => headers.Add("X-Internal-Secret", secret), ct);
            if (response.Status == HttpStatusCode.NotFound)
            {
                return new(new Error("The requested company scope does not exist at asOf.", ErrorSource.Client, ErrorType.NotFound) { HttpStatusCode = HttpStatusCode.NotFound });
            }
            if (!response.IsSuccessStatusCode || response.Result is null)
            {
                return new(new Error("CompanyService rejected scope resolution.", ErrorSource.External, ErrorType.Undefined) { HttpStatusCode = HttpStatusCode.BadGateway });
            }
            return new(response.Result, null);
        }
        catch (HttpRequestException)
        {
            return new(new Error("CompanyService could not be reached.", ErrorSource.External, ErrorType.Undefined) { HttpStatusCode = HttpStatusCode.ServiceUnavailable });
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new(new Error("CompanyService timed out.", ErrorSource.External, ErrorType.Undefined) { HttpStatusCode = HttpStatusCode.ServiceUnavailable });
        }
        catch (Newtonsoft.Json.JsonException)
        {
            return new(new Error("CompanyService returned an invalid scope.", ErrorSource.External, ErrorType.Undefined) { HttpStatusCode = HttpStatusCode.BadGateway });
        }
    }
}
