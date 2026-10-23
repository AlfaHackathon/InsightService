using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using InsightService.Common;
namespace InsightService.Repositories;

internal static class PersistenceIdentity
{
    internal static string StableId(string kind, params string[] components) => kind + "_" +
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(components, AnalysisProtocol.Json))));
}
