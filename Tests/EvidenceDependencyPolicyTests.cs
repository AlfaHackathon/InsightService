using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AlfaHackaton.Result;
using InsightService.Contracts.Analyses;
using InsightService.Contracts.Context;
using InsightService.Contracts.Questions;
using InsightService.Models.Policies;
using InsightService.Services;
using InsightService.Services.Interfaces;
using Xunit;

public sealed class EvidenceDependencyPolicyTests
{
    private static readonly Guid Company = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private const string Hash = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    [Fact]
    public async Task UnknownIndexIdentityCannotSupportDerivedRateEvenWhenOverallImpactIsUnknown()
    {
        var assessment = JsonSerializer.Deserialize<QuestionAssessment>("""
            {"conditions":[
              {"fact":"Own floating loan principal RUB 1000000","predicate":"Principal is documented","status":"supported","sourceRefs":[{"materialId":"contract","versionId":"1","contentHash":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"}],"id":"principal","assertion":"observed","dependsOn":[]},
              {"fact":"Public index changes 10 to 12; contract index plus 2","predicate":"Loan rate changes from 12 to 14 percent","status":"supported","sourceRefs":[{"materialId":"contract","versionId":"1","contentHash":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"},{"materialId":"news","versionId":"1","contentHash":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"}],"id":"rate","assertion":"derived","dependsOn":["index_identity"]}
            ],"unknowns":[{"fact":"Whether the news index is the contract index","query":null,"scope":"company","materialId":"contract","id":"index_identity"}]}
            """, AnalysisService.Json)!;

        var result = await ApplicabilityPolicy.Validate(Draft(assessment), Company, Material);

        Assert.NotNull(result.Error);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, result.Error.HttpStatusCode);
        Assert.Equal("A supported derived condition requires supported evidence dependencies.", result.Error.Title);
    }

    [Fact]
    public async Task UnrelatedUnknownDoesNotDowngradeAnIndependentObservedPrincipal()
    {
        var result = await Validate(new([Condition("principal")], [Unknown("index_identity")]));
        Assert.Null(result.Error);
    }

    [Fact]
    public async Task MatchedIndexAndDocumentedExposureSupportTheDerivedRate()
    {
        var result = await Validate(new([Condition("index_identity"), Condition("rate", "derived", ["index_identity"])], [], Exposure()), "supported");
        Assert.Null(result.Error);
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("contradicted")]
    public async Task UnsupportedPrerequisiteCannotSupportAClaim(string status)
    {
        var result = await Validate(new([Condition("index_identity") with { Status = status }, Condition("rate", "derived", ["index_identity"])], []));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, result.Error?.HttpStatusCode);
    }

    [Fact]
    public async Task TransitiveUnknownPrerequisiteCannotSupportAClaim()
    {
        var result = await Validate(new([
            Condition("identity", "derived", ["missing_identity"]) with { Status = "unknown" },
            Condition("rate", "derived", ["identity"])], [Unknown("missing_identity")]));
        Assert.Equal("A supported derived condition requires supported evidence dependencies.", result.Error?.Title);
    }

    [Fact]
    public async Task ExplicitConditionalScenarioRemainsUnknownOnEstablishedExposure()
    {
        var result = await Validate(Scenario(), "conditional");
        Assert.Null(result.Error);
    }

    [Theory]
    [InlineData("supported", "conditional", true)]
    [InlineData("unknown", "unknown", true)]
    [InlineData("unknown", "conditional", false)]
    public async Task ConditionalMetadataCannotMasqueradeAsFactualSupport(string conditionStatus, string evidence, bool assumptions)
    {
        var scenario = Scenario();
        scenario = scenario with
        {
            Conditions = [scenario.Conditions[0] with { Status = conditionStatus }],
            Applicability = Exposure(assumptions ? ["Index identity is assumed for this scenario"] : [])
        };
        var result = await Validate(scenario, evidence);
        Assert.Equal("Conditional evidence requires an unresolved scenario on established exposure with explicit assumptions.", result.Error?.Title);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("cycle")]
    [InlineData("self")]
    [InlineData("duplicate")]
    [InlineData("duplicate_unknown")]
    [InlineData("partial")]
    [InlineData("partial_unknown")]
    [InlineData("assertion")]
    [InlineData("identifier")]
    [InlineData("duplicate_edge")]
    [InlineData("observed_edge")]
    [InlineData("empty_derived")]
    [InlineData("empty_conditional")]
    [InlineData("conditions_bound")]
    [InlineData("unknowns_bound")]
    [InlineData("edges_bound")]
    public async Task MalformedGraphsFailClosed(string defect)
    {
        QuestionAssessment assessment = defect switch
        {
            "missing" => new([Condition("rate", "derived", ["absent"])], []),
            "cycle" => new([Condition("a", "derived", ["b"]), Condition("b", "derived", ["a"])], []),
            "self" => new([Condition("a", "derived", ["a"])], []),
            "duplicate" => new([Condition("a"), Condition("a")], []),
            "duplicate_unknown" => new([Condition("a")], [Unknown("a")]),
            "partial" => new([Condition("a") with { Assertion = null }], []),
            "partial_unknown" => new([Condition("a")], [Unknown("missing") with { Id = null }]),
            "assertion" => new([Condition("a") with { Assertion = "inferred" }], []),
            "identifier" => new([Condition("Not valid")], []),
            "duplicate_edge" => new([Condition("a"), Condition("b", "derived", ["a", "a"])], []),
            "observed_edge" => new([Condition("a"), Condition("b", "observed", ["a"])], []),
            "empty_derived" => new([Condition("a", "derived")], []),
            "empty_conditional" => new([Condition("a", "conditional") with { Status = "unknown" }], []),
            "conditions_bound" => new(Enumerable.Range(0, 17).Select(i => Condition("a" + i)).ToArray(), []),
            "unknowns_bound" => new([], Enumerable.Range(0, 17).Select(i => Unknown("a" + i)).ToArray()),
            "edges_bound" => new([Condition("a", "derived", Enumerable.Range(0, 17).Select(i => "u" + i).ToArray())], []),
            _ => throw new ArgumentOutOfRangeException(nameof(defect))
        };
        var result = await Validate(assessment);
        Assert.Equal(HttpStatusCode.BadRequest, result.Error?.HttpStatusCode);
        Assert.Equal("Invalid evidence dependencies.", result.Error?.Title);
    }

    [Fact]
    public async Task GraphMetadataCannotBypassValidationByOmittingQuestionIdentity()
    {
        var draft = Draft(new([Condition("rate", "derived", ["absent"])], [])) with { Question = null };
        Assert.Equal(HttpStatusCode.BadRequest, (await ApplicabilityPolicy.Validate(draft, Company, Material)).Error?.HttpStatusCode);
    }

    [Fact]
    public async Task ApplicabilityCandidateUsesTheSameDependencyGuard()
    {
        var service = new AnalysisPreparationService(null!, null!, new Sources(), null!, null!);
        var result = await service.ValidateApplicabilityAsync(new()
        {
            CompanyId = Company,
            AsOf = DateTimeOffset.UnixEpoch.AddDays(1),
            Candidates = [new()
            {
                Assessment = new([Condition("rate", "derived", ["index_identity"])], [Unknown("index_identity")]),
                EvidenceStatus = "unknown"
            }]
        }, CancellationToken.None);
        Assert.Equal("A supported derived condition requires supported evidence dependencies.", result.Error?.Title);
    }

    [Fact]
    public void LegacyAssessmentPreservesExactWireBytesAndHash()
    {
        const string legacy = "{\"conditions\":[{\"fact\":\"Principal\",\"predicate\":\"Documented\",\"status\":\"supported\",\"sourceRefs\":[]}],\"unknowns\":[{\"fact\":\"Index identity\",\"query\":null,\"scope\":\"company\",\"materialId\":\"contract\"}]}";
        var assessment = JsonSerializer.Deserialize<QuestionAssessment>(legacy, AnalysisService.Json)!;
        var serialized = JsonSerializer.Serialize(assessment, AnalysisService.Json);
        Assert.Equal(legacy, serialized);
        Assert.Equal(SHA256.HashData(Encoding.UTF8.GetBytes(legacy)), SHA256.HashData(Encoding.UTF8.GetBytes(serialized)));
        Assert.Null(assessment.Conditions[0].Id);
        Assert.Null(assessment.Conditions[0].DependsOn);
        Assert.Null(assessment.Unknowns[0].Id);
    }

    [Fact]
    public void DeclaredDependenciesRoundTripWithoutLosingEdgesAndChangeThePayloadHash()
    {
        var assessment = Scenario();
        var json = JsonSerializer.Serialize(assessment, AnalysisService.Json);
        var restored = JsonSerializer.Deserialize<QuestionAssessment>(json, AnalysisService.Json)!;
        Assert.Equal("conditional", restored.Conditions[0].Assertion);
        Assert.Equal(["index_identity"], Assert.IsType<string[]>(restored.Conditions[0].DependsOn));
        Assert.Equal("index_identity", restored.Unknowns[0].Id);
        var changed = assessment with { Conditions = [assessment.Conditions[0] with { DependsOn = ["other_identity"] }] };
        Assert.NotEqual(SHA256.HashData(Encoding.UTF8.GetBytes(json)), SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(changed, AnalysisService.Json))));
    }

    private static ApplicabilityCondition Condition(string id, string assertion = "observed", string[]? dependencies = null) =>
        new("Synthetic source fact", "Synthetic claim", "supported", [new("contract", "1", Hash)], id, assertion, dependencies ?? []);

    private static MissingFact Unknown(string id) => new("Index identity is unknown", null, "company", "contract", id);

    private static CompanyApplicability Exposure(string[]? assumptions = null) =>
        new("established", "documented_exposure", "Own contract establishes exposure", [new("contract", "1", Hash)], assumptions ?? []);

    private static QuestionAssessment Scenario() => new(
        [Condition("rate", "conditional", ["index_identity"]) with { Status = "unknown" }], [Unknown("index_identity")],
        Exposure(["Index identity is assumed for this scenario"]));

    private static Task<Result> Validate(QuestionAssessment assessment, string evidence = "unknown") =>
        ApplicabilityPolicy.Validate(Draft(assessment, evidence), Company, Material);

    private static EffectDraft Draft(QuestionAssessment assessment, string evidence = "unknown") => new(
        "q", "company:" + Company, null, "Synthetic financing assessment", evidence,
        [new("news", "1", Hash), new("contract", "1", Hash)],
        Impact: new("risk", "qualitative", null, evidence, 1, 1, 1),
        Question: new("financing_cost", "event_only", "qualitative", "unknown", null, "qualitative"),
        Assessment: assessment);

    private static Task<Result<MaterialVersion>> Material(string material, string version) => Task.FromResult(
        new Result<MaterialVersion>(new MaterialVersion(material, version, "fixture", material == "news" ? "news" : "company",
            material, "Synthetic source fixture", DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, null,
            material == "news" ? [] : [Company], [], Hash), null));

    private sealed class Sources : ISourceResolver
    {
        public Task<Result<MaterialVersion>> GetAsync(string materialId, string versionId, CancellationToken ct) => Material(materialId, versionId);
    }
}
