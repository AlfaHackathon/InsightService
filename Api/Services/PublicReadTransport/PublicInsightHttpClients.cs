using System.Text.Json;
using InsightService.Contracts.Context;
using InsightService.Contracts.PublicReads;
using InsightService.Services.PublicReads;

namespace InsightService.Services.PublicReadTransport;

// Only fixed internal owner routes; request callers never choose URLs or credentials.
public sealed class PublicInsightHttpClients(IHttpClientFactory clients, IConfiguration configuration)
    : IPublicInsightMembershipResolver, IPublicInsightSourceResolver
{
    private sealed record Membership(Guid Id, string Name, string? BankProfileId);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { MaxDepth = 64 };

    public async Task<PublicInsightMembership?> Get(Guid userId, CancellationToken ct)
    {
        var value = await Read<Membership>("CompanyService", $"company-service/internal/companies/by-user/{userId:D}", ct);
        if (value is null)
            return null;
        if (value.Id == Guid.Empty || value.Name is null || value.BankProfileId?.Length > 200)
            throw PublicInsightReadException.Unavailable();
        return new(value.Id, value.BankProfileId);
    }

    public Task<MaterialVersion?> Get(string materialId, string versionId, CancellationToken ct) =>
        Read<MaterialVersion>("ContextService", $"context-service/internal/material-versions/{Uri.EscapeDataString(materialId)}/{Uri.EscapeDataString(versionId)}", ct);

    private async Task<T?> Read<T>(string service, string path, CancellationToken ct) where T : class
    {
        var root = configuration[service + ":BaseUrl"];
        var secret = configuration[service + ":InternalSecret"] ?? configuration["Internal:Secret"];
        if (!Uri.TryCreate(root?.TrimEnd('/') + "/", UriKind.Absolute, out var address) ||
            address.Scheme is not ("http" or "https") || address.UserInfo.Length != 0 ||
            address.Query.Length != 0 || address.Fragment.Length != 0 || string.IsNullOrEmpty(secret))
            throw PublicInsightReadException.Unavailable();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        using var client = clients.CreateClient(nameof(PublicInsightHttpClients));
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(address, path));
        request.Headers.Add("X-Internal-Secret", secret);
        try
        {
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                return null;
            if (!response.IsSuccessStatusCode)
                throw PublicInsightReadException.Unavailable();
            const int maximum = 4 * 1024 * 1024;
            if (response.Content.Headers.ContentLength > maximum)
                throw PublicInsightReadException.Unavailable();
            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
            using var buffer = new MemoryStream();
            var chunk = new byte[8192];
            int count;
            while ((count = await stream.ReadAsync(chunk, timeout.Token)) > 0)
            {
                if (buffer.Length + count > maximum)
                    throw PublicInsightReadException.Unavailable();
                buffer.Write(chunk, 0, count);
            }
            return JsonSerializer.Deserialize<T>(buffer.ToArray(), Json) ?? throw PublicInsightReadException.Unavailable();
        }
        catch (Exception error) when (error is HttpRequestException or JsonException or IOException or OperationCanceledException)
        {
            throw PublicInsightReadException.Unavailable();
        }
    }
}
