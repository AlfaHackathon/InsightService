using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using InsightService.Contracts.Analyses;
namespace InsightService.Common;

public static class AnalysisProtocol
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public const string ReceiptSemantics = "saved_analysis_revisions_not_latest_heads";
    public static AnalysisReceipt Duplicate(AnalysisReceipt receipt) => receipt with
    {
        Duplicate = true,
        Effects = receipt.Effects.Select(x => x with { Duplicate = true }).ToArray()
    };
    public static string ProcessingVersion(string newsVersionId, MaterialTrigger? trigger) => trigger is null ? newsVersionId :
        "trigger-" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new[] { newsVersionId, trigger.MaterialId, trigger.VersionId }, Json))));
}
