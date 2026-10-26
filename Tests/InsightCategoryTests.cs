using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using InsightService.Common;
using InsightService.Contracts.Analyses;
using InsightService.Contracts.Reads;
using InsightService.Models.Policies;
using Xunit;

// Pure contract tests: no PostgreSQL, HTTP upstreams or model calls.
public sealed class InsightCategoryTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("risk")]
    [InlineData("opportunity")]
    [InlineData("event")]
    public void ValidValuesRemainIndependentOfStatusAndImportance(string? category)
    {
        Assert.True(InsightCategoryPolicy.IsValid(category));
        var revision = InsightReadTestData.Revision("r1", status: "forecast", importance: 4) with { Category = category };
        var json = JsonSerializer.Serialize(revision, AnalysisProtocol.Json);
        var restored = JsonSerializer.Deserialize<EffectRevision>(json, AnalysisProtocol.Json)!;
        Assert.Equal(category, restored.Category);
        Assert.Equal("forecast", restored.Status);
        Assert.Equal(4, restored.CompanyImportance!.Score);
        using var document = JsonDocument.Parse(json);
        Assert.True(document.RootElement.TryGetProperty("category", out _));
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("Risk")]
    [InlineData("mixed")]
    [InlineData("null")]
    public void InvalidValuesFailPolicyAndQueryValidation(string category)
    {
        Assert.False(InsightCategoryPolicy.IsValid(category));
        var options = new InsightReadOptions { Category = category };
        Assert.False(Validator.TryValidateObject(options, new ValidationContext(options), new List<ValidationResult>(), true));
    }

    [Fact]
    public void LegacyRevisionsReadAsNullWithoutInferringFromAmountsStatusOrPriority()
    {
        var node = System.Text.Json.Nodes.JsonNode.Parse(JsonSerializer.Serialize(InsightReadTestData.Revision("legacy", importance: 5), AnalysisProtocol.Json))!.AsObject();
        node.Remove("category");
        var revision = JsonSerializer.Deserialize<EffectRevision>(node.ToJsonString(), AnalysisProtocol.Json)!;
        Assert.Null(revision.Category);
        Assert.Equal(JsonValueKind.Null, JsonSerializer.SerializeToElement(revision, AnalysisProtocol.Json).GetProperty("category").ValueKind);
    }

    [Fact]
    public void OmittedOrNullLegacyDraftCategoryPreservesTheExistingIdempotencyHashShape()
    {
        var draft = new EffectDraft("legacy", "company:synthetic", null, "Synthetic legacy conclusion", "unknown", []);
        var json = JsonSerializer.Serialize(draft, AnalysisProtocol.Json);
        Assert.DoesNotContain("category", json, StringComparison.Ordinal);
        Assert.Equal(json, JsonSerializer.Serialize(draft with { Category = null }, AnalysisProtocol.Json));
        Assert.Contains("category", JsonSerializer.Serialize(draft with { Category = "risk" }, AnalysisProtocol.Json), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("risk")]
    [InlineData("opportunity")]
    [InlineData("event")]
    public void GeneralCheckpointPreservesCategoryAndRejectsInvalidValuesAtEveryKnownLocation(string category)
    {
        foreach (var path in new[] { "plan", "result", "segmentBatches" })
        {
            JsonElement Checkpoint(object? value) => path == "segmentBatches"
                ? JsonSerializer.SerializeToElement(new { segmentBatches = new[] { new { plan = new { generalInsights = new[] { new { category = value } } } } } })
                : JsonSerializer.SerializeToElement(new Dictionary<string, object> { [path] = new { generalInsights = new[] { new { category = value } } } });
            var checkpoint = Checkpoint(category);
            Assert.True(InsightCategoryPolicy.IsValidCheckpoint(checkpoint));
            Assert.Contains(category, checkpoint.GetRawText());
            Assert.True(InsightCategoryPolicy.IsValidCheckpoint(Checkpoint(null)));
            Assert.False(InsightCategoryPolicy.IsValidCheckpoint(Checkpoint("unknown")));
            Assert.False(InsightCategoryPolicy.IsValidCheckpoint(Checkpoint(3)));
        }
        Assert.True(InsightCategoryPolicy.IsValidCheckpoint(JsonSerializer.SerializeToElement(new { result = new { generalInsights = new[] { new { title = "Legacy" } } } })));
    }
}
