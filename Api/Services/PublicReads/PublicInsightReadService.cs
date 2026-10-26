using System.Collections.Concurrent;
using System.Text.Json;
using AlfaHackaton.Result;
using InsightService.Contracts.Analyses;
using InsightService.Contracts.Context;
using InsightService.Contracts.PublicReads;
using InsightService.Contracts.Reads;
using InsightService.Models.Policies;

namespace InsightService.Services.PublicReads;

public sealed class PublicInsightReadService(IPublicInsightMembershipResolver memberships, IPublicInsightSourceResolver sources,
    IPublicInsightReadStore store)
{
    private sealed class SourceDenied : Exception;
    private sealed class Request(IPublicInsightSourceResolver resolver, CancellationToken ct)
    {
        private readonly ConcurrentDictionary<(string Material, string Version), MaterialVersion> loaded = new();
        internal async Task Load(IEnumerable<SourceRef> references)
        {
            var pending = references.GroupBy(reference => (reference.MaterialId, reference.VersionId)).Select(group => group.Last())
                .Where(reference => !loaded.ContainsKey((reference.MaterialId, reference.VersionId))).ToArray();
            if (loaded.Count + pending.Length > 64)
            {
                throw PublicInsightReadException.Unavailable("SOURCE_READ_BUDGET");
            }

            for (var offset = 0; offset < pending.Length; offset += 4)
            {
                await Task.WhenAll(pending.Skip(offset).Take(4).Select(async reference =>
                {
                    ct.ThrowIfCancellationRequested();
                    var source = await resolver.GetAsync(reference.MaterialId, reference.VersionId, ct).WaitAsync(ct)
                        ?? throw PublicInsightReadException.Unavailable("SOURCE_UNAVAILABLE");
                    PublicInsightValidation.Source(source);
                    loaded[(reference.MaterialId, reference.VersionId)] = source;
                }));
            }
        }
        internal MaterialVersion Source(SourceRef reference, PublicInsightMembership membership, DateTimeOffset asOf, bool publicOnly = false)
        {
            if (!loaded.TryGetValue((reference.MaterialId, reference.VersionId), out var source) ||
                source.MaterialId != reference.MaterialId || source.VersionId != reference.VersionId || source.ContentHash != reference.ContentHash || source.KnownAt > asOf)
            {
                throw PublicInsightReadException.Contract("SOURCE_IDENTITY_MISMATCH");
            }

            var company = source.CompanyIds.Contains(membership.CompanyId) || source.Kind is "news" or "market" && source.CompanyIds.Length == 0;
            var bank = source.BankProfileIds.Length == 0 || membership.BankProfileId is not null && source.BankProfileIds.Contains(membership.BankProfileId, StringComparer.Ordinal);
            if (!company || !bank || publicOnly && (source.CompanyIds.Length != 0 || source.BankProfileIds.Length != 0 || source.Kind == "company"))
            {
                throw new SourceDenied();
            }

            return source;
        }
    }

    public Task<PublicInsightFeed> Feed(Guid userId, PublicInsightFeedQuery query, CancellationToken ct) => Run(async token =>
    {
        FeedQuery(query);
        var scope = await Scope(userId, token);
        var request = new Request(sources, token);
        var options = new InsightReadOptions
        {
            Limit = query.Limit,
            Cursor = query.Cursor,
            Status = query.Status,
            Mechanism = query.Mechanism,
            NewsMaterialId = query.NewsMaterialId,
            MinImportance = query.MinImportance,
            Category = query.Category
        };
        var cards = new List<PublicInsightCard>();
        string? cursor;
        if (query.Kind == "personal")
        {
            var page = await store.PersonalPage(scope.CompanyId, options, token).WaitAsync(token);
            Page(page, scope, query.Limit);
            foreach (var item in page.Items)
            {
                PersonalShape(item, scope, detail: false);
            }

            // Keep the underlying cursor: historical missing-data rows are internal
            // diagnostics, not client cards, even though their history remains intact.
            var visible = page.Items.Where(item => Publishable(item.CurrentRevision)).ToArray();
            await request.Load(visible.SelectMany(item => item.CurrentRevision.SourceRefs));
            foreach (var item in visible)
            {
                try
                { cards.Add(await Personal(item, scope, request)); }
                catch (SourceDenied) { } // Keep the underlying cursor even when every card is source-denied.
            }
            cursor = page.NextCursor;
        }
        else
        {
            var page = await store.GeneralPage(scope.CompanyId, options, token).WaitAsync(token);
            Page(page, scope, query.Limit);
            var parsed = page.Items.Select(item => (Item: item, Insight: GeneralShape(item, scope, detail: false))).ToArray();
            await request.Load(parsed.SelectMany(value => value.Insight.SourceRefs.Select(InternalRef)));
            foreach (var (item, insight) in parsed)
            {
                try
                { cards.Add(General(item, insight, scope, request)); }
                catch (SourceDenied) { }
            }
            cursor = page.NextCursor;
        }
        return new PublicInsightFeed(scope.CompanyId, cards, cursor);
    }, ct);

    public Task<PublicInsightCard> Detail(Guid userId, string kind, string id, CancellationToken ct) => Run<PublicInsightCard>(async token =>
    {
        Kind(kind);
        Id(id);
        var scope = await Scope(userId, token);
        var request = new Request(sources, token);
        try
        {
            if (kind == "personal")
            {
                var item = await store.PersonalDetail(scope.CompanyId, id, token).WaitAsync(token) ?? throw PublicInsightReadException.Missing();
                PersonalShape(item, scope, detail: true, id);
                if (!Publishable(item.CurrentRevision))
                {
                    throw PublicInsightReadException.Missing();
                }
                await request.Load(item.CurrentRevision.SourceRefs);
                return await Personal(item, scope, request);
            }
            var general = await store.GeneralDetail(scope.CompanyId, id, token).WaitAsync(token) ?? throw PublicInsightReadException.Missing();
            var insight = GeneralShape(general, scope, detail: true, id);
            await request.Load(insight.SourceRefs.Select(InternalRef));
            return General(general, insight, scope, request);
        }
        catch (SourceDenied) { throw PublicInsightReadException.Missing(); }
    }, ct);

    public Task<PublicInsightHistory> History(Guid userId, string id, PublicInsightHistoryQuery query, CancellationToken ct) => Run(async token =>
    {
        Id(id);
        if (query is null || query.Limit is < 1 or > 20 || query.Cursor?.Length > 2048)
        {
            throw PublicInsightReadException.Invalid();
        }

        var scope = await Scope(userId, token);
        var current = await store.PersonalDetail(scope.CompanyId, id, token).WaitAsync(token) ?? throw PublicInsightReadException.Missing();
        PersonalShape(current, scope, detail: true, id);
        var page = await store.PersonalHistory(scope.CompanyId, id, query.Limit, query.Cursor, token).WaitAsync(token) ?? throw PublicInsightReadException.Missing();
        Page(page, scope, query.Limit);
        foreach (var revision in page.Items)
        {
            PublicInsightValidation.Revision(revision);
        }

        var request = new Request(sources, token);
        await request.Load(current.CurrentRevision.SourceRefs.Concat(page.Items.SelectMany(revision => revision.SourceRefs)));
        try
        {
            await Personal(current, scope, request);
            var items = new List<PublicInsightHistoryItem>();
            foreach (var revision in page.Items)
            {
                var card = await Personal(current with { CurrentRevision = revision }, scope, request);
                items.Add(new(card.CurrentRevision, card.Sources, card.Indicators, card.IndicatorAvailability));
            }
            return new PublicInsightHistory(scope.CompanyId, id, items, page.NextCursor);
        }
        catch (SourceDenied) { throw PublicInsightReadException.Missing(); }
    }, ct);

    private async Task<PublicInsightMembership> Scope(Guid userId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var scope = await memberships.GetAsync(userId, ct).WaitAsync(ct);
        if (scope is null)
        {
            throw new PublicInsightReadException(403, "COMPANY_MEMBERSHIP_REQUIRED", "Current company membership is required.");
        }

        PublicInsightValidation.Require(scope.CompanyId != Guid.Empty, "INSIGHT_SCOPE_MISMATCH");
        return scope;
    }
    private static void PersonalShape(PersonalInsightRead? item, PublicInsightMembership scope, bool detail, string? id = null)
    {
        PublicInsightValidation.Require(item is not null && PublicInsightValidation.Text(item.Id, 512) && item.CompanyId != Guid.Empty &&
            PublicInsightValidation.Identifier(item.NewsMaterialId) && PublicInsightValidation.Text(item.LiteralObjectRef, 500, 0));
        PublicInsightValidation.Revision(item!.CurrentRevision);
        if (item.CompanyId != scope.CompanyId || id is not null && item.Id != id)
        {
            if (detail)
            {
                throw PublicInsightReadException.Missing();
            }

            throw PublicInsightReadException.Contract("INSIGHT_SCOPE_MISMATCH");
        }
    }
    private static PublicGeneralInsight GeneralShape(GeneralInsightRead? item, PublicInsightMembership scope, bool detail, string? id = null)
    {
        PublicInsightValidation.Require(item is not null && PublicInsightValidation.Text(item.Id, 512) && item.CompanyId != Guid.Empty &&
            PublicInsightValidation.Identifier(item.JobId) && item.InsightIndex is >= 0 and <= 23 && PublicInsightValidation.Identifier(item.NewsMaterialId) &&
            PublicInsightValidation.Identifier(item.NewsVersionId) && PublicInsightValidation.TryInstant(item.AsOf, out _) && PublicInsightValidation.TryInstant(item.UpdatedAt, out _) &&
            item.TextCompleteness is "full" or "lead" or "unknown" && item.CompanyStatus is "completed" or "failed" or "pending");
        var insight = PublicInsightValidation.General(item!.Insight);
        if (item.CompanyId != scope.CompanyId || id is not null && item.Id != id)
        {
            if (detail)
            {
                throw PublicInsightReadException.Missing();
            }

            throw PublicInsightReadException.Contract("INSIGHT_SCOPE_MISMATCH");
        }
        return insight;
    }
    private static void Page<T>(InsightReadPage<T>? page, PublicInsightMembership scope, int limit)
    {
        if (page is null)
        {
            throw PublicInsightReadException.Unavailable();
        }

        PublicInsightValidation.Require(page.CompanyId == scope.CompanyId, "INSIGHT_SCOPE_MISMATCH");
        PublicInsightValidation.Require(page.Items is not null && page.Items.Count <= limit && page.NextCursor?.Length is not > 2048);
    }
    private static bool Publishable(EffectRevision revision) =>
        revision.PublicationClass != "missing_data" && revision.Impact?.EvidenceStatus != "unknown" &&
        !(revision.Impact is null && revision.Status == "unknown");

    private static async Task<PublicPersonalInsightCard> Personal(PersonalInsightRead item, PublicInsightMembership scope, Request request)
    {
        var revision = item.CurrentRevision;
        var originals = revision.SourceRefs.Select(reference => request.Source(reference, scope, revision.AsOf)).ToArray();
        PublicInsightValidation.Require(originals.Length <= 64);
        var byIdentity = originals.GroupBy(source => (source.MaterialId, source.VersionId)).ToDictionary(group => group.Key, group => group.First());
        Task<Result<MaterialVersion>> Resolve(string material, string version) => Task.FromResult(new Result<MaterialVersion>(byIdentity[(material, version)], null));
        if ((await CompanyImportancePolicy.Validate(PublicInsightProjection.Draft(revision), scope.CompanyId, Resolve)).Error is not null)
        {
            throw PublicInsightReadException.Contract("IMPORTANCE_EVIDENCE_MISMATCH");
        }

        var indicators = await PublicInsightProjection.Indicators(revision, item.RequiresReassessment, originals, scope.CompanyId);
        return new(item.Id, scope.CompanyId, item.NewsMaterialId, item.LiteralObjectRef, PublicInsightProjection.Revision(revision), item.RequiresReassessment,
            originals.Select(PublicInsightProjection.Metadata).ToArray(), indicators.Items, indicators.Availability);
    }
    private static PublicGeneralInsightCard General(GeneralInsightRead item, PublicGeneralInsight insight, PublicInsightMembership scope, Request request)
    {
        PublicInsightValidation.TryInstant(item.AsOf, out var asOf);
        PublicInsightValidation.TryInstant(item.UpdatedAt, out var updatedAt);
        var originals = insight.SourceRefs.Select(reference => request.Source(InternalRef(reference), scope, asOf, publicOnly: true)).ToArray();
        if (!originals.Any(source => source.Kind == "news" && source.MaterialId == item.NewsMaterialId && source.VersionId == item.NewsVersionId))
        {
            throw PublicInsightReadException.Contract("GENERAL_NEWS_ANCHOR");
        }

        var indicators = PublicInsightProjection.Unavailable("general_not_company_calculation");
        return new(item.Id, scope.CompanyId, item.NewsMaterialId, item.NewsVersionId, asOf, updatedAt, item.TextCompleteness,
            item.CompanyStatus, insight, originals.Select(PublicInsightProjection.Metadata).ToArray(), indicators.Items, indicators.Availability);
    }
    private static SourceRef InternalRef(PublicInsightSourceRef reference) => new(reference.MaterialId, reference.VersionId, reference.ContentHash);
    private static void Id(string id)
    {
        if (!PublicInsightValidation.Text(id, 512))
        {
            throw PublicInsightReadException.Invalid();
        }
    }
    private static void Kind(string kind)
    {
        if (kind is not ("personal" or "general"))
        {
            throw PublicInsightReadException.Invalid();
        }
    }
    private static void FeedQuery(PublicInsightFeedQuery query)
    {
        if (query is null)
        {
            throw PublicInsightReadException.Invalid();
        }

        Kind(query.Kind);
        if (!InsightCategoryPolicy.IsValid(query.Category) || query.Limit is < 1 or > 20 || query.Cursor?.Length > 2048 || query.Status is not null && !PublicInsightValidation.Text(query.Status, 100) ||
            query.NewsMaterialId is not null && !PublicInsightValidation.Text(query.NewsMaterialId, 200) ||
            query.Mechanism is not null && !PublicInsightValidation.Mechanisms.Contains(query.Mechanism) || query.MinImportance is < 1 or > 5 ||
            query.Kind == "general" && (query.Status is not null || query.Mechanism is not null || query.MinImportance is not null))
        {
            throw PublicInsightReadException.Invalid();
        }
    }
    private static async Task<T> Run<T>(Func<CancellationToken, Task<T>> operation, CancellationToken ct)
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(TimeSpan.FromSeconds(15));
        try
        { return await operation(budget.Token).WaitAsync(budget.Token); }
        catch (PublicInsightReadException) { throw; }
        catch (BadHttpRequestException) { throw new PublicInsightReadException(400, "INVALID_CURSOR", "Invalid read cursor or filters."); }
        catch (JsonException) { throw PublicInsightReadException.Contract(); }
        catch (Exception error) when (error is not OutOfMemoryException) { throw PublicInsightReadException.Unavailable(); }
    }
}
