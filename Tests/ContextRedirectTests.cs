using System.Net;
using System.Net.Sockets;
using System.Text;
using InsightService.Extensions;
using InsightService.Services.Interfaces;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

public sealed class ContextRedirectTests
{
    [Fact]
    public async Task ConfiguredResolverRejectsRedirectWithoutContactingOtherOrigin()
    {
        const string syntheticSecret = "local-redirect-test-not-a-real-credential";
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var destinationDeadline = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
        var origin = new TcpListener(IPAddress.Loopback, 0);
        var destination = new TcpListener(IPAddress.Loopback, 0);
        origin.Start();
        destination.Start();
        try
        {
            var originUrl = $"http://127.0.0.1:{((IPEndPoint)origin.LocalEndpoint).Port}";
            var destinationUrl = $"http://127.0.0.1:{((IPEndPoint)destination.LocalEndpoint).Port}/redirected-material";
            var originObservation = Serve(origin, "302 Found", $"Location: {destinationUrl}\r\n", "", syntheticSecret, deadline.Token);
            var destinationObservation = ObserveDestination(destination, syntheticSecret, destinationDeadline.Token);
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["ContextService:BaseUrl"] = originUrl, ["ContextService:InternalSecret"] = syntheticSecret }).Build();
            var services = new ServiceCollection();
            services.AddSingleton<IConfiguration>(configuration);
            services.AddContextSourceResolver();
            using var provider = services.BuildServiceProvider();
            var resolver = provider.GetRequiredService<ISourceResolver>();
            var failure = await ResultAssert.ErrorAsync(() => resolver.GetAsync("news", "v1", deadline.Token));
            var originSawSecret = await originObservation;
            destinationDeadline.CancelAfter(TimeSpan.FromSeconds(1));
            var destinationSaw = await destinationObservation;
            Assert.True(originSawSecret);
            Assert.False(destinationSaw.RequestReceived);
            Assert.False(destinationSaw.SecretReceived);
            Assert.NotNull(failure);
            Assert.Equal(502, failure.StatusCode);
            Assert.Equal("context_rejected", failure.Code);
        }
        finally
        {
            origin.Stop();
            destination.Stop();
        }
    }

    private static async Task<(bool RequestReceived, bool SecretReceived)> ObserveDestination(TcpListener listener, string secret, CancellationToken ct)
    {
        try
        {
            var body = """
                {"materialId":"news","versionId":"v1","sourceId":"synthetic","kind":"news",
                "title":"Local test","text":"Synthetic source","knownAt":"2026-10-21T08:00:00Z",
                "publishedAt":null,"correctsVersionId":null,"companyIds":[],"bankProfileIds":[],
                "contentHash":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"}
                """;
            var secretReceived = await Serve(listener, "200 OK", "Content-Type: application/json\r\n", body, secret, ct);
            return (true, secretReceived);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return (false, false);
        }
    }

    private static async Task<bool> Serve(TcpListener listener, string status, string extraHeaders, string body, string secret, CancellationToken ct)
    {
        using var client = await listener.AcceptTcpClientAsync(ct);
        await using var stream = client.GetStream();
        var request = new StringBuilder();
        var buffer = new byte[2048];
        while (!request.ToString().Contains("\r\n\r\n", StringComparison.Ordinal))
        {
            var count = await stream.ReadAsync(buffer, ct);
            if (count == 0 || request.Length > 16384)
            {
                throw new IOException("Incomplete bounded local test request.");
            }

            request.Append(Encoding.ASCII.GetString(buffer, 0, count));
        }

        var sawSecret = request.ToString().Split("\r\n").Any(line => line.StartsWith("X-Internal-Secret:", StringComparison.OrdinalIgnoreCase) && line[(line.IndexOf(':') + 1)..].Trim() == secret);
        var payload = Encoding.UTF8.GetBytes(body);
        var headers = Encoding.ASCII.GetBytes($"HTTP/1.1 {status}\r\n{extraHeaders}Content-Length: {payload.Length}\r\nConnection: close\r\n\r\n");
        await stream.WriteAsync(headers, ct);
        await stream.WriteAsync(payload, ct);
        await stream.FlushAsync(ct);
        return sawSecret;
    }
}
