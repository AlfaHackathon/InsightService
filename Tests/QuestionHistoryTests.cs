using AlfaHackaton.Result;
using InsightService.Contracts.Analyses;
using InsightService.Contracts.Calculations;
using InsightService.Contracts.Context;
using InsightService.Contracts.Questions;
using InsightService.Repositories;
using InsightService.Services;
using InsightService.Services.Interfaces;
using Xunit;

public sealed class QuestionHistoryTests : IDisposable
{
    private readonly PgTestDatabase database = new();
    private readonly Guid company = Guid.NewGuid();
    private readonly DateTimeOffset at = DateTimeOffset.Parse("2026-10-22T12:00:00Z");
    private const string Hash = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private AnalysisService Service() => new(new EffectRepository(database.Context), new Sources(at, company));
    private AnalysisRequest Request(string news, string version, string? prior = null, string mechanism = "financing_cost") => new(company, null, news, version, at.AddHours(1), [new("question-" + mechanism, "company:" + company, prior, "Synthetic source correction requires review.", "observed", [new(news, version, Hash)], Impact: new("qualitative", "qualitative", null, "contradicted", 0, 0, 0), Question: new(mechanism, "absolute", "RUB", "2026", null))]);
    [Fact]
    public async Task RestrictedNewVersionCannotInvalidateAnotherCompanyThroughOldDependency()
    {
        var original = Request("news-a", "v1");
        original = original with
        {
            Drafts = [original.Drafts[0] with
            {
                SourceRefs = [new("news-a", "v1", Hash), new("market", "v1", Hash)]
            }

            ]
        };
        var first = (await (Service().Save(original)).RequireContentAsync()).Effects.Single();
        Assert.Empty((await (Service().React(new("material:market", "market", "v2", at.AddHours(2), [Guid.NewGuid()]))).RequireContentAsync()).Items);
        Assert.False((await (Service().GetEffect(first.EffectId)).RequireContentAsync()).RequiresReassessment);
        Assert.Single((await (Service().React(new("material:market", "market", "v2", at.AddHours(2), [company]))).RequireContentAsync()).Items);
        Assert.True((await (Service().GetEffect(first.EffectId)).RequireContentAsync()).RequiresReassessment);
    }

    [Fact]
    public async Task EmptyAssessmentIsWatchedWhenFirstCompanyDocumentArrivesWithoutInventingQuestion()
    {
        var receipt = await (Service().Save(Request("news-a", "v1") with
        {
            Drafts = [],
            ContextComplete = false
        })).RequireContentAsync();
        var page = (await (Service().React(new("material:company", "first-own-document", "v1", at.AddHours(2), [company]))).RequireContentAsync());
        var work = Assert.Single(page.Items);
        Assert.Null(work.EffectId);
        Assert.Equal("analysis_watch", work.Kind);
        Assert.Equal(receipt.AnalysisId, work.AnalysisId);
        Assert.Equal("news-a", work.NewsMaterialId);
        Assert.Empty((await (Service().GetEffects(company, "news-a")).RequireContentAsync()));
        Assert.Empty((await (Service().React(new("material:company", "foreign", "v1", at.AddHours(2), [Guid.NewGuid()]))).RequireContentAsync()).Items);
        Assert.Empty((await (Service().React(new("material:market", "foreign-market", "v1", at.AddHours(2), [Guid.NewGuid()]))).RequireContentAsync()).Items);
        Assert.Empty((await (Service().React(new("material:company", "too-early", "v1", at, [company]))).RequireContentAsync()).Items);
    }

    [Fact]
    public async Task UnmigratedFreeTextPeriodDoesNotBlockUnrelatedMechanism()
    {
        var first = (await (Service().Save(Request("news-a", "v1"))).RequireContentAsync()).Effects.Single();
        using (var db = database.Open())
        {
            using var command = db.CreateCommand();
            command.CommandText = "UPDATE question_revisions SET payload_json=jsonb_set(payload_json::jsonb,'{question,targetPeriod}','\"September 2026\"')::text";
            command.ExecuteNonQuery();
        }

        var next = await (Service().Save(Request("news-b", "v1", mechanism: "rent_indexation"))).RequireContentAsync();
        Assert.Single(next.Effects);
        Assert.Equal(2, (await (Service().GetEffects(company, "news-b")).RequireContentAsync()).Count);
        Assert.Equal("September 2026", (await (Service().GetEffect(first.EffectId)).RequireContentAsync()).CurrentRevision.Question!.TargetPeriod);
    }

    [Fact]
    public async Task LegacyDatedQuestionRekeysWithoutLosingHistoryOrReplay()
    {
        var q = new QuestionIdentity("financing_cost", "event_only", "qualitative", "2026-09-11", null, "qualitative");
        var request = Request("news-a", "v1") with
        {
            Drafts = [Request("news-a", "v1").Drafts[0] with
            {
                Question = q
            }

            ]
        };
        var first = (await (Service().Save(request)).RequireContentAsync()).Effects.Single();
        var keyText = string.Join('\u001f', request.Drafts[0].LiteralObjectRef, q.Mechanism, q.Frame, q.Unit, q.TargetPeriod, "", q.Basis);
        var oldKey = "question-" + Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(keyText)))[..40];
        var oldRequest = request with
        {
            Drafts = [request.Drafts[0] with
            {
                EffectKey = oldKey
            }

            ]
        };
        var oldHash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(System.Text.Json.JsonSerializer.Serialize(oldRequest, AnalysisService.Json))));
        using (var db = database.Open())
        {
            using var command = db.CreateCommand();
            command.CommandText = "UPDATE questions SET effect_key=@key; UPDATE question_revisions SET payload_json=jsonb_set(payload_json::jsonb,'{question,targetPeriod}','\"2026-09-11\"')::text; UPDATE analysis_receipts SET request_hash=@hash";
            command.Parameters.AddWithValue("@key", oldKey);
            command.Parameters.AddWithValue("@hash", oldHash);
            command.ExecuteNonQuery();
        }

        Assert.True((await (Service().Save(request)).RequireContentAsync()).Duplicate);
        var next = Request("news-b", "v1", first.RevisionId) with
        {
            Drafts = [Request("news-b", "v1", first.RevisionId).Drafts[0] with
            {
                Question = q with
                {
                    TargetPeriod = "2026-10-23"
                }
            }

            ]
        };
        var second = (await (Service().Save(next)).RequireContentAsync()).Effects.Single();
        Assert.Equal(first.EffectId, second.EffectId);
        Assert.Equal(2, (await (Service().GetEffect(first.EffectId)).RequireContentAsync()).History.Count);
        Assert.True((await (Service().Save(request)).RequireContentAsync()).Duplicate);
        Assert.Equal(second.RevisionId, (await (Service().GetEffect(first.EffectId)).RequireContentAsync()).CurrentRevision.RevisionId);
    }

    [Fact]
    public async Task QualitativeApplicabilityQuestionSurvivesIndependentPublicationDates()
    {
        AnalysisRequest Qualitative(string news, string date, string? previous = null) => Request(news, "v1", previous) with
        {
            Drafts = [Request(news, "v1", previous).Drafts[0] with
            {
                Question = new("financing_cost", "event_only", "qualitative", date, null, "qualitative")
            }

            ]
        };
        var firstRequest = Qualitative("news-a", "2026-09-11");
        var first = (await (Service().Save(firstRequest)).RequireContentAsync()).Effects.Single();
        var next = (await (Service().Save(Qualitative("news-b", "2026-10-23", first.RevisionId))).RequireContentAsync()).Effects.Single();
        Assert.Equal(first.EffectId, next.EffectId);
        Assert.Equal("unknown", next.CurrentRevision.Question!.TargetPeriod);
        Assert.Equal(first.RevisionId, next.PreviousRevisionId);
        Assert.Equal(2, (await (Service().GetEffect(first.EffectId)).RequireContentAsync()).History.Count);
        Assert.True((await (Service().Save(firstRequest)).RequireContentAsync()).Duplicate);
        Assert.Equal(next.RevisionId, (await (Service().GetEffect(first.EffectId)).RequireContentAsync()).CurrentRevision.RevisionId);
    }

    [Fact]
    public async Task IndependentNewsCorrectionAndOldReplayKeepOneActualHistory()
    {
        var firstRequest = Request("news-a", "v1");
        var first = (await (Service().Save(firstRequest)).RequireContentAsync()).Effects.Single();
        var second = (await (Service().Save(Request("news-b", "v1", first.RevisionId))).RequireContentAsync()).Effects.Single();
        Assert.Equal(first.EffectId, second.EffectId);
        Assert.Equal(first.RevisionId, second.PreviousRevisionId);
        var correction = (await (Service().Save(Request("news-a", "v2", second.RevisionId))).RequireContentAsync()).Effects.Single();
        Assert.Equal(first.EffectId, correction.EffectId);
        Assert.Equal(second.RevisionId, correction.PreviousRevisionId);
        var replay = await (Service().Save(firstRequest)).RequireContentAsync();
        Assert.True(replay.Duplicate);
        Assert.Equal(first.RevisionId, replay.Effects.Single().RevisionId);
        var state = (await (Service().GetEffect(first.EffectId)).RequireContentAsync());
        Assert.Equal(correction.RevisionId, state.CurrentRevision.RevisionId);
        Assert.Equal(["news-a", "news-b", "news-a"], state.History.Select(r => r.NewsMaterialId));
        Assert.Equal(3, state.History.Count);
        Assert.Single((await (Service().GetEffects(company, "news-b")).RequireContentAsync()));
    }

    [Fact]
    public async Task NewDocumentVersionInvalidatesAndReassessesSameQuestionWithSeparateReceipt()
    {
        var original = Request("news-a", "v1");
        original = original with
        {
            Drafts = [original.Drafts[0] with
            {
                SourceRefs = [new("news-a", "v1", Hash), new("contract", "v1", Hash)]
            }

            ]
        };
        var first = (await (Service().Save(original)).RequireContentAsync()).Effects.Single();
        var affected = (await (Service().React(new("material:company", "contract", "v2", at.AddHours(2), [company]))).RequireContentAsync());
        Assert.Equal(first.EffectId, affected.Items.Single().EffectId);
        Assert.True((await (Service().GetEffect(first.EffectId)).RequireContentAsync()).RequiresReassessment);
        Assert.False((await (Service().GetSnapshot(company, "news-a", at.AddHours(1))).RequireContentAsync()).Effects.Single().RequiresReassessment);
        Assert.Equal([company], (await (Service().Recipients(at.AddHours(2), null, "contract")).RequireContentAsync()).CompanyIds);
        var revisionRequest = Request("news-a", "v1", first.RevisionId) with
        {
            AsOf = at.AddHours(2),
            Trigger = new("contract", "v2")
        };
        revisionRequest = revisionRequest with
        {
            Drafts = [revisionRequest.Drafts[0] with
            {
                SourceRefs = [new("news-a", "v1", Hash), new("contract", "v1", Hash), new("contract", "v2", Hash)]
            }

            ]
        };
        var updated = await (Service().Save(revisionRequest)).RequireContentAsync();
        Assert.NotEqual((await (Service().Save(original)).RequireContentAsync()).AnalysisId, updated.AnalysisId);
        Assert.Equal(first.EffectId, updated.Effects.Single().EffectId);
        Assert.False((await (Service().GetEffect(first.EffectId)).RequireContentAsync()).RequiresReassessment);
        Assert.Empty((await (Service().React(new("material:company", "contract", "v2", at.AddHours(2), [company]))).RequireContentAsync()).Items);
        Assert.False((await (Service().GetEffect(first.EffectId)).RequireContentAsync()).RequiresReassessment);
        Assert.Equal(updated.AnalysisId, (await (Service().GetAnalysis(company, "news-a", "v1", new("contract", "v2"))).RequireContentAsync()).AnalysisId);
        Assert.True((await (Service().Save(revisionRequest)).RequireContentAsync()).Duplicate);
        Assert.Equal(updated.Effects.Single().RevisionId, (await (Service().GetEffect(first.EffectId)).RequireContentAsync()).CurrentRevision.RevisionId);
        Assert.Equal(2, (await (Service().GetEffect(first.EffectId)).RequireContentAsync()).History.Count);
    }

    [Fact]
    public async Task RepeatedPendingChangesAndLaterClearPreserveHistoricalInvalidity()
    {
        var first = (await (Service().Save(Request("news-a", "v1"))).RequireContentAsync()).Effects.Single();
        await (Service().React(new("material:company", "contract", "v2", at.AddHours(2), [company]))).RequireContentAsync();
        await (Service().React(new("material:company", "contract", "v3", at.AddHours(3), [company]))).RequireContentAsync();
        Assert.True((await (Service().GetSnapshot(company, "news-a", at.AddHours(2.5))).RequireContentAsync()).Effects.Single().RequiresReassessment);
        var request = Request("news-a", "v1", first.RevisionId) with
        {
            AsOf = at.AddHours(4),
            Trigger = new("contract", "v3")
        };
        request = request with
        {
            Drafts = [request.Drafts[0] with
            {
                SourceRefs = [new("news-a", "v1", Hash), new("contract", "v3", Hash)]
            }

            ]
        };
        await (Service().Save(request)).RequireContentAsync();
        Assert.False((await (Service().GetEffect(first.EffectId)).RequireContentAsync()).RequiresReassessment);
        Assert.True((await (Service().GetSnapshot(company, "news-a", at.AddHours(2.5))).RequireContentAsync()).Effects.Single().RequiresReassessment);
        Assert.True((await (Service().GetSnapshot(company, "news-a", at.AddHours(3.5))).RequireContentAsync()).Effects.Single().RequiresReassessment);
    }

    [Fact]
    public async Task HistoricalMembershipChangeRoutesUsingAvailablePriorQuestionRevision()
    {
        var first = (await (Service().Save(Request("news-a", "v1"))).RequireContentAsync()).Effects.Single();
        await (Service().Save(Request("news-b", "v1", first.RevisionId) with
        {
            AsOf = at.AddHours(3)
        })).RequireContentAsync();
        var work = Assert.Single((await (Service().React(new("group", "root", "2", at.AddHours(2), []))).RequireContentAsync()).Items);
        Assert.Equal("news-a", work.NewsMaterialId);
        Assert.Equal("v1", work.NewsVersionId);
    }

    [Fact]
    public async Task SourceReactionAndRecipientsPreserveOneHundredNanosecondBoundary()
    {
        var saved = (await (Service().Save(Request("news-a", "v1") with
        {
            AsOf = at.AddTicks(1)
        })).RequireContentAsync()).Effects.Single();
        Assert.Empty((await (Service().React(new("material:company", "later-document", "1", at, [company]))).RequireContentAsync()).Items);
        Assert.Empty((await (Service().Recipients(at, null, null)).RequireContentAsync()).CompanyIds);
        Assert.Equal([company], (await (Service().Recipients(at.AddTicks(1), null, null)).RequireContentAsync()).CompanyIds);
        Assert.False((await (Service().GetEffect(saved.EffectId)).RequireContentAsync()).RequiresReassessment);
    }

    [Fact]
    public async Task UnrelatedMechanismScopeDoesNotInvalidateAnUnchangedFinancingQuestion()
    {
        var finance = (await (Service().Save(Request("news-a", "v1"))).RequireContentAsync()).Effects.Single();
        var rent = Request("news-b", "v1", mechanism: "rent_indexation") with
        {
            Mechanisms = ["rent_indexation"]
        };
        var saved = await (Service().Save(rent)).RequireContentAsync();
        Assert.Empty(saved.UnrevisedEffectIds!);
        Assert.Single((await (Service().GetEffect(finance.EffectId)).RequireContentAsync()).History);
    }

    [Fact]
    public async Task FinancialPresentationRejectsWrongModelDigitsRatherThanReplacingTheNarrative()
    {
        var request = Request("news-a", "v1");
        var calculation = new Calculation(new("multiply", Left: new("value", "100"), Right: new("value", "80")), "RUB", "2026-11");
        var draft = request.Drafts[0] with
        {
            Conclusion = "Synthetic wrong model text: 800 RUB",
            CalculationClaim = new("800 RUB", "800", "RUB", "2026-11"),
            Calculation = calculation,
            Question = request.Drafts[0].Question! with
            {
                TargetPeriod = "2026-11",
                Basis = "monthly_run_rate"
            },
            SourceRefs = [new("news-a", "v1", Hash), new("contract", "v1", Hash)],
            Impact = new("expense_delta", "monthly_run_rate", "FixtureLoan", "supported", 1, 1, 1),
            Assessment = new([], [], new("established", "documented_exposure", "Synthetic own exposure", [new("contract", "v1", Hash)], []))
        };
        var error = await ResultAssert.ErrorAsync(() => Service().Save(request with { Drafts = [draft] }));
        Assert.StartsWith("CONCLUSION_CLAIM_VALUE_MISMATCH:", error.Title);
        Assert.Empty((await Service().GetEffects(request.CompanyId, request.NewsMaterialId)).RequireContent());
        var corrected = draft with { Conclusion = "Расходы могут вырасти на 8000 RUB за 2026-11. Проверьте условия договора.", CalculationClaim = new("8000 RUB", "8000", "RUB", "2026-11") };
        var saved = (await Service().Save(request with { Drafts = [corrected] }).RequireContentAsync()).Effects.Single().CurrentRevision;
        Assert.Equal("8000", saved.CalculationResult);
        Assert.Equal(corrected.Conclusion, saved.Conclusion);
        Assert.Equal(corrected.CalculationClaim, saved.CalculationClaim);
    }

    [Fact]
    public async Task ComparisonPeriodRejectsFreeTextAmountsAndDifferentFramesRemainDistinct()
    {
        var request = Request("news-a", "v1");
        var absolute = request.Drafts[0];
        var eventOnly = absolute with
        {
            Question = absolute.Question! with
            {
                Frame = "event_only"
            }
        };
        var delta = absolute with
        {
            Question = absolute.Question! with
            {
                Frame = "budget_delta",
                BaselinePeriod = "budget:2026-11"
            }
        };
        var receipt = await (Service().Save(request with
        {
            Drafts = [absolute, eventOnly, delta]
        })).RequireContentAsync();
        Assert.Equal(3, receipt.Effects.Select(e => e.EffectId).Distinct().Count());
        var invalid = Request("news-b", "v1", mechanism: "rent_indexation");
        invalid = invalid with
        {
            Drafts = [invalid.Drafts[0] with
            {
                Question = invalid.Drafts[0].Question!with
                {
                    Frame = "budget_delta",
                    BaselinePeriod = "2026-11 budget 14000 RUB"
                }
            }

            ]
        };
        Assert.Equal("A stable mechanism, frame, unit, target period and comparison baseline for budget deltas are required.", (await ResultAssert.ErrorAsync(async () => await Service().Save(invalid))).Title);
    }

    [Fact]
    public async Task ConcurrentQuestionCreationInvalidatesWholeBeforeSnapshot()
    {
        var snapshot = (await (Service().GetSnapshot(company, "news-a")).RequireContentAsync());
        Assert.Equal(0, snapshot.StateVersion);
        await (Service().Save(Request("news-a", "v1"))).RequireContentAsync();
        var stale = Request("news-b", "v1", mechanism: "rent_indexation") with
        {
            ExpectedStateVersion = snapshot.StateVersion
        };
        var error = await ResultAssert.ErrorAsync(async () => await Service().Save(stale));
        Assert.Equal("The immutable company snapshot changed before atomic commit.", error.Title);
        Assert.Single((await (Service().GetSnapshot(company, "news-b")).RequireContentAsync()).Effects);
        await ResultAssert.ErrorAsync(async () => await Service().GetAnalysis(company, "news-b", "v1"));
    }

    [Fact]
    public async Task DraftOrderDoesNotChangeAdmissionOrReceiptIdentity()
    {
        var first = Request("news-a", "v1");
        var rent = Request("news-a", "v1", mechanism: "rent_indexation").Drafts.Single();
        var request = first with
        {
            Drafts = [first.Drafts.Single(), rent]
        };
        var saved = await (Service().Save(request)).RequireContentAsync();
        var replay = await (Service().Save(request with
        {
            Drafts = request.Drafts.Reverse().ToArray()
        })).RequireContentAsync();
        Assert.True(replay.Duplicate);
        Assert.Equal(saved.AnalysisId, replay.AnalysisId);
        Assert.All((await (Service().GetEffects(company, "news-a")).RequireContentAsync()), q => Assert.Single(q.History));
    }

    [Fact]
    public async Task DifferentMechanismsDoNotMergeEvenWithSameCoarseMetricAndObject()
    {
        var finance = Request("news-a", "v1");
        var rent = Request("news-a", "v1", mechanism: "rent_indexation").Drafts.Single();
        var receipt = await (Service().Save(finance with
        {
            Drafts = [finance.Drafts.Single(), rent]
        })).RequireContentAsync();
        Assert.Equal(2, receipt.Effects.Select(e => e.EffectId).Distinct().Count());
        Assert.Equal(2, (await (Service().GetEffects(company, "news-b")).RequireContentAsync()).Count);
    }

    private sealed class Sources(DateTimeOffset at, Guid company) : ISourceResolver
    {
        public async Task<Result<MaterialVersion>> GetAsync(string materialId, string versionId, CancellationToken ct)
        {
            return new(await Task.FromResult(new MaterialVersion(materialId, versionId, "synthetic", materialId.StartsWith("news") ? "news" : "company", "Fixture", "Synthetic original for FixtureLoan", at, at, versionId == "v2" ? "v1" : null, materialId.StartsWith("news") ? [] : [company], [], Hash)), null);
        }
    }

    public void Dispose() => database.Dispose();
}
