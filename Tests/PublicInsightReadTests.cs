using System.Text.Json;
using InsightService.Common;
using InsightService.Contracts.Analyses;
using InsightService.Contracts.Context;
using InsightService.Contracts.PublicReads;
using InsightService.Services.PublicReads;
using Xunit;

public sealed class PublicInsightReadTests
{
    private static Task<PublicInsightFeed> Feed(PublicInsightReadFixture fixture, PublicInsightFeedQuery? query = null) => fixture.Service().Feed(PublicInsightReadFixture.User, query ?? new(), default);
    private static async Task<PublicInsightReadException> Error(Func<Task> action, int status, string? code = null)
    {
        var error = await Assert.ThrowsAsync<PublicInsightReadException>(action);
        Assert.Equal(status, error.StatusCode);
        if (code is not null)
            Assert.Equal(code, error.Code);
        Assert.DoesNotContain("PRIVATE", error.Message);
        return error;
    }

    [Fact]
    public async Task PublicCardKeepsSourcedDecimalStringAndExactTicksWithoutAuditAstRankingOrSourceText()
    {
        var f = new PublicInsightReadFixture();
        var result = await Feed(f);
        var card = Assert.IsType<PublicPersonalInsightCard>(Assert.Single(result.Items));
        Assert.Equal("12345678901234567890.1234", Assert.Single(card.Indicators).Value);
        Assert.Equal("conditional", card.Indicators[0].EvidenceStatus);
        Assert.Equal("available", card.IndicatorAvailability.Reason);
        Assert.Equal(4, card.CurrentRevision.CompanyImportance!.Score);
        Assert.Equal(PublicInsightReadFixture.At.UtcTicks, card.CurrentRevision.AsOf.UtcTicks);
        Assert.Equal(2, card.Sources.Length);
        Assert.Equal("lead", card.Sources[0].Provenance!.TextCompleteness);
        Assert.False(result.Exhaustive);
        Assert.Equal("opaque-next", result.NextCursor);
        var json = JsonSerializer.SerializeToElement<PublicInsightCard>(card, AnalysisProtocol.Json);
        Assert.Equal("personal", json.GetProperty("kind").GetString());
        Assert.True(json.TryGetProperty("currentRevision", out _));
        Assert.Equal(JsonValueKind.Null, json.GetProperty("currentRevision").GetProperty("category").ValueKind);
        foreach (var hidden in new[] { "PRIVATE-AUDIT", "PRIVATE-LOCATOR", "Synthetic loan", "modelExplanation", "calculationResult", "expression", "importance\"", "bankProfileIds", "companyIds" })
            Assert.DoesNotContain(hidden, json.GetRawText());
    }

    [Fact]
    public async Task EveryCallResolvesMembershipAndSourcesAfreshAndForwardsOnlyValidatedFilters()
    {
        var f = new PublicInsightReadFixture();
        var service = f.Service();
        var query = new PublicInsightFeedQuery { Limit = 2, Cursor = "opaque", Status = "forecast", Mechanism = "input_cost", NewsMaterialId = "news", MinImportance = 3 };
        await service.Feed(PublicInsightReadFixture.User, query, default);
        Assert.Equal(query.Cursor, f.Options!.Cursor);
        Assert.Equal(query.MinImportance, f.Options.MinImportance);
        Assert.Equal(query.Status, f.Options.Status);
        Assert.Equal(query.Mechanism, f.Options.Mechanism);
        Assert.Equal(query.NewsMaterialId, f.Options.NewsMaterialId);
        await service.Feed(PublicInsightReadFixture.User, new(), default);
        Assert.Equal(2, f.MembershipCalls);
        Assert.Equal(4, f.SourceCalls);
    }

    [Fact]
    public async Task MissingMembershipAndForeignRowsAreRejectedBeforeAnySourceLookup()
    {
        var f = new PublicInsightReadFixture { Membership = null };
        await Error(() => Feed(f), 403, "COMPANY_MEMBERSHIP_REQUIRED");
        Assert.Equal(0, f.StoreCalls);
        f.Membership = new(PublicInsightReadFixture.Company, null);
        f.Personal = f.Personal! with { CompanyId = PublicInsightReadFixture.Foreign };
        await Error(() => Feed(f), 502, "INSIGHT_SCOPE_MISMATCH");
        Assert.Equal(0, f.SourceCalls);
        await Error(() => f.Service().Detail(PublicInsightReadFixture.User, "personal", "question-1", default), 404);
    }

    [Theory]
    [InlineData("company")]
    [InlineData("bank")]
    public async Task SourceDeniedCardIsOmittedFromFeedWithCursorButDetailAndHistoryAreNotFound(string denial)
    {
        var f = new PublicInsightReadFixture();
        var original = f.Materials[("own", "v1")];
        f.Materials[("own", "v1")] = denial == "company" ? original with { CompanyIds = [PublicInsightReadFixture.Foreign] } : original with { BankProfileIds = ["U_97"] };
        var page = await Feed(f);
        Assert.Empty(page.Items);
        Assert.Equal("opaque-next", page.NextCursor);
        Assert.False(page.Exhaustive);
        await Error(() => f.Service().Detail(PublicInsightReadFixture.User, "personal", "question-1", default), 404);
        await Error(() => f.Service().History(PublicInsightReadFixture.User, "question-1", new(), default), 404);
        if (denial == "bank")
        {
            f.Membership = new(PublicInsightReadFixture.Company, "U_97");
            Assert.Single((await Feed(f)).Items);
        }
    }

    [Theory]
    [InlineData("id")]
    [InlineData("version")]
    [InlineData("hash")]
    [InlineData("time")]
    public async Task ExactSourceIdentityHashAndSubMillisecondKnowledgeMismatchAreBadContracts(string cause)
    {
        var f = new PublicInsightReadFixture();
        var source = f.Materials[("own", "v1")];
        f.Materials[("own", "v1")] = cause switch
        {
            "id" => source with { MaterialId = "another" },
            "version" => source with { VersionId = "another" },
            "hash" => source with { ContentHash = new string('b', 64) },
            _ => source with { KnownAt = source.KnownAt.AddTicks(1) }
        };
        await Error(() => Feed(f), 502, "SOURCE_IDENTITY_MISMATCH");
    }

    [Fact]
    public async Task UnavailableSourcesAndStoreNeverBecomeSuccessfulEmptyFeed()
    {
        var f = new PublicInsightReadFixture();
        f.Materials.Remove(("own", "v1"));
        await Error(() => Feed(f), 503, "SOURCE_UNAVAILABLE");
        f = new() { ThrowSource = true };
        await Error(() => Feed(f), 503);
        f = new() { MissingPage = true };
        await Error(() => Feed(f), 503);
        f = new() { ThrowCursor = true };
        await Error(() => Feed(f), 400, "INVALID_CURSOR");
    }

    [Fact]
    public async Task SourceBudgetCountsUniqueVersionsAcrossTheWholeRequestBeforeIo()
    {
        var f = new PublicInsightReadFixture();
        var refs = f.Personal!.CurrentRevision.SourceRefs.Concat(Enumerable.Range(0, 63)
            .Select(index => new SourceRef("source-" + index, "v1", PublicInsightReadFixture.Hash))).ToArray();
        f.Personal = f.Personal with { CurrentRevision = f.Personal.CurrentRevision with { SourceRefs = refs } };
        await Error(() => Feed(f), 503, "SOURCE_READ_BUDGET");
        Assert.Equal(0, f.SourceCalls);
    }

    [Fact]
    public async Task SourceLookupConcurrencyIsFourAndDuplicateReferencesDoNotSpendExtraBudget()
    {
        var f = new PublicInsightReadFixture { SourceGate = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        var own = f.Materials[("own", "v1")];
        var additional = Enumerable.Range(0, 8).Select(index => PublicInsightReadFixture.Material("extra-" + index, "market", [])).ToArray();
        foreach (var item in additional)
            f.Materials[(item.MaterialId, item.VersionId)] = item;
        var revision = f.Personal!.CurrentRevision;
        f.Personal = f.Personal with { CurrentRevision = revision with { SourceRefs = revision.SourceRefs.Concat(additional.Select(PublicInsightReadFixture.Ref)).Append(PublicInsightReadFixture.Ref(own)).ToArray() } };
        var pending = Feed(f);
        await f.FourSourcesStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(4, f.SourceCalls);
        Assert.Equal(4, f.Active);
        f.SourceGate.SetResult(true);
        Assert.Single((await pending).Items);
        Assert.Equal(10, f.SourceCalls);
        Assert.InRange(f.MaximumActive, 1, 4);
    }

    [Fact]
    public async Task HistoryUsesTheSameProjectionAndCurrentStalenessAndSourcesWithoutStartingGenerators()
    {
        var f = new PublicInsightReadFixture();
        var old = f.Personal!.CurrentRevision with { RevisionId = "revision-1", PreviousRevisionId = null, CalculationResult = "0" };
        f.Revisions = [f.Personal.CurrentRevision, old];
        f.Personal = f.Personal with { RequiresReassessment = true };
        var history = await f.Service().History(PublicInsightReadFixture.User, "question-1", new() { Limit = 2 }, default);
        Assert.Equal(["revision-2", "revision-1"], history.Items.Select(item => item.Revision.RevisionId).ToArray());
        Assert.All(history.Items, item => Assert.Equal("stale_sources", item.IndicatorAvailability.Reason));
        Assert.Equal("opaque-next", history.NextCursor);
        Assert.Equal(2, f.SourceCalls);
    }

    [Fact]
    public async Task GeneralProjectionRequiresAnExactPublicNewsAnchorAndNeverPublishesCompanyFinancialIndicators()
    {
        var f = new PublicInsightReadFixture();
        var card = Assert.IsType<PublicGeneralInsightCard>(Assert.Single((await Feed(f, new() { Kind = "general" })).Items));
        Assert.Null(card.RequiresReassessment);
        Assert.Empty(card.Indicators);
        Assert.Equal("general_not_company_calculation", card.IndicatorAvailability.Reason);
        Assert.False(JsonSerializer.SerializeToElement(card, AnalysisProtocol.Json).GetProperty("insight").TryGetProperty("rawCheckpoint", out _));
        Assert.DoesNotContain("companyImportance", JsonSerializer.Serialize(card, AnalysisProtocol.Json));
        f.General = PublicInsightReadFixture.GeneralItem([new("own", "v1", PublicInsightReadFixture.Hash)]);
        Assert.Empty((await Feed(f, new() { Kind = "general" })).Items);
        await Error(() => f.Service().Detail(PublicInsightReadFixture.User, "general", "opaque-general", default), 404);
        f.General = PublicInsightReadFixture.GeneralItem([new("market", "v1", PublicInsightReadFixture.Hash)]);
        f.Materials[("market", "v1")] = PublicInsightReadFixture.Material("market", "market", []);
        await Error(() => Feed(f, new() { Kind = "general" }), 502, "GENERAL_NEWS_ANCHOR");
    }

    [Fact]
    public async Task CompanyImportanceCannotBeLegacyFallbackOrUseUnboundEvidence()
    {
        var f = new PublicInsightReadFixture();
        var r = f.Personal!.CurrentRevision;
        f.Personal = f.Personal with { CurrentRevision = r with { CompanyImportance = null } };
        Assert.Null(Assert.IsType<PublicPersonalInsightCard>(Assert.Single((await Feed(f)).Items)).CurrentRevision.CompanyImportance);
        f.Personal = f.Personal with { CurrentRevision = r with { CompanyImportance = r.CompanyImportance! with { EvidenceRefs = [r.SourceRefs[0]] } } };
        await Error(() => Feed(f), 502, "IMPORTANCE_EVIDENCE_MISMATCH");
        f.Personal = f.Personal with { CurrentRevision = r with { Impact = r.Impact! with { Importance = 5 } } };
        await Error(() => Feed(f), 502, "IMPORTANCE_EVIDENCE_MISMATCH");
        f.Personal = f.Personal with { CurrentRevision = r with { CompanyImportance = new(null, "Not assessed", []), Impact = r.Impact! with { Importance = 0 } } };
        Assert.Null(Assert.IsType<PublicPersonalInsightCard>(Assert.Single((await Feed(f)).Items)).CurrentRevision.CompanyImportance!.Score);
    }

    [Fact]
    public async Task MissingProvenanceIsNotInventedAndCredentialOrNonHttpUrlsAreNotPublished()
    {
        var f = new PublicInsightReadFixture();
        var own = f.Materials[("own", "v1")];
        f.Materials[("own", "v1")] = own with { Provenance = null };
        Assert.Null(Assert.IsType<PublicPersonalInsightCard>(Assert.Single((await Feed(f)).Items)).Sources[1].Provenance);
        f.Materials[("own", "v1")] = own with { Provenance = own.Provenance! with { SourceUrl = "https://user:password@example.test/secret" } };
        Assert.Null(Assert.IsType<PublicPersonalInsightCard>(Assert.Single((await Feed(f)).Items)).Sources[1].Provenance!.SourceUrl);
    }

    [Theory]
    [InlineData("kind")]
    [InlineData("limit")]
    [InlineData("cursor")]
    [InlineData("mechanism")]
    [InlineData("general-filter")]
    public async Task InvalidQueriesAreRejectedBeforeMembership(string cause)
    {
        var f = new PublicInsightReadFixture();
        var query = cause switch
        {
            "kind" => new PublicInsightFeedQuery { Kind = "other" },
            "limit" => new() { Limit = 21 },
            "cursor" => new() { Cursor = new string('x', 2049) },
            "mechanism" => new() { Mechanism = "arbitrary" },
            _ => new() { Kind = "general", MinImportance = 4 }
        };
        await Error(() => Feed(f, query), 400, "INVALID_REQUEST");
        Assert.Equal(0, f.MembershipCalls);
    }

    [Theory]
    [InlineData("condition", false)]
    [InlineData("condition", true)]
    [InlineData("applicability", false)]
    [InlineData("applicability", true)]
    public async Task LegacyNestedEvidenceCannotPublishReferencesOutsideVerifiedOuterSourceSet(string location, bool mismatchedHash)
    {
        var f = new PublicInsightReadFixture();
        var original = f.Personal!.CurrentRevision;
        var own = original.SourceRefs[1];
        var nested = mismatchedHash ? own with { ContentHash = new string('b', 64) } : new("foreign-source", "v1", PublicInsightReadFixture.Hash);
        var assessment = location == "condition"
            ? new InsightService.Contracts.Questions.QuestionAssessment([new("Legacy unverified claim", "exposure", "unknown", [nested])], [])
            : new([], [], new("unknown", "not_established", "Legacy unverified applicability", [nested], []));
        f.Personal = f.Personal with
        {
            CurrentRevision = original with
            {
                SourceRefs = [own],
                Question = null,
                Calculation = null,
                CalculationResult = null,
                CompanyImportance = null,
                Impact = null,
                Assessment = assessment
            }
        };
        await Error(() => Feed(f), 502, "INSIGHT_READ_CONTRACT");
        await Error(() => f.Service().Detail(PublicInsightReadFixture.User, "personal", "question-1", default), 502, "INSIGHT_READ_CONTRACT");
        Assert.Equal(0, f.SourceCalls); // No expansion or fetch of the nested foreign/hash-mismatched references.
    }

    [Fact]
    public async Task SharedCategoryIntegrationMustBindStoredCategoryRatherThanSilentlyDiscardKnownValues()
    {
        // Reflection is a test-only integration guard. Production never probes for optional domain features.
        var categorySupported = typeof(EffectRevision).GetProperty("Category") is not null;
        var f = new PublicInsightReadFixture();
        var revision = JsonSerializer.SerializeToNode(f.Personal!.CurrentRevision, AnalysisProtocol.Json)!.AsObject();
        revision["category"] = "risk";
        f.Personal = f.Personal with { CurrentRevision = revision.Deserialize<EffectRevision>(AnalysisProtocol.Json)! };
        var personal = Assert.IsType<PublicPersonalInsightCard>(Assert.Single((await Feed(f)).Items));
        if (categorySupported)
            Assert.Equal("risk", personal.CurrentRevision.Category);
        else
            Assert.Null(personal.CurrentRevision.Category);

        var general = JsonSerializer.SerializeToNode(f.General!.Insight, AnalysisProtocol.Json)!.AsObject();
        general["category"] = "risk";
        f.General = f.General with { Insight = JsonSerializer.SerializeToElement(general, AnalysisProtocol.Json) };
        var card = Assert.IsType<PublicGeneralInsightCard>(Assert.Single((await Feed(f, new() { Kind = "general" })).Items));
        if (categorySupported)
            Assert.Equal("risk", card.Insight.Category);
        else
            Assert.Null(card.Insight.Category);
    }

    [Fact]
    public async Task CancellationIsBoundedWithoutReturningAFalseSuccessfulPage()
    {
        var f = new PublicInsightReadFixture { SourceGate = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        using var cancellation = new CancellationTokenSource();
        var pending = f.Service().Feed(PublicInsightReadFixture.User, new(), cancellation.Token);
        cancellation.Cancel();
        await Error(async () => await pending, 503);
    }
}
