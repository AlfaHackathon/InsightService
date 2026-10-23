using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using AlfaHackaton.Result;
using AlfaHackaton.Result.Errors;
using InsightService.Common;
using InsightService.Contracts.Analyses;
using InsightService.Contracts.Context;
using InsightService.Contracts.Execution;
using InsightService.Contracts.Projections;
using InsightService.Models;
using InsightService.Services.Interfaces;

namespace InsightService.Services;

public sealed class CompanyExecutionService(
    ExecutionJobReader jobs,
    AnalysisPreparationService preparation,
    CompanyKnowledgeClient company,
    ContextEvidenceClient evidence,
    ISourceResolver sources)
{
    public async Task<Result<CompanyPlanResponse>> PlanAsync(CompanyPlanRequest input, CancellationToken ct)
    {
        var loaded = await jobs.ReadAsync(input.JobId);
        if (loaded.Error is { } jobError)
        {
            return new(jobError);
        }

        var job = loaded.Content!;
        if (input.CompanyId == Guid.Empty || !job.Checkpoint.SelectedCompanyIds.Concat(job.Checkpoint.PendingCompanyIds).Contains(input.CompanyId))
        {
            return new(Invalid("The company must belong to the persisted job selection."));
        }

        var profile = await company.ProfileAsync(input.CompanyId, job.Request.AsOf, ct);
        if (profile.Error is { } profileError && profileError.HttpStatusCode != HttpStatusCode.NotFound)
        {
            return new(profileError);
        }

        var inventory = await evidence.InventoryAsync(input.CompanyId, job.Request.AsOf, ct);
        if (inventory.Error is { } inventoryError)
        {
            return new(inventoryError);
        }

        var news = await sources.GetAsync(job.Request.NewsMaterialId, job.Request.NewsVersionId, ct);
        if (news.Error is { } newsError)
        {
            return new(newsError);
        }

        if (news.Content!.Kind != "news" || news.Content.KnownAt > job.Request.AsOf)
        {
            return new(Invalid("The triggering news must be available at the job knowledge time."));
        }

        try
        {
            return await PreparePlanAsync(input, job, profile.Content, inventory.Content!, news.Content, ct);
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or KeyNotFoundException)
        {
            return new(Invalid("The persisted company planning inputs are malformed."));
        }
    }

    private async Task<Result<CompanyPlanResponse>> PreparePlanAsync(
        CompanyPlanRequest input,
        ExecutionJobContext job,
        CompanyIdentity? profile,
        DepthInventory inventory,
        MaterialVersion news,
        CancellationToken ct)
    {
        var request = job.Request;
        var scopedPlan = ScopedPlan(job.Checkpoint, input.CompanyId);
        var scope = new AnalysisScope
        {
            CompanyId = input.CompanyId,
            NewsMaterialId = request.NewsMaterialId,
            NewsVersionId = request.NewsVersionId,
            AsOf = request.AsOf,
            BankProfileId = profile?.BankProfileId,
            Trigger = request.Trigger,
            ProjectionOnly = request.ProjectionOnly,
            Mechanisms = request.Trigger is null && scopedPlan["mechanisms"] is JsonArray { Count: > 0 } mechanisms
                ? mechanisms.Select(n => n!.GetValue<string>()).Distinct(StringComparer.Ordinal).ToArray() : null
        };
        var depthResult = await preparation.DepthAsync(new()
        {
            CompanyId = input.CompanyId,
            Request = scope,
            Plan = JsonSerializer.SerializeToElement(scopedPlan, AnalysisProtocol.Json),
            Inventory = inventory,
            HasProfile = profile is not null,
            NewsCorrection = news.CorrectsVersionId is not null,
            Relevance = input.Relevance
        }, ct);
        if (depthResult.Error is { } depthError)
        {
            return new(depthError);
        }

        var depth = (DepthDecisionResponse)depthResult.Content!;
        var relevance = await PrepareRelevanceAsync(input, inventory, depth, request.AsOf, ct);
        if (relevance.Error is { } relevanceError)
        {
            return new(relevanceError);
        }

        var watchRegistered = job.Job.Checkpoint.TryGetProperty("watchCursors", out var watches) && watches.ValueKind == JsonValueKind.Array &&
            watches.EnumerateArray().Any(w => w.TryGetProperty("companyId", out var id) && id.GetGuid() == input.CompanyId);
        var receipt = watchRegistered ? await ResolveReceiptAsync(scope, depth, ct) : new Result<AnalysisReceipt?>(null, null);
        if (receipt.Error is { } receiptError)
        {
            return new(receiptError);
        }

        var analysisInput = JsonSerializer.SerializeToNode(new
        {
            scope.CompanyId,
            scope.BankProfileId,
            scope.NewsMaterialId,
            scope.NewsVersionId,
            scope.AsOf,
            retrievalQueries = scopedPlan["queries"]?.Deserialize<string[]>(AnalysisProtocol.Json) ?? [],
            request.RetrievalLimit,
            scope.Trigger,
            scope.Mechanisms
        }, AnalysisProtocol.Json)!.AsObject();
        foreach (var property in analysisInput.Where(p => p.Value is null).Select(p => p.Key).ToArray())
        {
            analysisInput.Remove(property);
        }

        return new(new CompanyPlanResponse
        {
            AnalysisInput = analysisInput,
            Depth = depth,
            WatchAfterSequence = inventory.WatchAfterSequence,
            RequiresWatchRegistration = !watchRegistered,
            RelevanceSources = relevance.Content!,
            RelevancePlan = scopedPlan,
            Receipt = receipt.Content,
            SegmentKeys = job.Checkpoint.SegmentBatches.SelectMany(b => b.GetProperty("segments").EnumerateArray())
                .Where(s => s.GetProperty("companyIds").EnumerateArray().Any(id => id.GetGuid() == input.CompanyId))
                .Select(s => s.GetProperty("key").GetString()!).Distinct(StringComparer.Ordinal).ToArray()
        }, null);
    }

    private async Task<Result<MaterialVersion[]>> PrepareRelevanceAsync(
        CompanyPlanRequest input,
        DepthInventory inventory,
        DepthDecisionResponse depth,
        DateTimeOffset asOf,
        CancellationToken ct)
    {
        var own = inventory.Sources.Where(s => s.SourceId != "provided-bank-csv").ToArray();
        var expected = own.Select(s => new SourceRef(s.MaterialId, s.VersionId, s.ContentHash)).ToHashSet();
        if (input.Relevance is not null && (input.Relevance.Length > 8 || input.Relevance.Length != expected.Count ||
            input.Relevance.Select(r => r.SourceRef).Distinct().Count() != expected.Count || input.Relevance.Any(r =>
                !expected.Contains(r.SourceRef) || r.Status is not ("related" or "unrelated" or "unknown") || r.Quote is null || r.Quote.Length > 1600)))
        {
            return new(Invalid("Relevance proposals must account for every exact own original."));
        }

        var shouldClassify = depth.Reason == "own_originals" && inventory.CompleteForLoadedScope && own.Length is > 0 and <= 8;
        if (!shouldClassify && input.Relevance is null)
        {
            return new([], null);
        }

        var materials = new List<MaterialVersion>();
        foreach (var reference in own)
        {
            var resolved = await sources.GetAsync(reference.MaterialId, reference.VersionId, ct);
            if (resolved.Error is { } error)
            {
                return new(error);
            }

            var material = resolved.Content!;
            if (material.Kind != "company" || !material.CompanyIds.Contains(input.CompanyId) || material.KnownAt > asOf || material.ContentHash != reference.ContentHash)
            {
                return new(Invalid("Relevance sources must match the exact company inventory."));
            }

            var proposal = input.Relevance?.Single(r => r.SourceRef == new SourceRef(reference.MaterialId, reference.VersionId, reference.ContentHash));
            if (proposal is { Status: not "unknown" } && (proposal.Quote.Length == 0 || !material.Text.Contains(proposal.Quote, StringComparison.Ordinal)))
            {
                return new(Invalid("Relevance proposals must quote their exact original."));
            }

            materials.Add(material);
        }
        return new(shouldClassify && JsonSerializer.Serialize(materials, AnalysisProtocol.Json).Length <= 60000 ? materials.ToArray() : [], null);
    }

    private async Task<Result<AnalysisReceipt?>> ResolveReceiptAsync(
        AnalysisScope scope,
        DepthDecisionResponse depth,
        CancellationToken ct)
    {
        if (depth.Level == "canonical_projection")
        {
            var canonical = await preparation.CanonicalCompanyAsync(scope);
            if (canonical.Error is null)
            {
                return new(canonical.Content, null);
            }

            if (canonical.Error.HttpStatusCode != HttpStatusCode.UnprocessableEntity)
            {
                return new(canonical.Error);
            }
        }
        if (depth.Level == "company_deep")
        {
            return new(null, null);
        }

        var shallow = await preparation.ApplicabilityAsync(scope, ct);
        if (shallow.Error is null)
        {
            return new(shallow.Content, null);
        }

        if (shallow.Error.HttpStatusCode != HttpStatusCode.Conflict)
        {
            return new(shallow.Error);
        }

        depth.Level = "company_deep";
        depth.Reason = "prior_canonical_state";
        depth.MissingFacts = [];
        return new(null, null);
    }
    private static JsonObject ScopedPlan(JobExecutionCheckpoint checkpoint, Guid companyId)
    {
        var plan = JsonNode.Parse(checkpoint.Plan.GetRawText())!.AsObject();
        if (plan["mechanisms"] is not JsonArray mechanisms || mechanisms.Count == 0)
        {
            return plan;
        }
        var linked = new HashSet<string>(StringComparer.Ordinal);
        foreach (var batch in checkpoint.SegmentBatches)
        {
            var companySegments = batch.GetProperty("segments").EnumerateArray()
                .Where(s => s.GetProperty("companyIds").EnumerateArray().Any(id => id.GetGuid() == companyId))
                .Select(s => s.GetProperty("key").GetString()).ToHashSet(StringComparer.Ordinal);
            var batchPlan = batch.GetProperty("plan");
            if (!batchPlan.TryGetProperty("segmentCoverage", out var coverage))
            {
                continue;
            }
            var insights = batchPlan.GetProperty("generalInsights");
            foreach (var row in coverage.EnumerateArray().Where(r => companySegments.Contains(r.GetProperty("segmentKey").GetString())))
            {
                foreach (var index in row.GetProperty("insightIndexes").EnumerateArray())
                {
                    var insight = insights[index.GetInt32()];
                    if (insight.TryGetProperty("signalIds", out var ids))
                    {
                        foreach (var id in ids.EnumerateArray())
                        {
                            linked.Add(id.GetString()!);
                        }
                    }
                }
            }
        }
        var scoped = mechanisms.Select(m => m!.GetValue<string>()).ToHashSet(StringComparer.Ordinal);
        foreach (var signal in checkpoint.SignalInventories.SelectMany(i => i.GetProperty("signals").EnumerateArray()))
        {
            if (!linked.Contains(signal.GetProperty("signalId").GetString()!))
            {
                continue;
            }

            foreach (var mechanism in signal.GetProperty("mechanisms").EnumerateArray())
            {
                scoped.Add(mechanism.GetString()!);
            }
        }
        plan["mechanisms"] = new JsonArray(scoped.Select(m => JsonValue.Create(m)).ToArray());
        return plan;
    }

    private static Error Invalid(string message) => new(message, ErrorSource.Client, ErrorType.CorruptedInfo) { HttpStatusCode = HttpStatusCode.UnprocessableEntity };
}
