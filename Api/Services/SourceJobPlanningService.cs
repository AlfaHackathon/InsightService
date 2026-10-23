using System.Net;
using AlfaHackaton.Result;
using AlfaHackaton.Result.Errors;
using InsightService.Contracts.Execution;
using InsightService.Repositories.Interfaces;

namespace InsightService.Services;

public sealed class SourceJobPlanningService(
    ExecutionJobReader jobs,
    IEffectRepository effects,
    CompanyKnowledgeClient company,
    ContextEvidenceClient context)
{
    public async Task<Result<SourceJobsResponse>> SourceJobsAsync(SourceJobsRequest input, CancellationToken ct)
    {
        var change = input.Event;
        if (change is null || change.Sequence < 0 || change.KnownAt == default || change.CompanyIds is null)
        {
            return new(Invalid());
        }

        if (!Supported(change.Category))
        {
            return new(new SourceJobsResponse(), null);
        }

        if (change.Sequence < 1)
        {
            return new(Invalid());
        }
        var events = change.Category.StartsWith("material:", StringComparison.Ordinal)
            ? await context.ChangesAsync(change.Sequence - 1, ct)
            : await company.ChangesAsync(change.Sequence - 1, ct);
        if (events.Error is { } eventError)
        {
            return new(eventError);
        }
        var original = events.Content?.Changes.SingleOrDefault(e => e.Sequence == change.Sequence);
        if (original is null || original.Category != change.Category || original.EntityId != change.EntityId || original.VersionId != change.VersionId ||
            original.KnownAt != change.KnownAt || !original.CompanyIds.ToHashSet().SetEquals(change.CompanyIds))
        {
            return new(Invalid());
        }
        var reacted = await effects.React(new(change.Category, change.EntityId, change.VersionId, change.KnownAt, change.CompanyIds, input.AfterQuestion));
        if (reacted.Error is { } error)
        {
            return new(error);
        }

        var page = reacted.Content!;
        var groupIds = Array.Empty<string>();
        if (page.Items.Length > 0)
        {
            var groups = await company.GroupsAsync(change.KnownAt, ct);
            if (groups.Error is { } groupError)
            {
                return new(groupError);
            }

            groupIds = groups.Content!.Groups.Select(g => g.GroupId).Order(StringComparer.Ordinal).ToArray();
        }
        var groupBatches = groupIds.Chunk(32).ToArray();
        if (groupBatches.Length == 0)
        {
            groupBatches = [[]];
        }

        var requests = page.Items.GroupBy(w => (w.NewsMaterialId, w.NewsVersionId))
            .SelectMany(anchor => groupBatches.Select(groups => Create(anchor.Key.NewsMaterialId, anchor.Key.NewsVersionId,
                change.KnownAt, anchor.Select(w => w.CompanyId).Distinct().OrderBy(id => id.ToString("D"), StringComparer.Ordinal).ToArray(), groups, [], "unknown", change)))
            .ToArray();
        return new(new SourceJobsResponse { Requests = requests, NextCursor = page.NextCursor }, null);
    }

    public async Task<Result<CompanyWatchResponse>> CompanyWatchAsync(CompanyWatchRequest input, CancellationToken ct)
    {
        if (input.CompanyId == Guid.Empty || input.AfterSequence < 0)
        {
            return new(Invalid());
        }

        var loaded = await jobs.ReadAsync(input.JobId);
        if (loaded.Error is { } error)
        {
            return new(error);
        }

        var job = loaded.Content!;
        if (!job.Checkpoint.SelectedCompanyIds.Contains(input.CompanyId))
        {
            return new(Invalid());
        }

        var after = input.AfterSequence;
        var requests = new List<JobFlowParameters>();
        var page = await context.ChangesAsync(after, ct);
        if (page.Error is { } sourceError)
        {
            return new(sourceError);
        }

        if (page.Content!.Changes is null)
        {
            return new(Invalid());
        }

        var events = page.Content.Changes.Where(e => e.Sequence > after).OrderBy(e => e.Sequence).Take(64).ToArray();
        foreach (var change in events)
        {
            if (MatchesCompany(change, input.CompanyId))
            {
                var scope = job.Request;
                requests.Add(Create(scope.NewsMaterialId, scope.NewsVersionId,
                    change.KnownAt > scope.AsOf ? change.KnownAt : scope.AsOf, [input.CompanyId], scope.GroupIds,
                    scope.PersonIds, scope.TextCompleteness, change));
            }
            after = change.Sequence;
        }
        return new(new CompanyWatchResponse { Requests = requests.ToArray(), AfterSequence = after, Complete = page.Content.Changes.Length == 0 }, null);
    }

    private static JobFlowParameters Create(
        string newsMaterialId,
        string newsVersionId,
        DateTimeOffset asOf,
        Guid[] companyIds,
        string[] groupIds,
        string[] personIds,
        string textCompleteness,
        SourceEvent change) => new()
        {
            NewsMaterialId = newsMaterialId,
            NewsVersionId = newsVersionId,
            AsOf = asOf,
            TargetCompanyIds = companyIds,
            GroupIds = groupIds,
            PersonIds = personIds,
            CompanyBudget = 4,
            TextCompleteness = textCompleteness,
            ProjectionOnly = !change.Category.StartsWith("material:", StringComparison.Ordinal),
            Trigger = change.Category.StartsWith("material:", StringComparison.Ordinal) ? new(change.EntityId, change.VersionId) : null
        };

    private static bool Supported(string category) => category is "material:company" or "material:market" or "company" or "group" or "role";
    private static bool MatchesCompany(SourceEvent change, Guid companyId) =>
        change.Category == "material:company" ? change.CompanyIds.Contains(companyId) :
        change.Category is "material:market" or "group" or "role" or "company" && (change.CompanyIds.Length == 0 || change.CompanyIds.Contains(companyId));
    private static Error Invalid() => new("The source job planning scope is invalid.", ErrorSource.Client, ErrorType.CorruptedInfo) { HttpStatusCode = HttpStatusCode.BadRequest };
}
