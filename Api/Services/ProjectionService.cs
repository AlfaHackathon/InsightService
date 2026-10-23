using System.Net;
using AlfaHackaton.Result;
using AlfaHackaton.Result.Errors;
using InsightService.Contracts.Projections;
using InsightService.Models.Policies;
using InsightService.Repositories.Interfaces;
using InsightService.Services.Interfaces;

namespace InsightService.Services;

public sealed class ProjectionService(IEffectRepository repository, CompanyKnowledgeClient company, ISourceResolver sources)
{
    public async Task<Result<CanonicalProjection>> Canonical(ProjectionRequest request)
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
                companies.Add(new(run.CompanyId, "failed", Error: "NO_CANONICAL_STATE"));
                continue;
            }
            var asOf = request.AsOf ?? identity.AsOf;
            var stored = await repository.FindAnalysis(run.CompanyId, identity.NewsMaterialId, identity.NewsVersionId, identity.Trigger);
            if (stored is null || stored.Receipt.AsOf > asOf)
            {
                companies.Add(new(run.CompanyId, "failed", Error: "NO_CANONICAL_STATE"));
                continue;
            }
            var receipt = stored.Receipt;
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
            companies.Add(new(run.CompanyId, "completed", receipt with
            {
                Effects = heads.Values.ToArray()
            },
                ContextComplete: receipt.ContextComplete, StaleEffectIds: snapshot.Effects.Where(e => e.RequiresReassessment).Select(e => e.EffectId).ToArray()));
        }
        var facts = companies.SelectMany(c => c.Receipt?.Effects.Select(e =>
        {
            var stale = c.StaleEffectIds?.Contains(e.EffectId, StringComparer.Ordinal) == true;
            var revision = e.CurrentRevision with
            {
                ModelExplanation = null
            };
            if (revision.Calculation is { } calculation && revision.CalculationResult is { } value)
            {
                revision = revision with
                {
                    Conclusion = QuestionIdentityPolicy.CalculatedConclusion(revision.Question?.Frame ?? "absolute", value, calculation.Unit, calculation.Period, "")
                };
            }
            return new CanonicalFact(c.CompanyId, e.EffectId, revision, stale ? 0 : ProjectionPolicy.Priority(revision), stale);
        }) ?? []).OrderByDescending(f => f.Priority).ToArray();
        return new(new CanonicalProjection(companies.ToArray(), facts), null);
    }

    public async Task<Result<GroupProjectionResponse>> Groups(ProjectionRequest request, CancellationToken ct)
    {
        var canonical = await Canonical(request);
        if (canonical.Error is { } error)
        {
            return new(error);
        }

        var groups = await ResolveGroups(request.Groups ?? [], request.AsOf ?? request.Companies.Select(c => c.Receipt?.AsOf ?? DateTimeOffset.MinValue).DefaultIfEmpty(DateTimeOffset.UtcNow).Max(), ct);
        if (groups.Error is { } groupError)
        {
            return new(groupError);
        }

        return new(new GroupProjectionResponse(groups.Content!.Select(g => ProjectionPolicy.Group(g, canonical.Content!.Companies)).ToArray()), null);
    }

    public async Task<Result<RoleProjectionResponse>> Role(RoleProjectionRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.PersonId) || request.PersonId.Length > 200 || request.AsOf == default || request.CompanyId == Guid.Empty)
        {
            return new(InvalidRequest());
        }
        var scope = await company.Role(request.PersonId, request.CompanyId, request.AsOf, ct);
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
        var canonical = await Canonical(new(request.Companies, request.Groups, request.AsOf));
        if (canonical.Error is { } error)
        {
            return new(error);
        }

        var allowed = role.AllowedCompanyIds.ToHashSet();
        var cache = new Dictionary<(string, string), Contracts.Context.MaterialVersion>();
        async Task<Result<bool>> CanRead(Contracts.Context.SourceRef[] refs)
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
            var read = await CanRead(fact.Revision.SourceRefs);
            if (read.Error is { } readError)
            {
                return new(readError);
            }

            if (read.Content)
            {
                visible.Add(fact);
            }
        }
        var expansions = await ResolveGroups(request.Groups ?? [], request.AsOf, ct);
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

            var read = await CanRead(group.SourceRefs);
            if (read.Error is { } readError)
            {
                return new(readError);
            }

            if (read.Content)
            {
                groups.Add(projection);
            }
        }
        return new(new RoleProjectionResponse(new(role.PersonId, role.CompanyId, role.Role, role.Responsibilities), visible.ToArray(), groups.ToArray()), null);
    }

    private async Task<Result<GroupExpansion[]>> ResolveGroups(GroupExpansion[] requested, DateTimeOffset asOf, CancellationToken ct)
    {
        if (requested.Length > 64 || requested.Any(g => string.IsNullOrWhiteSpace(g.GroupId) || g.GroupId.Length > 200))
        {
            return new(InvalidRequest());
        }

        var result = new List<GroupExpansion>();
        foreach (var input in requested.DistinctBy(g => g.GroupId))
        {
            var resolved = await company.Group(input.GroupId, asOf, ct);
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
