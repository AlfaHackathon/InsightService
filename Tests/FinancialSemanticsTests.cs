using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AlfaHackaton.Result;
using InsightService.Contracts.Analyses;
using InsightService.Contracts.Context;
using InsightService.Models.Policies;
using InsightService.Models.Persistence;
using InsightService.Repositories;
using InsightService.Services;
using Xunit;

public sealed class FinancialSemanticsTests
{
    private static readonly Guid Company = Guid.Parse("cfa10a85-b634-4d72-841e-d6ae1d03d501");
    private const string Hash = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private static readonly SourceRef Contract = new("contract", "1", Hash);
    private static EffectDraft Draft() => new("draft", "company:" + Company, null, "Incremental interest", "announced", [Contract],
        new(new("multiply", Left: new("value", "1000000"), Right: new("value", "0.02")), "RUB", "2027"),
        new("expense_delta", "annual_run_rate", "Loan-C-001", "supported", 1, 1, 1),
        new("financing_cost", "event_only", "RUB", "2027", null, "annual_run_rate"),
        new([], [], new("established", "documented_exposure", "Documented loan", [Contract], [])));

    private static Task<Result> Validate(EffectDraft draft, string text = "Loan-C-001: principal RUB 1000000, floating rate.",
        string kind = "company", Guid? owner = null, string hash = Hash, string sourceId = "own-document") =>
        ApplicabilityPolicy.Validate(draft, Company, (id, version) => Task.FromResult(new Result<MaterialVersion>(
            new(id, version, sourceId, kind, "Own source", text, DateTimeOffset.MinValue, null, null, [owner ?? Company], [], hash), null)));

    [Fact]
    public async Task InventedBudgetCannotTurnContractualCounterfactualIntoBudgetDelta()
    {
        var draft = Draft();
        var result = await Validate(draft with { Question = draft.Question! with { Frame = "budget_delta", BaselinePeriod = "budget:2027" } });
        Assert.NotNull(result.Error);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("FakeLoan")]
    [InlineData("Loan-C-00")]
    public async Task MissingOrUngroundedObjectCannotSupportMonetaryCalculation(string? objectId)
    {
        var draft = Draft();
        Assert.NotNull((await Validate(draft with { Impact = draft.Impact! with { EconomicObjectId = objectId } })).Error);
    }

    [Fact]
    public async Task ContractualCounterfactualRemainsValidWithoutBudget()
    {
        Assert.Null((await Validate(Draft())).Error);
    }

    private const string BudgetQuote = "Approved budget:2027 for Loan-C-001: interest RUB 120000.";
    private static EffectDraft BudgetDraft()
    {
        var draft = Draft();
        return draft with
        {
            Question = draft.Question! with { Frame = "budget_delta", BaselinePeriod = "budget:2027" },
            Assessment = draft.Assessment! with
            {
                Conditions = [new(BudgetQuote, "budget_baseline", "supported", [Contract], "budget", "observed", [])]
            }
        };
    }

    [Fact]
    public async Task ExactObservedBudgetForTheSameObjectCanSupportBudgetDelta()
    {
        Assert.Null((await Validate(BudgetDraft(), BudgetQuote)).Error);
    }

    [Theory]
    [InlineData("invented_quote")]
    [InlineData("wrong_period")]
    [InlineData("wrong_object")]
    [InlineData("unknown")]
    [InlineData("scenario")]
    [InlineData("wrong_reference")]
    [InlineData("no_budget_prefix")]
    public async Task BudgetLabelRequiresExactObservedDocumentaryBinding(string defect)
    {
        var draft = BudgetDraft();
        var condition = draft.Assessment!.Conditions.Single();
        var text = BudgetQuote;
        switch (defect)
        {
            case "invented_quote":
                text = "Loan-C-001: floating rate, no budget supplied.";
                break;
            case "wrong_period":
                text = condition.Fact.Replace("budget:2027", "budget:2028", StringComparison.Ordinal);
                condition = condition with { Fact = text };
                break;
            case "wrong_object":
                text = condition.Fact.Replace("Loan-C-001", "Loan-C-002", StringComparison.Ordinal);
                condition = condition with { Fact = text };
                break;
            case "unknown":
                condition = condition with { Status = "unknown" };
                break;
            case "scenario":
                condition = condition with { Assertion = "conditional", DependsOn = ["missing"] };
                break;
            case "wrong_reference":
                condition = condition with { SourceRefs = [new("other", "1", Hash)] };
                break;
            case "no_budget_prefix":
                draft = draft with { Question = draft.Question! with { BaselinePeriod = "2027" } };
                break;
        }
        draft = draft with { Assessment = draft.Assessment with { Conditions = [condition] } };
        Assert.NotNull((await Validate(draft, text)).Error);
    }

    [Fact]
    public async Task ForeignOrMismatchedOriginalCannotBindObject()
    {
        Assert.NotNull((await Validate(Draft(), owner: Guid.NewGuid())).Error);
        Assert.NotNull((await Validate(Draft(), hash: new string('b', 64))).Error);
    }

    [Fact]
    public async Task QualitativeUnknownDoesNotRequireAnObjectOrBudget()
    {
        var draft = Draft();
        Assert.Null((await Validate(draft with
        {
            Calculation = null,
            Impact = draft.Impact! with { EconomicObjectId = null, EvidenceStatus = "unknown" },
            Assessment = new([], [])
        })).Error);
    }

    [Theory]
    [InlineData("news", "own-document")]
    [InlineData("company", "provided-bank-csv")]
    public async Task PublicOrProfileSourceCannotBindObject(string kind, string sourceId)
    {
        Assert.NotNull((await Validate(Draft(), kind: kind, sourceId: sourceId)).Error);
    }
}

public sealed class FinancialSemanticsPersistenceTests
{
    [Theory]
    [InlineData("valid")]
    [InlineData("missing_dependencies")]
    [InlineData("observed_dependencies")]
    [InlineData("missing_id")]
    public async Task BudgetEvidenceHasTheSameRequirementsInPreparationAndSave(string shape)
    {
        using var database = new PgTestDatabase();
        var fixture = PreservationFixture.Load();
        var repository = new EffectRepository(database.Context);
        var draft = fixture.CurrentFirst.Drafts.Single();
        var contract = draft.SourceRefs.Single(r => r.MaterialId != fixture.CurrentFirst.NewsMaterialId);
        var quote = $"Approved budget:2027 for {draft.Impact!.EconomicObjectId}: interest RUB 120000.";
        fixture.AddVersion(contract.MaterialId, "budget", text: quote);
        var reference = fixture.Reference(contract.MaterialId, "budget");
        var condition = new InsightService.Contracts.Questions.ApplicabilityCondition(quote, "budget_baseline", "supported", [reference], "budget", "observed", []);
        condition = shape switch
        {
            "missing_dependencies" => condition with { DependsOn = null },
            "observed_dependencies" => condition with { DependsOn = ["budget"] },
            "missing_id" => condition with { Id = null },
            _ => condition
        };
        draft = draft with
        {
            SourceRefs = [.. draft.SourceRefs.Where(r => r.MaterialId == fixture.CurrentFirst.NewsMaterialId), reference],
            Question = draft.Question! with { Frame = "budget_delta", BaselinePeriod = "budget:2027" },
            Assessment = new([condition], [], new("established", "documented_exposure", "Approved own budget", [reference], []))
        };
        var request = fixture.CurrentFirst with { Drafts = [draft] };
        var preparation = new AnalysisPreparationService(repository, null!, fixture, null!, null!);
        var prepared = await preparation.PrepareAsync(new() { Request = request, Drafts = request.Drafts });
        var saved = await new AnalysisService(repository, fixture).Save(request);
        if (shape == "valid")
        {
            Assert.Null(prepared.Error);
            Assert.Null(saved.Error);
        }
        else
        {
            Assert.NotNull(prepared.Error);
            Assert.NotNull(saved.Error);
            Assert.Empty(await repository.GetEffects(request.CompanyId, request.NewsMaterialId));
        }
    }

    [Theory]
    [InlineData("supported")]
    [InlineData("conditional")]
    public async Task ApplicabilityOnlyCandidateDoesNotRequireUnavailableFinancialDimensions(string status)
    {
        var fixture = PreservationFixture.Load();
        var assessment = fixture.CurrentFirst.Drafts.Single().Assessment!;
        if (status == "conditional")
        {
            assessment = assessment with { Applicability = assessment.Applicability! with { Assumptions = ["Future rate scenario"] } };
        }
        var service = new AnalysisPreparationService(null!, null!, fixture, null!, null!);
        var result = await service.ValidateApplicabilityAsync(new()
        {
            CompanyId = fixture.CurrentFirst.CompanyId,
            AsOf = fixture.CurrentFirst.AsOf,
            Candidates = [new() { Assessment = assessment, EvidenceStatus = status, HasCalculation = true }]
        }, CancellationToken.None);
        Assert.Null(result.Error);
    }

    [Theory]
    [InlineData("budget")]
    [InlineData("missing_object")]
    [InlineData("fake_object")]
    public async Task PreparationAndDirectSaveRejectInvalidCapturedCalculationWithoutWriting(string defect)
    {
        using var database = new PgTestDatabase();
        var fixture = PreservationFixture.Load();
        var repository = new EffectRepository(database.Context);
        var draft = fixture.CurrentFirst.Drafts.Single();
        draft = defect switch
        {
            "budget" => draft with { Question = draft.Question! with { Frame = "budget_delta", BaselinePeriod = "budget:2027" } },
            "missing_object" => draft with { Impact = draft.Impact! with { EconomicObjectId = null } },
            _ => draft with { Impact = draft.Impact! with { EconomicObjectId = "InventedLoan" } }
        };
        var request = fixture.CurrentFirst with { Drafts = [draft] };
        var preparation = new AnalysisPreparationService(repository, null!, fixture, null!, null!);
        var prepared = await ResultAssert.ErrorAsync(() => preparation.PrepareAsync(new() { Request = request, Drafts = request.Drafts }));
        Assert.Equal(422, (int)prepared.HttpStatusCode!);
        var service = new AnalysisService(repository, fixture);
        var saved = await ResultAssert.ErrorAsync(() => service.Save(request));
        Assert.Equal(422, (int)saved.HttpStatusCode!);
        Assert.Empty(await repository.GetEffects(request.CompanyId, request.NewsMaterialId));
        Assert.Null(await repository.FindAnalysis(request.CompanyId, request.NewsMaterialId, request.NewsVersionId));
    }

    [Fact]
    public async Task CapturedCounterfactualCanPrepareSaveAndReplay()
    {
        using var database = new PgTestDatabase();
        var fixture = PreservationFixture.Load();
        var repository = new EffectRepository(database.Context);
        var preparation = new AnalysisPreparationService(repository, null!, fixture, null!, null!);
        var prepared = await preparation.PrepareAsync(new() { Request = fixture.CurrentFirst, Drafts = fixture.CurrentFirst.Drafts }).RequireContentAsync();
        Assert.Single(prepared.Drafts);
        var service = new AnalysisService(repository, fixture);
        var saved = await service.Save(fixture.CurrentFirst).RequireContentAsync();
        Assert.Equal("20000", Assert.Single(saved.Effects).CurrentRevision.CalculationResult);
        Assert.True((await service.Save(fixture.CurrentFirst).RequireContentAsync()).Duplicate);
    }

    [Fact]
    public async Task HistoricalUnboundBudgetRemainsReadableAndReplayable()
    {
        using var database = new PgTestDatabase();
        var repository = new EffectRepository(database.Context);
        var fixture = PreservationFixture.Load();
        var draft = fixture.First.Drafts.Single();
        var question = draft.Question! with { Frame = "budget_delta", BaselinePeriod = "budget:2027" };
        var key = AnalysisService.QuestionKey(draft.LiteralObjectRef, question).RequireContent();
        // Seed an already completed historical result directly, before the new write policy.
        draft = draft with
        {
            EffectKey = key,
            Question = question,
            Impact = draft.Impact! with { EconomicObjectId = null },
            ModelExplanation = draft.Conclusion,
            Conclusion = QuestionIdentityPolicy.LegacyReceiptConclusion("budget_delta", "20000", "RUB", "2027", draft.LiteralObjectRef),
            Calculation = DecimalCalculator.Calculate(draft.Calculation!).RequireContent().Calculation,
            SourceRefs = draft.SourceRefs.OrderBy(r => r.MaterialId, StringComparer.Ordinal).ThenBy(r => r.VersionId, StringComparer.Ordinal).ThenBy(r => r.ContentHash, StringComparer.Ordinal).ToArray()
        };
        var request = fixture.First with { Drafts = [draft] };
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(request, AnalysisService.Json))));
        var historical = await repository.Save(request, hash, [new PreparedDraft(draft, "20000")]).RequireContentAsync();
        var service = new AnalysisService(repository, fixture);
        Assert.Equal("20000", (await service.GetEffect(historical.Effects.Single().EffectId).RequireContentAsync()).CurrentRevision.CalculationResult);
        Assert.True((await service.Save(request).RequireContentAsync()).Duplicate);
    }
}
