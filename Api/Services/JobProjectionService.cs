using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using AlfaHackaton.Result;
using AlfaHackaton.Result.Errors;
using InsightService.Common;
using InsightService.Contracts.Execution;
using InsightService.Contracts.Projections;
using InsightService.Models;
using InsightService.Services.Interfaces;

namespace InsightService.Services;

public sealed class JobProjectionService(
    ExecutionJobReader jobs,
    ProjectionService projections,
    AnalysisPreparationService analyses,
    CompanyKnowledgeClient company,
    ISourceResolver sources)
{
    public async Task<Result<JobProjectionResponse>> ProjectAsync(JobProjectionRequest input, CancellationToken ct)
    {
        try
        {
            return await ProjectCoreAsync(input, ct);
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or KeyNotFoundException)
        {
            return new(Invalid("The job checkpoint or role action proposal is malformed."));
        }
    }

    private async Task<Result<JobProjectionResponse>> ProjectCoreAsync(JobProjectionRequest input, CancellationToken ct)
    {
        var loaded = await jobs.ReadAsync(input.JobId);
        if (loaded.Error is { } jobError)
        {
            return new(jobError);
        }

        var job = loaded.Content!;
        var request = job.Request;
        var checkpoint = job.Checkpoint;
        var selected = checkpoint.SelectedCompanyIds.Concat(checkpoint.PendingCompanyIds).Distinct().ToArray();
        var scope = await company.ScopeAsync(request.AsOf, request.PersonIds, selected, ct);
        if (scope.Error is { } scopeError)
        {
            return new(scopeError);
        }

        var groupIds = checkpoint.Groups.Select(g => g.GroupId).Concat(request.GroupIds).Distinct(StringComparer.Ordinal).ToArray();
        if (groupIds.Length > 64)
        {
            return new(Invalid("The job group selection exceeds the supported scope."));
        }

        var groups = new List<GroupExpansion>();
        foreach (var id in groupIds)
        {
            var expansion = await company.GroupAsync(id, request.AsOf, ct);
            if (expansion.Error is { } groupError)
            {
                return new(groupError);
            }

            groups.Add(expansion.Content!);
        }
        var runs = checkpoint.Companies.ToList();
        foreach (var id in groups.SelectMany(g => g.CompanyIds).Concat(scope.Content!.CompanyIds).Concat(selected).Distinct())
        {
            if (runs.Any(r => r.CompanyId == id))
            {
                continue;
            }

            var receipt = await analyses.CanonicalCompanyAsync(new() { CompanyId = id, NewsMaterialId = request.NewsMaterialId, NewsVersionId = request.NewsVersionId, AsOf = request.AsOf });
            runs.Add(new()
            {
                CompanyId = id,
                Status = receipt.Error is null ? "completed" : "failed",
                Receipt = receipt.Content,
                Error = receipt.Error is null ? null : "NO_CANONICAL_STATE",
                ContextComplete = receipt.Content?.ContextComplete
            });
        }
        var canonical = await projections.CanonicalAsync(new() { Companies = runs.ToArray(), Groups = groups.ToArray(), AsOf = request.AsOf });
        if (canonical.Error is { } canonicalError)
        {
            return new(canonicalError);
        }

        var roles = await PrepareRolesAsync(input, job, canonical.Content!, groups.ToArray(), scope.Content!, ct);
        if (roles.Error is { } roleError)
        {
            return new(roleError);
        }

        return await BuildResultAsync(input, job, canonical.Content!, groups.ToArray(), scope.Content!, roles.Content.Scopes, roles.Content.Views, ct);
    }

    private async Task<Result<(RoleReasoningScope[] Scopes, JsonObject[] Views)>> PrepareRolesAsync(
        JobProjectionRequest input,
        ExecutionJobContext job,
        CanonicalProjection canonical,
        GroupExpansion[] groups,
        CompanyRoleScope scope,
        CancellationToken ct)
    {
        var roleScopes = new List<RoleReasoningScope>();
        var roleViews = new List<JsonObject>();
        var supplied = input.RoleViews ?? [];
        var suppliedKeys = supplied.Where(p => p.ValueKind == JsonValueKind.Object && p.TryGetProperty("key", out var key) && key.ValueKind == JsonValueKind.String)
            .Select(p => p.GetProperty("key").GetString()).ToHashSet(StringComparer.Ordinal);
        var proposals = supplied.Concat(job.Checkpoint.RoleViews.Where(p => p.ValueKind == JsonValueKind.Object && p.TryGetProperty("key", out var key) &&
            key.ValueKind == JsonValueKind.String && !suppliedKeys.Contains(key.GetString()) && p.TryGetProperty("status", out var status) &&
            status.ValueKind == JsonValueKind.String && status.GetString() is "completed" or "proposed")).ToArray();
        if (supplied.Length > scope.Roles.Length)
        {
            return new(Invalid("Role proposals must belong to the authoritative job role scope."));
        }

        foreach (var role in scope.Roles)
        {
            var key = $"{role.PersonId}:{role.CompanyId:D}:{role.RevisionId}";
            var view = await projections.RoleAsync(new()
            {
                PersonId = role.PersonId,
                CompanyId = role.CompanyId,
                AsOf = job.Request.AsOf,
                Companies = canonical.Companies,
                Groups = groups
            }, ct);
            if (view.Error is { } roleError)
            {
                roleViews.Add(new JsonObject { ["key"] = key, ["personId"] = role.PersonId, ["companyId"] = role.CompanyId.ToString("D"), ["status"] = "failed", ["error"] = roleError.Title });
                continue;
            }
            var visible = view.Content!;
            var result = CreateRoleView(role, visible, key);
            var applied = ApplyProposal(result, visible, proposals, suppliedKeys.Contains(key));
            if (applied.Error is { } proposalError)
            {
                return new(proposalError);
            }

            if (applied.Content is { } reasoningScope)
            {
                roleScopes.Add(reasoningScope);
            }

            roleViews.Add(result);
        }
        var visibleKeys = roleViews.Select(v => v["key"]!.GetValue<string>()).ToHashSet(StringComparer.Ordinal);
        if (supplied.Any(p => p.ValueKind != JsonValueKind.Object || !p.TryGetProperty("key", out var key) || key.ValueKind != JsonValueKind.String || !visibleKeys.Contains(key.GetString()!)))
        {
            return new(Invalid("Role proposals must belong to the authoritative job role scope."));
        }

        return new((roleScopes.ToArray(), roleViews.ToArray()), null);
    }

    private static JsonObject CreateRoleView(
        RoleProfile role,
        RoleProjectionResponse visible,
        string key)
    {
        var scopeVersion = Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new
        {
            visible.Profile,
            visible.Facts,
            visible.Groups,
            role.RevisionId,
            role.AllowedCompanyIds,
            role.AllowedMaterialIds,
            role.SourceRefs
        }, AnalysisProtocol.Json)));
        var result = JsonSerializer.SerializeToNode(visible.Profile, AnalysisProtocol.Json)!.AsObject();
        result["key"] = key;
        result["scopeVersion"] = scopeVersion;
        result["facts"] = JsonSerializer.SerializeToNode(visible.Facts, AnalysisProtocol.Json);
        result["groups"] = JsonSerializer.SerializeToNode(visible.Groups, AnalysisProtocol.Json);
        return result;
    }

    private static Result<RoleReasoningScope?> ApplyProposal(
        JsonObject result,
        RoleProjectionResponse visible,
        JsonElement[] proposals,
        bool explicitlySupplied)
    {
        var key = result["key"]!.GetValue<string>();
        var scopeVersion = result["scopeVersion"]!.GetValue<string>();
        if (visible.Facts.Length == 0)
        {
            result["status"] = "withheld";
            result["actions"] = new JsonArray();
            result["missingContext"] = new JsonArray("No source-complete facts are available within this explicit read grant.");
            return new(null, null);
        }
        var matches = proposals.Where(p => p.TryGetProperty("key", out var k) && k.ValueKind == JsonValueKind.String && k.GetString() == key).ToArray();
        if (matches.Length > 1)
        {
            return new(Invalid("A role action proposal may occur only once."));
        }

        var matchingVersion = matches.Length == 1 && matches[0].TryGetProperty("scopeVersion", out var version) && version.ValueKind == JsonValueKind.String && version.GetString() == scopeVersion;
        if (!matchingVersion && explicitlySupplied)
        {
            return new(new Error("The canonical facts or role scope changed while preparing the role actions; repeat reasoning for the current scope.", ErrorSource.Client, ErrorType.CorruptedInfo) { HttpStatusCode = HttpStatusCode.Conflict });
        }

        if (!matchingVersion)
        {
            result["status"] = "pending";
            return new(new RoleReasoningScope { Key = key, ScopeVersion = scopeVersion, Profile = visible.Profile, Facts = visible.Facts, Groups = visible.Groups }, null);
        }
        var proposal = matches[0];
        if (proposal.TryGetProperty("error", out var failure) && failure.ValueKind == JsonValueKind.String)
        {
            result["status"] = "failed";
            result["error"] = failure.GetString() is { Length: <= 200 } code ? code : "ROLE_ANALYSIS_FAILED";
            return new(null, null);
        }
        var validation = ValidateActions(proposal, visible.Facts);
        if (validation.Error is { } actionError)
        {
            return new(actionError);
        }

        result["status"] = "completed";
        result["actions"] = JsonNode.Parse(proposal.GetProperty("actions").GetRawText());
        result["missingContext"] = JsonNode.Parse(proposal.GetProperty("missingContext").GetRawText());
        return new(null, null);
    }

    private async Task<Result<JobProjectionResponse>> BuildResultAsync(
        JobProjectionRequest input,
        ExecutionJobContext job,
        CanonicalProjection canonical,
        GroupExpansion[] groups,
        CompanyRoleScope scope,
        RoleReasoningScope[] roleScopes,
        JsonObject[] roleViews,
        CancellationToken ct)
    {
        var request = job.Request;
        var checkpoint = job.Checkpoint;
        var selected = checkpoint.SelectedCompanyIds.Concat(checkpoint.PendingCompanyIds).Distinct().ToArray();
        var companyState = canonical.Companies;
        var groupViews = groups.Select(g => Models.Policies.ProjectionPolicy.Group(g, companyState)).ToArray();
        var news = await sources.GetAsync(request.NewsMaterialId, request.NewsVersionId, ct);
        if (news.Error is { } newsError)
        {
            return new(newsError);
        }

        var material = news.Content!;
        if (material.KnownAt > request.AsOf || material.Kind != "news")
        {
            return new(Invalid("The news is unavailable at job knowledge time."));
        }

        var analyzed = checkpoint.Companies.Select(c => companyState.Single(x => x.CompanyId == c.CompanyId)).ToArray();
        var segmented = checkpoint.SegmentBatches.SelectMany(b => b.GetProperty("segments").EnumerateArray())
            .SelectMany(s => s.GetProperty("companyIds").EnumerateArray()).Select(id => id.GetGuid()).ToHashSet();
        var resultDocument = new
        {
            news = new { material.MaterialId, material.VersionId, material.ContentHash },
            generalInsights = checkpoint.Plan.GetProperty("generalInsights"),
            segmentBatches = checkpoint.SegmentBatches,
            segmentCoverageAvailable = input.SegmentCoverageAvailable,
            unsegmentedCompanyIds = selected.Where(id => !segmented.Contains(id)).ToArray(),
            companies = analyzed,
            rankedCompanyFacts = canonical.Facts,
            groups = groupViews,
            roles = roleViews,
            coverage = new
            {
                selectedCompanies = selected.Length,
                pendingCompanyIds = checkpoint.PendingCompanyIds,
                failedCompanyIds = checkpoint.Companies.Where(c => c.Status == "failed").Select(c => c.CompanyId).ToArray(),
                missingPersonIds = scope.MissingPersonIds,
                unrevisedEffectIds = analyzed.SelectMany(c => c.Receipt?.UnrevisedEffectIds ?? []).ToArray(),
                unassessedCompanyIds = analyzed.Where(c => c.Receipt?.UnpublishedDrafts is { Length: > 0 }).Select(c => c.CompanyId).ToArray(),
                staleEffectIds = companyState.SelectMany(c => c.StaleEffectIds ?? []).ToArray(),
                discoveryExhaustive = false,
                newsTextCompleteness = request.TextCompleteness,
                completeForSelectedScopes = request.TextCompleteness == "full" && checkpoint.PendingCompanyIds.Length == 0 && checkpoint.RecipientCursor is null &&
                    companyState.All(c => c.Status == "completed" && c.StaleEffectIds is not { Length: > 0 }) && scope.MissingPersonIds.Length == 0 &&
                    analyzed.All(c => c.Status == "completed" && c.ContextComplete == true && c.Receipt?.UnrevisedEffectIds is not { Count: > 0 } && c.Receipt?.UnpublishedDrafts is not { Length: > 0 }) &&
                    selected.All(id => analyzed.Any(c => c.CompanyId == id)) && groupViews.All(g => g.Complete) && roleViews.All(v => v["status"]?.GetValue<string>() == "completed")
            }
        };
        return new(new JobProjectionResponse
        {
            CanonicalCompanies = companyState,
            RoleScopes = roleScopes,
            RoleViews = roleViews,
            Result = JsonSerializer.SerializeToNode(resultDocument, AnalysisProtocol.Json)!.AsObject()
        }, null);
    }
    private static Result ValidateActions(JsonElement proposal, CanonicalFact[] facts)
    {
        if (!proposal.TryGetProperty("actions", out var actions) || actions.ValueKind != JsonValueKind.Array || actions.GetArrayLength() > 12 ||
            !proposal.TryGetProperty("missingContext", out var missing) || missing.ValueKind != JsonValueKind.Array || missing.GetArrayLength() > 16 ||
            missing.EnumerateArray().Any(m => m.ValueKind != JsonValueKind.String || m.GetString()!.Length is < 1 or > 1000))
        {
            return new(Invalid("A bounded role action proposal is required."));
        }

        var ids = facts.Select(f => f.EffectId).ToHashSet(StringComparer.Ordinal);
        foreach (var action in actions.EnumerateArray())
        {
            if (!action.TryGetProperty("action", out var text) || text.ValueKind != JsonValueKind.String || text.GetString()!.Length is < 1 or > 2000 ||
                !action.TryGetProperty("rationale", out var rationale) || rationale.ValueKind != JsonValueKind.String || rationale.GetString()!.Length is < 1 or > 2000 ||
                !action.TryGetProperty("priority", out var priority) || !priority.TryGetInt32(out var score) || score is < 0 or > 5 ||
                !action.TryGetProperty("effectIds", out var effects) || effects.ValueKind != JsonValueKind.Array || effects.GetArrayLength() is < 1 or > 32 ||
                effects.EnumerateArray().Any(e => e.ValueKind != JsonValueKind.String || !ids.Contains(e.GetString()!)))
            {
                return new(Invalid("A role action cites a fact outside the permitted projection or has an invalid shape."));
            }
        }
        return new();
    }

    private static Error Invalid(string message) => new(message, ErrorSource.Client, ErrorType.CorruptedInfo) { HttpStatusCode = HttpStatusCode.UnprocessableEntity };
}
