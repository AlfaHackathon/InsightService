using System.Text.Json;
using System.Text.RegularExpressions;
using InsightService.Common;

namespace InsightService.Repositories;

internal sealed record InsightReadCursor(int Version, Guid CompanyId, string Kind, string Filter,
    long After, string Id, int Index = 0)
{
    internal string Encode() => EncodeValue(this);
    internal static string EncodeValue<T>(T value) => Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(value, AnalysisProtocol.Json))
        .TrimEnd('=').Replace('+', '-').Replace('/', '_');
    internal static T DecodeValue<T>(string text)
    {
        if (text.Length > 2048 || !Regex.IsMatch(text, "^[A-Za-z0-9_-]+$", RegexOptions.CultureInvariant))
        {
            throw new BadHttpRequestException("Invalid read cursor.");
        }
        try
        {
            var encoded = text.Replace('-', '+').Replace('_', '/');
            encoded = encoded.PadRight(encoded.Length + (4 - encoded.Length % 4) % 4, '=');
            return JsonSerializer.Deserialize<T>(Convert.FromBase64String(encoded), AnalysisProtocol.Json)
                ?? throw new BadHttpRequestException("Invalid read cursor.");
        }
        catch (Exception error) when (error is FormatException or JsonException)
        {
            throw new BadHttpRequestException("Invalid read cursor.");
        }
    }
    internal static InsightReadCursor? Read(string? text, Guid company, string kind, string filter)
    {
        if (text is null)
        {
            return null;
        }
        var value = DecodeValue<InsightReadCursor>(text);
        if (value.Version != 1 || value.CompanyId != company || value.Kind != kind || value.Filter != filter ||
            value.After < 0 || string.IsNullOrWhiteSpace(value.Id) || value.Id.Length > 200 || value.Index is < 0 or > 23)
        {
            throw new BadHttpRequestException("Invalid read cursor.");
        }
        return value;
    }
}
