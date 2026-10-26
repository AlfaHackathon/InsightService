using System.Net;
using AlfaHackaton.Result;
using AlfaHackaton.Result.Errors;
using InsightService.Contracts.Projections;
using InsightService.Models.Policies;
using InsightService.Repositories.Interfaces;
using InsightService.Services.Interfaces;

namespace InsightService.Services;

public sealed class ProjectionService(
    IEffectRepository repository,
    CompanyKnowledgeClient company,
    ISourceResolver sources)
{
    public async Task<Result<CanonicalProjection>> CanonicalAsync(ProjectionRequest request)
    {
        if (request.Companies is null || request.Companies.Length > 10000 || request.Companies.Any(c => c.CompanyId == Guid.Empty) || request.AsOf == default(DateTimeOffset))
        {
            return new(InvalidRequest());
        }
        var companies = new List<CompanyProjection>();
        foreach (var run in request.Companies.DistinctBy(c => c.CompanyId))
        {
            if (run.Receipt is not { } identity)
            {
                companies.Add(new() { CompanyId = run.CompanyId, Status = "failed", Error = "NO_CANONICAL_STATE" });
                continue;
            }
            var asOf = request.AsOf ?? identity.AsOf;
            var stored = await repository.FindAnalysis(run.CompanyId, identity.NewsMaterialId, identity.NewsVersionId, identity.Trigger);
            if (stored is null || stored.Receipt.AsOf > asOf)
            {
                companies.Add(new() { CompanyId = run.CompanyId, Status = "failed", Error = "NO_CANONICAL_STATE" });
                continue;
            }
            var receipt = stored.Receipt;
            if (receipt.Outcome == "no_insight")
            {
                // Historical heads remain readable but are not new consequences of
                // this news. Do not turn a diagnostic receipt into actionable facts.
                companies.Add(new()
                {
                    CompanyId = run.CompanyId,
                    Status = "completed",
                    Receipt = receipt,
                    ContextComplete = receipt.ContextComplete,
                    StaleEffectIds = []
                });
                continue;
            }
            var snapshot = await repository.GetSnapshot(run.CompanyId, receipt.NewsMaterialId, asOf);
            var heads = receipt.Effects.Where(e => e.CurrentRevision.AsOf <= asOf).ToDictionary(e => e.EffectId, StringComparer.Ordinal);
            foreach (var effect in snapshot.Effects)
            {
                var revision = effect.History.LastOrDefault(r => r.AsOf <= asOf);
                if (revision is not null)
                {
                    heads[effect.EffectId] = new(effect.EffectId, revision.RevisionId, revision.PreviousRevisionId, false, revision);
                }
            }
            companies.Add(new()
            {
                CompanyId = run.CompanyId,
                Status = "completed",
                Receipt = receipt with { Effects = heads.Values.ToArray() },
                ContextComplete = receipt.ContextComplete,
                StaleEffectIds = snapshot.Effects.Where(e => e.RequiresReassessment).Select(e => e.EffectId).ToArray()
            });
        }
        var facts = companies.SelectMany(c => c.Receipt?.Effects.Select(e =>
        {
            var stale = c.StaleEffectIds?.Contains(e.EffectId, StringComparer.Ordinal) == true;
            var revision = e.CurrentRevision with
            {
                ModelExplanation = null
            };
            return new CanonicalFact { CompanyId = c.CompanyId, EffectId = e.EffectId, Revision = revision, Priority = stale ? 0 : ProjectionPolicy.Priority(revision), RequiresReassessment = stale };
        }) ?? []).OrderByDescending(f => f.Priority).ToArray();
        return new(new CanonicalProjection { Companies = companies.ToArray(), Facts = facts }, null);
    }

    public async Task<Result<GroupProjectionResponse>> GroupsAsync(ProjectionRequest request, CancellationToken ct)
    {
        var canonical = await CanonicalAsync(request);
        if (canonical.Error is { } error)
        {
            return new(error);
        }

        var groups = await ResolveGroupsAsync(request.Groups ?? [], request.AsOf ?? request.Companies.Select(c => c.Receipt?.AsOf ?? DateTimeOffset.MinValue).DefaultIfEmpty(DateTimeOffset.UtcNow).Max(), ct);
        if (groups.Error is { } groupError)
        {
            return new(groupError);
        }

        return new(new GroupProjectionResponse { Groups = groups.Content!.Select(g => ProjectionPolicy.Group(g, canonical.Content!.Companies)).ToArray() }, null);
    }

    public async Task<Result<RoleProjectionResponse>> RoleAsync(RoleProjectionRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.PersonId) || request.PersonId.Length > 200 || request.AsOf == default || request.CompanyId == Guid.Empty)
        {
            return new(InvalidRequest());
        }
        var scope = await company.RoleAsync(request.PersonId, request.CompanyId, request.AsOf, ct);
        if (scope.Error is { } scopeError)
        {
            return new(scopeError);
        }

        var role = scope.Content!;
        if (role.PersonId != request.PersonId || role.CompanyId != request.CompanyId || role.KnownAt > request.AsOf ||
            role.AllowedCompanyIds is null || role.AllowedMaterialIds is null || role.Responsibilities is null || role.SourceRefs is null)
        {
            return new(new Error("CompanyService returned an invalid role scope.", ErrorSource.External, ErrorType.Undefined) { HttpStatusCode = HttpStatusCode.BadGateway });
        }
        var canonical = await CanonicalAsync(new() { Companies = request.Companies, Groups = request.Groups, AsOf = request.AsOf });
        if (canonical.Error is { } error)
        {
            return new(error);
        }

        var allowed = role.AllowedCompanyIds.ToHashSet();
        var cache = new Dictionary<(string, string), Contracts.Context.MaterialVersion>();
        async Task<Result<bool>> CanReadAsync(Contracts.Context.SourceRef[] refs)
        {
            foreach (var reference in refs)
            {
                if (!cache.TryGetValue((reference.MaterialId, reference.VersionId), out var material))
                {
                    var resolved = await sources.GetAsync(reference.MaterialId, reference.VersionId, ct);
                    if (resolved.Error is { } resolutionError)
                    {
                        return new(resolutionError);
                    }

                    material = resolved.Content!;
                    cache[(reference.MaterialId, reference.VersionId)] = material;
                }
                if (material.MaterialId != reference.MaterialId || material.VersionId != reference.VersionId ||
                    material.ContentHash != reference.ContentHash || material.KnownAt > request.AsOf ||
                    material.CompanyIds.Length > 0 &&
                    (!material.CompanyIds.All(allowed.Contains) || !role.AllowedMaterialIds.Contains(material.MaterialId, StringComparer.Ordinal)))
                {
                    return new(false, null);
                }
            }
            return new(true, null);
        }
        var eligible = canonical.Content!.Facts.Where(f => allowed.Contains(f.CompanyId) && !f.RequiresReassessment).ToArray();
        var visible = new List<CanonicalFact>();
        foreach (var fact in eligible)
        {
            var read = await CanReadAsync(fact.Revision.SourceRefs);
            if (read.Error is { } readError)
            {
                return new(readError);
            }

            if (read.Content)
            {
                visible.Add(fact);
            }
        }
        var expansions = await ResolveGroupsAsync(request.Groups ?? [], request.AsOf, ct);
        if (expansions.Error is { } expansionError)
        {
            return new(expansionError);
        }

        var visibleIds = visible.Select(f => f.EffectId).ToHashSet(StringComparer.Ordinal);
        var groups = new List<GroupProjection>();
        foreach (var group in expansions.Content!)
        {
            if (!group.CompanyIds.All(allowed.Contains))
            {
                continue;
            }

            var projection = ProjectionPolicy.Group(group, canonical.Content.Companies);
            if (projection.Coverage.UnrevisedEffectIds.Length > 0 || eligible.Any(f => group.CompanyIds.Contains(f.CompanyId) && !visibleIds.Contains(f.EffectId)))
            {
                continue;
            }

            var read = await CanReadAsync(group.SourceRefs);
            if (read.Error is { } readError)
            {
                return new(readError);
            }

            if (read.Content)
            {
                groups.Add(projection);
            }
        }
        return new(new RoleProjectionResponse { Profile = new() { PersonId = role.PersonId, CompanyId = role.CompanyId, Role = role.Role, Responsibilities = role.Responsibilities }, Facts = visible.ToArray(), Groups = groups.ToArray() }, null);
    }

    private async Task<Result<GroupExpansion[]>> ResolveGroupsAsync(
        GroupExpansion[] requested,
        DateTimeOffset asOf,
        CancellationToken ct)
    {
        if (requested.Length > 64 || requested.Any(g => string.IsNullOrWhiteSpace(g.GroupId) || g.GroupId.Length > 200))
        {
            return new(InvalidRequest());
        }

        var result = new List<GroupExpansion>();
        foreach (var input in requested.DistinctBy(g => g.GroupId))
        {
            var resolved = await company.GroupAsync(input.GroupId, asOf, ct);
            if (resolved.Error is { } error)
            {
                return new(error);
            }

            var group = resolved.Content!;
            if (group.GroupId != input.GroupId || group.CompanyIds is null || group.SourceRefs is null || group.MissingGroups is null || group.Cycles is null)
            {
                return new(InvalidRequest());
            }

            result.Add(group);
        }
        return new(result.ToArray(), null);
    }

    private static Error InvalidRequest() => new("A bounded company projection and valid asOf are required.", ErrorSource.Client, ErrorType.CorruptedInfo) { HttpStatusCode = HttpStatusCode.BadRequest };
}
