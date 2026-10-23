using System.Net;
using AlfaHackaton.Result;
using AlfaHackaton.Result.Errors;
using InsightService.Contracts.Analyses;
using InsightService.Models.Policies;
using InsightService.Repositories.Interfaces;
using InsightService.Services.Interfaces;

namespace InsightService.Services;

public sealed class AnalysisPreparationService(IEffectRepository repository, IAnalysisService analyses, ISourceResolver sources,
    ContextEvidenceClient evidence, CompanyKnowledgeClient company)
{
    private static readonly string[] MissingExposure = ["В загруженном корпусе не установлены собственные договорные, имущественные или операционные основания применимости. Это не доказывает отсутствия обязательств."];
    public async Task<Result<object>> ValidateApplicability(ApplicabilityValidation request, CancellationToken ct)
    {
        if (request.CompanyId == Guid.Empty || request.AsOf == default || request.Candidates is null || request.Candidates.Length > 32)
        {
            return new(Invalid());
        }
        var cache = new Dictionary<(string, string), Contracts.Context.MaterialVersion>();
        async Task<Result<Contracts.Context.MaterialVersion>> Material(string materialId, string versionId)
        {
            if (!cache.TryGetValue((materialId, versionId), out var material))
            {
                if (cache.Count >= 128)
                {
                    return new(Invalid());
                }
                var resolved = await sources.GetAsync(materialId, versionId, ct);
                if (resolved.Error is { } error)
                {
                    return new(error);
                }
                material = resolved.Content!;
                cache[(materialId, versionId)] = material;
            }
            if (material.MaterialId != materialId || material.VersionId != versionId || material.KnownAt > request.AsOf ||
                material.CompanyIds.Length > 0 && !material.CompanyIds.Contains(request.CompanyId))
            {
                return new(Invalid());
            }
            return new(material, null);
        }
        foreach (var candidate in request.Candidates)
        {
            if (candidate is null || candidate.Assessment is { Conditions: null } or { Unknowns: null } ||
                candidate.Assessment?.Conditions.Length > 16 || candidate.Assessment?.Unknowns.Length > 16 ||
                candidate.EvidenceStatus is not (null or "supported" or "conditional" or "unknown" or "unaffected" or "contradicted"))
            {
                return new(Invalid());
            }
            var references = (candidate.Assessment?.Applicability?.SourceRefs ?? [])
                .Concat(candidate.Assessment?.Conditions.SelectMany(c => c.SourceRefs ?? []) ?? []).Distinct().ToArray();
            if (references.Length > 64 || references.Any(r => r is null))
            {
                return new(Invalid());
            }
            foreach (var reference in references)
            {
                var resolved = await Material(reference.MaterialId, reference.VersionId);
                if (resolved.Error is { } error)
                {
                    return new(error);
                }
                if (resolved.Content!.ContentHash != reference.ContentHash)
                {
                    return new(Invalid());
                }
            }
            var draft = new EffectDraft("candidate", "candidate", null, "candidate", "candidate", references,
                candidate.HasCalculation ? new(new("value", "0"), "unknown", "unknown") : null,
                new("unknown", "unknown", null, candidate.EvidenceStatus ?? "unknown", 0, 0, 0),
                new("liquidity", "event_only", "qualitative", "unknown", null, "qualitative"), candidate.Assessment);
            var result = await ApplicabilityPolicy.Validate(draft, request.CompanyId, Material);
            if (result.Error is { } validationError)
            {
                return new(validationError);
            }
        }
        return new(new
        {
            valid = true
        }, null);
    }
    public async Task<Result<AnalysisReceipt>> CanonicalCompany(AnalysisScope request)
    {
        if (!Valid(request))
        {
            return new(Invalid());
        }

        var snapshot = await repository.GetSnapshot(request.CompanyId, request.NewsMaterialId, request.AsOf);
        var latest = snapshot.Effects.SelectMany(e => e.History.Where(r => r.AsOf <= request.AsOf)
            .TakeLast(1).Select(r => (Effect: e, Revision: r))).OrderByDescending(x => x.Revision.CreatedAt).FirstOrDefault();
        if (latest.Revision is null)
        {
            return new(new Error("No source-supported company state exists for this projection.", ErrorSource.Client, ErrorType.CorruptedInfo) { HttpStatusCode = HttpStatusCode.UnprocessableEntity });
        }

        var stored = await repository.FindAnalysis(request.CompanyId, latest.Revision.NewsMaterialId ?? latest.Effect.NewsMaterialId, latest.Revision.NewsVersionId, latest.Revision.Trigger);
        if (stored is null)
        {
            return new(new Error("The canonical head has no processing receipt.", ErrorSource.External, ErrorType.Undefined) { HttpStatusCode = HttpStatusCode.BadGateway });
        }

        return new(stored.Receipt, null);
    }

    public async Task<Result<AnalysisReceipt>> Applicability(AnalysisScope request, CancellationToken ct)
    {
        if (!Valid(request))
        {
            return new(Invalid());
        }

        var prior = await repository.FindAnalysis(request.CompanyId, request.NewsMaterialId, request.NewsVersionId, request.Trigger);
        if (prior is not null)
        {
            var receipt = prior.Receipt;
            if (receipt.AsOf != request.AsOf || receipt.BankProfileId != request.BankProfileId)
            {
                return new(Conflict("The existing receipt has a different scope."));
            }

            if (receipt.Effects.Count > 0)
            {
                return new(Conflict("Existing canonical effects require deep assessment."));
            }

            return new(AnalysisService.Duplicate(receipt) with
            {
                ContextComplete = false
            }, null);
        }
        var before = await repository.GetSnapshot(request.CompanyId, request.NewsMaterialId, request.AsOf);
        if (before.Effects.Any(e => Relevant(e, request.Mechanisms)))
        {
            return new(Conflict("Canonical state changed since the depth decision; deep assessment is required."));
        }

        return await analyses.Save(new(request.CompanyId, request.BankProfileId, request.NewsMaterialId, request.NewsVersionId, request.AsOf, [],
            ContextComplete: false, Trigger: request.Trigger, ExpectedStateVersion: before.StateVersion, Mechanisms: request.Mechanisms), ct);
    }

    public async Task<Result<EffectDraft[]>> Prepare(DraftPreparation input)
    {
        var request = input.Request;
        if (request is null || request.CompanyId == Guid.Empty || request.AsOf == default || string.IsNullOrWhiteSpace(request.NewsMaterialId) || input.Drafts is null || input.Drafts.Length > 32)
        {
            return new(Invalid());
        }

        var snapshot = await repository.GetSnapshot(request.CompanyId, request.NewsMaterialId, request.AsOf);
        var prior = new Dictionary<string, EffectResponse>(StringComparer.Ordinal);
        foreach (var effect in snapshot.Effects.Where(e => Relevant(e, request.Mechanisms)))
        {
            var key = effect.EffectKey;
            if (effect.CurrentRevision.Question is { } question)
            {
                var normalizedKey = QuestionIdentityPolicy.QuestionKey(effect.LiteralObjectRef, question);
                if (normalizedKey.Error is { } error)
                {
                    return new(error);
                }

                key = normalizedKey.Content!;
            }
            if (!prior.TryAdd(key, effect))
            {
                return new(Conflict("Multiple stored histories require explicit reconciliation."));
            }
        }
        var drafts = new List<EffectDraft>();
        foreach (var draft in input.Drafts)
        {
            var prepared = draft;
            if (draft.Question is { } question)
            {
                question = QuestionIdentityPolicy.NormalizeQuestion(question);
                var key = QuestionIdentityPolicy.QuestionKey(draft.LiteralObjectRef, question);
                if (key.Error is { } error)
                {
                    return new(error);
                }

                if (request.Mechanisms?.Length > 0 && !request.Mechanisms.Contains(question.Mechanism, StringComparer.Ordinal))
                {
                    return new(Invalid());
                }

                prepared = draft with
                {
                    Question = question,
                    EffectKey = key.Content!
                };
            }
            prior.TryGetValue(prepared.EffectKey, out var existing);
            if (existing is not null && existing.LiteralObjectRef != prepared.LiteralObjectRef)
            {
                return new(Invalid());
            }

            drafts.Add(prepared with
            {
                ExpectedPreviousRevisionId = existing?.CurrentRevision.RevisionId
            });
        }
        return new(drafts.ToArray(), null);
    }

    public async Task<Result<object>> Depth(DepthRequest input, CancellationToken ct)
    {
        var request = input.Request with
        {
            CompanyId = input.CompanyId
        };
        if (!Valid(request))
        {
            return new(Invalid());
        }
        var inventoryResult = await evidence.Inventory(input.CompanyId, request.AsOf, ct);
        if (inventoryResult.Error is { } inventoryError)
        {
            return new(inventoryError);
        }
        var inventory = inventoryResult.Content!;
        if (inventory.CompanyId != input.CompanyId || inventory.AsOf != request.AsOf || inventory.Sources is null || inventory.Sources.Length > 128 || inventory.Sources.Any(s => s.KnownAt > request.AsOf))
        {
            return new(Invalid());
        }

        var own = inventory.Sources.Where(s => s.SourceId != "provided-bank-csv").ToArray();
        var irrelevant = own.Length > 0 && input.Relevance?.Length == own.Length && own.All(s => input.Relevance.Any(r => r.SourceRef == new Contracts.Context.SourceRef(s.MaterialId, s.VersionId, s.ContentHash) && r.Status == "unrelated"));
        if (irrelevant)
        {
            foreach (var source in own)
            {
                var resolved = await sources.GetAsync(source.MaterialId, source.VersionId, ct);
                if (resolved.Error is { } error)
                {
                    return new(error);
                }

                var material = resolved.Content!;
                var assessment = input.Relevance!.Single(r => r.SourceRef == new Contracts.Context.SourceRef(source.MaterialId, source.VersionId, source.ContentHash));
                if (material.ContentHash != source.ContentHash || material.Kind != "company" || !material.CompanyIds.Contains(input.CompanyId) || material.KnownAt > request.AsOf || string.IsNullOrEmpty(assessment.Quote) || !material.Text.Contains(assessment.Quote, StringComparison.Ordinal))
                {
                    return new(Invalid());
                }
            }
        }
        var snapshot = await repository.GetSnapshot(input.CompanyId, request.NewsMaterialId, request.AsOf);
        var mechanisms = input.Plan.TryGetProperty("mechanisms", out var mechanismElement) ? mechanismElement.EnumerateArray().Select(x => x.GetString()!).ToArray() : null;
        var profile = await company.Profile(input.CompanyId, request.AsOf, ct);
        if (profile.Error is { } profileError && profileError.HttpStatusCode != HttpStatusCode.NotFound)
        {
            return new(profileError);
        }
        if (profile.Content is { } identity && (identity.CompanyId != input.CompanyId || identity.KnownAt == default || identity.KnownAt > request.AsOf))
        {
            return new(new Error("CompanyService returned an invalid profile scope.", ErrorSource.External, ErrorType.Undefined) { HttpStatusCode = HttpStatusCode.BadGateway });
        }
        var reason = request.ProjectionOnly ? "projection_refresh" : request.Trigger is not null ? "source_revision" : input.NewsCorrection ? "news_correction" :
            snapshot.Effects.Any(e => Relevant(e, mechanisms)) ? "prior_canonical_state" : !inventory.CompleteForLoadedScope ? "incomplete_inventory" : own.Length > 0 ? irrelevant ? "unrelated_evidence" : "own_originals" : profile.Content is not null || inventory.Sources.Length > 0 ? "profile_only" : "no_own_evidence";
        var level = reason == "projection_refresh" ? "canonical_projection" : reason is "profile_only" or "unrelated_evidence" ? "applicability" : reason == "no_own_evidence" ? "general" : "company_deep";
        return new(new DepthDecisionResponse("evidence-depth-v1", level, reason, own.Length, inventory.CompleteForLoadedScope,
            inventory.Sources.Select(s => new Contracts.Context.SourceRef(s.MaterialId, s.VersionId, s.ContentHash)).ToArray(),
            input.Plan.TryGetProperty("generalInsights", out var insights) ? Enumerable.Range(0, insights.GetArrayLength()).ToArray() : [],
            "unknown", level is "general" or "applicability" ? MissingExposure : [], input.Relevance), null);
    }

    private static bool Relevant(EffectResponse effect, string[]? mechanisms) => mechanisms is not { Length: > 0 } || effect.CurrentRevision.Question is null || mechanisms.Contains(effect.CurrentRevision.Question.Mechanism, StringComparer.Ordinal);
    private static bool Valid(AnalysisScope? request) => request is not null && request.CompanyId != Guid.Empty && request.AsOf != default && !string.IsNullOrWhiteSpace(request.NewsMaterialId) && !string.IsNullOrWhiteSpace(request.NewsVersionId);
    private static Error Invalid() => new("The analysis preparation scope is invalid.", ErrorSource.Client, ErrorType.CorruptedInfo) { HttpStatusCode = HttpStatusCode.BadRequest };
    private static Error Conflict(string message) => new(message, ErrorSource.Client, ErrorType.CorruptedInfo) { HttpStatusCode = HttpStatusCode.Conflict };
}
