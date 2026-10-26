using System.Globalization;
using InsightService.Contracts.PublicReads;
using InsightService.Contracts.Reads;

namespace InsightService.Services.PublicReads;

public sealed record InsightUpdateEvent(string EventType, string Id, string Kind, string? RevisionId,
    string Version, DateTimeOffset Time);

public sealed record InsightUpdateHead(string Id, string Kind, string? RevisionId, string Version);

// Poll committed owner read models. No write hooks, receipts, checkpoints or source bodies
// leave this seam. Every changed candidate is authorized by the existing public detail read.
public sealed class InsightUpdateSnapshot(IPublicInsightMembershipResolver memberships,
    IPublicInsightReadStore store, PublicInsightReadService reads)
{
    public const int MaximumHeads = 1000;

    public async Task<PublicInsightMembership> Scope(Guid user, CancellationToken ct)
    {
        var scope = await memberships.GetAsync(user, ct).WaitAsync(ct);
        if (scope is null || scope.CompanyId == Guid.Empty)
        {
            throw new PublicInsightReadException(403, "COMPANY_MEMBERSHIP_REQUIRED", "Current company membership is required.");
        }
        return scope;
    }

    public async Task<IReadOnlyList<InsightUpdateHead>> Read(Guid user, PublicInsightMembership expected, CancellationToken ct)
    {
        await RequireScope(user, expected, ct);
        var heads = new Dictionary<(string Kind, string Id), InsightUpdateHead>();
        var count = 0;
        var pages = 0;
        foreach (var kind in new[] { "personal", "general" })
        {
            string? cursor = null;
            var cursors = new HashSet<string>(StringComparer.Ordinal);
            do
            {
                if (++pages > 100)
                { throw PublicInsightReadException.Unavailable("INSIGHT_UPDATE_RESYNC_REQUIRED"); }
                var options = new InsightReadOptions { Limit = 20, Cursor = cursor };
                if (kind == "personal")
                {
                    var page = await store.PersonalPage(expected.CompanyId, options, ct).WaitAsync(ct);
                    CheckPage(page.CompanyId, page.Items.Count, expected);
                    count += page.Items.Count;
                    foreach (var item in page.Items)
                    {
                        if (item.CompanyId != expected.CompanyId)
                        { throw PublicInsightReadException.Contract("INSIGHT_SCOPE_MISMATCH"); }
                        var revision = item.CurrentRevision;
                        if (Published(revision.PublicationClass, revision.Status, revision.Impact?.EvidenceStatus))
                        {
                            heads[(kind, item.Id)] = new(item.Id, kind, revision.RevisionId,
                                revision.RevisionId + (item.RequiresReassessment ? ":stale" : ":current"));
                        }
                    }
                    cursor = page.NextCursor;
                }
                else
                {
                    var page = await store.GeneralPage(expected.CompanyId, options, ct).WaitAsync(ct);
                    CheckPage(page.CompanyId, page.Items.Count, expected);
                    count += page.Items.Count;
                    foreach (var item in page.Items)
                    {
                        if (item.CompanyId != expected.CompanyId)
                        { throw PublicInsightReadException.Contract("INSIGHT_SCOPE_MISMATCH"); }
                        // A failed company execution is not a new card notification.
                        if (item.CompanyStatus == "completed")
                        {
                            heads[(kind, item.Id)] = new(item.Id, kind, null, item.UpdatedAt);
                        }
                    }
                    cursor = page.NextCursor;
                }
                if (count > MaximumHeads || cursor is not null && !cursors.Add(cursor))
                {
                    throw PublicInsightReadException.Unavailable("INSIGHT_UPDATE_RESYNC_REQUIRED");
                }
            } while (cursor is not null);
        }
        await RequireScope(user, expected, ct);
        return heads.Values.OrderBy(head => head.Kind, StringComparer.Ordinal)
            .ThenBy(head => head.Id, StringComparer.Ordinal).ToArray();
    }

    public async Task<InsightUpdateEvent?> Authorize(Guid user, PublicInsightMembership expected, InsightUpdateHead head, CancellationToken ct)
    {
        await RequireScope(user, expected, ct);
        PublicInsightCard card;
        try
        { card = await reads.Detail(user, head.Kind, head.Id, ct); }
        catch (PublicInsightReadException error) when (error.StatusCode == 404) { return null; }
        if (card.CompanyId != expected.CompanyId)
        { throw PublicInsightReadException.Contract("INSIGHT_SCOPE_MISMATCH"); }
        InsightUpdateHead? current = card switch
        {
            PublicPersonalInsightCard personal when Published(personal.CurrentRevision.PublicationClass, personal.CurrentRevision.Status, personal.CurrentRevision.Impact?.EvidenceStatus) =>
                new(personal.Id, personal.Kind, personal.CurrentRevision.RevisionId,
                    personal.CurrentRevision.RevisionId + (personal.RequiresReassessment ? ":stale" : ":current")),
            PublicGeneralInsightCard general when general.CompanyStatus == "completed" =>
                new(general.Id, general.Kind, null, general.UpdatedAt.ToString("O", CultureInfo.InvariantCulture)),
            _ => null
        };
        await RequireScope(user, expected, ct);
        // A concurrent commit is retried by the next full poll, never announced under an old version.
        if (current is null || current.RevisionId != head.RevisionId ||
            (head.Kind == "personal" ? current.Version != head.Version :
                !DateTimeOffset.TryParse(head.Version, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var time) ||
                time != ((PublicGeneralInsightCard)card).UpdatedAt))
        { return null; }
        return new("insight.upsert", head.Id, head.Kind, head.RevisionId, head.Version, DateTimeOffset.UtcNow);
    }

    private async Task RequireScope(Guid user, PublicInsightMembership expected, CancellationToken ct)
    {
        if (await Scope(user, ct) != expected)
        {
            throw new PublicInsightReadException(403, "COMPANY_MEMBERSHIP_CHANGED", "Reconnect using current company membership.");
        }
    }

    // Matches the no-publication read boundary from the independent narrative work:
    // diagnostic missing-data/unknown heads are never events, including legacy rows.
    private static bool Published(string? publication, string status, string? evidence) =>
        (publication is null or "consequence" or "scenario" or "unaffected" or "review") &&
        status is not ("no_insight" or "internal_failed" or "failed" or "pending") &&
        evidence != "unknown" && !(evidence is null && status == "unknown");

    private static void CheckPage(Guid company, int count, PublicInsightMembership expected)
    {
        if (company != expected.CompanyId || count > 20)
        {
            throw PublicInsightReadException.Contract("INSIGHT_SCOPE_MISMATCH");
        }
    }
}
